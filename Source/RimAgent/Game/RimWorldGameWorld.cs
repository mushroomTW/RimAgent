using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimAgent.Game
{
    /// <summary>
    /// <see cref="IGameWorld"/> 的正式實作：對 Find.CurrentMap 的薄轉接層。
    /// 只做 RimWorld API 呼叫與純資料轉換，不含決策邏輯；假設呼叫者已在 Unity 主線程
    /// （由框架的 AsMainThreadFunctionInvokingClient 保證）。這一層只能在遊戲內驗證。
    /// </summary>
    internal sealed class RimWorldGameWorld : IGameWorld
    {
        private const string MineDefName = "Mine";
        private const string ColonistNotFound = "colonist not found";

        private IReadOnlyList<DefInfo> _workTypes;
        private IReadOnlyList<DefInfo> _buildables;
        private IReadOnlyList<DefInfo> _stuffs;
        private IReadOnlyList<DefInfo> _timeAssignments;
        private IReadOnlyList<DefInfo> _tradeGoods;
        private IReadOnlyList<DefInfo> _medicalCareLevels;
        private IReadOnlyList<DefInfo> _surgeryRecipes;
        private IReadOnlyList<DefInfo> _prisonerInteractionModes;
        private IReadOnlyList<DefInfo> _animalTraining;
        private IReadOnlyList<DefInfo> _storageFilterables;
        private IReadOnlyList<DefInfo> _growablePlants;

        private static Map Map => Find.CurrentMap;

        public ColonyOverview GetOverview()
        {
            Map map = Map;
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            return new ColonyOverview
            {
                ColonyName = map.Parent?.Label ?? Faction.OfPlayer?.Name ?? string.Empty,
                ColonistCount = colonists.Count,
                Year = GenLocalDate.Year(map),
                Season = GenLocalDate.Season(map).ToString(),
                DayOfSeason = GenLocalDate.DayOfSeason(map),
                Hour = GenLocalDate.HourInteger(map),
                Weather = map.weatherManager.curWeather?.label ?? string.Empty,
                OutdoorTemperatureC = map.mapTemperature.OutdoorTemp,
                Wealth = map.wealthWatcher.WealthTotal,
                HostileCount = CountHostiles(map),
                ColonistsNearMentalBreak = colonists.Count(IsNearMentalBreak)
            };
        }

        private struct ColonistSignalsAccumulator
        {
            public int ColonistCount;
            public int NearMentalBreak;
            public int LowMood;
            public int DownedOrBleeding;
            public int ColdColonists;
            public int Bedless;
            public int OutdoorsBed;

            public void AccumulateColonists(List<Pawn> colonists)
            {
                if (colonists == null) return;
                for (int p = 0; p < colonists.Count; p++)
                {
                    AccumulateColonist(colonists[p]);
                }
            }

            public void AccumulateColonist(Pawn pawn)
            {
                // 每 500 tick 跑一次，外層抓到例外就整次作廢：其他模組的 pawn 一丟例外自主模式就停擺。
                // 逐個隔離，全部讀完才一起計入，壞掉的那一位整個不算，不留半套計數。
                bool nearBreak;
                bool low;
                bool downed;
                bool cold;
                bool noBed;
                bool bedOutdoors;
                try
                {
                    // 兩個心情門檻不疊加：快崩潰的算崩潰事件，不然同一件事報兩次。
                    nearBreak = IsNearMentalBreak(pawn);
                    low = !nearBreak && IsLowMood(pawn);
                    downed = pawn.Downed || HasBleeding(pawn);
                    cold = IsInDangerousTemperature(pawn);
                    Building_Bed bed = pawn.ownership?.OwnedBed;
                    noBed = bed == null;
                    bedOutdoors = !noBed && IsOutdoors(bed.GetRoom());
                }
                catch (Exception ex)
                {
                    WarnModError(pawn, "autonomous signals", ex);
                    return;
                }
                ColonistCount++;
                if (nearBreak) NearMentalBreak++;
                if (low) LowMood++;
                if (downed) DownedOrBleeding++;
                if (cold) ColdColonists++;
                if (noBed) Bedless++;
                if (bedOutdoors) OutdoorsBed++;
            }

            private static bool HasBleeding(Pawn pawn)
            {
                return pawn.health?.hediffSet?.BleedRateTotal > 0f;
            }

            private static bool IsInDangerousTemperature(Pawn pawn)
            {
                if (pawn.health?.hediffSet == null) return false;
                return !pawn.SafeTemperatureRange().Includes(pawn.AmbientTemperature);
            }

            private static bool IsOutdoors(Room room)
            {
                return room == null || room.PsychologicallyOutdoors;
            }

            private static bool IsLowMood(Pawn pawn)
            {
                Need_Mood mood = pawn.needs?.mood;
                return mood != null && mood.CurLevel < Agent.AutonomousScheduler.LowMoodThreshold;
            }
        }

        /// <summary>
        /// 自主排程器每 500 tick 要的數字，不建完整快照（GetOverview／GetResources 會為每個威脅與資源配置物件）。
        /// 除了應急訊號，也收「基地狀況」（受寒、沒床、露宿、停電、心情、停研），
        /// 讓排程器自己看出需要保養，不必為了問一句「沒事嗎」而燒一次模型呼叫。
        /// </summary>
        internal Agent.AutonomousSignals GetAutonomousSignals()
        {
            List<Map> maps = Find.Maps;
            int hostileCount = 0;
            float foodNutrition = 0f;
            int fireCount = 0;
            bool anyPower = false;
            var accumulator = new ColonistSignalsAccumulator();

            if (maps != null)
            {
                for (int m = 0; m < maps.Count; m++)
                {
                    AccumulateMapSignals(maps[m], ref hostileCount, ref foodNutrition, ref fireCount, ref anyPower, ref accumulator);
                }
            }

            return new Agent.AutonomousSignals
            {
                HostileCount = hostileCount,
                NearMentalBreakCount = accumulator.NearMentalBreak,
                FoodNutrition = foodNutrition,
                ColonistCount = accumulator.ColonistCount,
                DownedOrBleedingCount = accumulator.DownedOrBleeding,
                FireCount = fireCount,
                ColdColonistCount = accumulator.ColdColonists,
                BedlessCount = accumulator.Bedless,
                OutdoorsBedCount = accumulator.OutdoorsBed,
                LowMoodCount = accumulator.LowMood,
                // 1.6 的 ResearchManager 同時只有一個 currentProj，沒有 activeProjects 清單。
                HasActiveResearch = Find.ResearchManager != null && Find.ResearchManager.GetProject() != null,
                AnyWorkingPower = anyPower
            };
        }

        private static void AccumulateMapSignals(Map map, ref int hostileCount, ref float foodNutrition, ref int fireCount, ref bool anyPower, ref ColonistSignalsAccumulator accumulator)
        {
            if (map == null) return;
            // 地圖層的讀數不包 try：讀一半就把食物、電力當 0 會造成假警報，寧可整次作廢。
            // 會跑到其他模組程式的攻擊目標已在 CountHostiles 裡逐個隔離。
            hostileCount += CountHostiles(map);
            foodNutrition += map.resourceCounter.TotalHumanEdibleNutrition;
            fireCount += map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            if (!anyPower && HasWorkingPower(map)) anyPower = true;
            accumulator.AccumulateColonists(map.mapPawns.FreeColonistsSpawned);
        }

        /// <summary>有東西真的通電在跑（不只是接了發電機）。</summary>
        private static bool HasWorkingPower(Map map)
        {
            PowerNetManager manager = map.powerNetManager;
            if (manager == null) return false;
            IReadOnlyList<PowerNet> nets = manager.AllNetsListForReading;
            for (int i = 0; i < nets.Count; i++)
            {
                List<CompPowerTrader> comps = nets[i].powerComps;
                for (int c = 0; c < comps.Count; c++)
                {
                    if (comps[c].PowerOn) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 構成實際威脅的敵對 pawn 數，沿用原版判定（攻擊目標快取＋IsActiveThreatTo）。
        /// 倒地、休眠中的機兵群、迷霧裡、逃跑中的都不算：留著會讓「襲擊結束」的邊緣永遠不歸零，
        /// 下一波真襲擊就觸發不了。守巢的蟲族照算（蟲災本身就是襲擊）。火與敵方砲塔也不算。
        /// </summary>
        public int GetHostileCount() => CountHostiles(Map);

        private static int CountHostiles(Map map)
        {
            int count = 0;
            foreach (IAttackTarget target in map.attackTargetsCache.TargetsHostileToColony)
            {
                if (!(target.Thing is Pawn pawn)) continue;
                // IsActiveThreatTo 會跑到模組 pawn 的 Lord／心智狀態；一個丟例外只跳過它，其餘照數。
                try
                {
                    if (GenHostility.IsActiveThreatTo(target, Faction.OfPlayer, ignoreHives: false)) count++;
                }
                catch (Exception ex)
                {
                    WarnModError(pawn, "threat status", ex);
                }
            }
            return count;
        }

        public IReadOnlyList<ColonistInfo> GetColonists()
        {
            return Map.mapPawns.FreeColonistsSpawned.Select(ToColonist).ToList();
        }

        public IReadOnlyList<ColonistHealthInfo> GetHealth()
        {
            return Map.mapPawns.FreeColonistsSpawned.Select(ToHealth).ToList();
        }

        private static ColonistHealthInfo ToHealth(Pawn pawn)
        {
            var conditions = new List<HealthConditionInfo>();
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                // 模組 hediff 的標籤、流血率等可能丟例外：退回 defName，不讓整份健康報告失敗，也不藏掉這個狀況。
                HealthConditionInfo condition;
                try
                {
                    condition = ToCondition(hediff);
                }
                catch (Exception ex)
                {
                    WarnModError(pawn, "health condition " + hediff.def?.defName, ex);
                    condition = FallbackCondition(hediff);
                }
                conditions.Add(condition);
            }
            List<SurgeryBillInfo> surgeries = pawn.BillStack.Bills.OfType<Bill_Medical>().Select(b => new SurgeryBillInfo
            {
                Label = b.LabelCap,
                Part = b.Part != null ? b.Part.LabelCap : string.Empty,
                Suspended = b.suspended
            }).ToList();
            return new ColonistHealthInfo { PawnId = pawn.thingIDNumber, Conditions = conditions, PendingSurgeries = surgeries };
        }

        private static HealthConditionInfo ToCondition(Hediff hediff)
        {
            bool tended = false, needsTending = false;
            if (hediff.def.tendable)
            {
                HediffComp_TendDuration tend = (hediff as HediffWithComps)?.TryGetComp<HediffComp_TendDuration>();
                tended = tend != null && tend.IsTended;
                needsTending = !tended && hediff.TendableNow(false);
            }
            return new HealthConditionInfo
            {
                Label = hediff.LabelCap,
                Part = hediff.Part != null ? hediff.Part.LabelCap : string.Empty,
                Severity = hediff.Severity,
                Kind = ResolveConditionKind(hediff),
                Bleeding = hediff.BleedRate > 0f,
                Tended = tended,
                NeedsTending = needsTending
            };
        }

        /// <summary>
        /// 讀不到完整資料的狀況。標籤壞了不代表沒在流血：流血與待處置各自再讀一次，
        /// 再讀不到就往「需要處理」報，寧可多治一次也不讓殖民者流血至死。
        /// </summary>
        private static HealthConditionInfo FallbackCondition(Hediff hediff)
        {
            bool bleeding, needsTending;
            try
            {
                bleeding = hediff.BleedRate > 0f;
            }
            catch (Exception)
            {
                bleeding = hediff.def?.injuryProps?.bleedRate > 0f;
            }
            try
            {
                needsTending = hediff.TendableNow(false);
            }
            catch (Exception)
            {
                needsTending = hediff.def?.tendable == true;
            }
            return new HealthConditionInfo
            {
                Label = hediff.def?.defName ?? "unknown condition",
                Kind = ResolveConditionKind(hediff),
                Bleeding = bleeding,
                NeedsTending = needsTending
            };
        }

        private static string ResolveConditionKind(Hediff hediff)
        {
            if (hediff is Hediff_MissingPart) return HealthConditionInfo.MissingKind;
            if (hediff is Hediff_Injury) return HealthConditionInfo.InjuryKind;
            return HealthConditionInfo.ConditionKind;
        }

        public ResourceSnapshot GetResources()
        {
            ResourceCounter counter = Map.resourceCounter;
            return new ResourceSnapshot
            {
                TotalHumanEdibleNutrition = counter.TotalHumanEdibleNutrition,
                Items = counter.AllCountedAmounts
                    .Where(pair => pair.Value > 0)
                    .Select(pair => new ResourceInfo
                    {
                        DefName = pair.Key.defName,
                        Label = pair.Key.label,
                        Count = pair.Value,
                        Category = pair.Key.FirstThingCategory?.label ?? pair.Key.category.ToString()
                    })
                    .ToList()
            };
        }

        // AlertsReadout 的活動清單在 1.6 為私有快取；只讀取遊戲已計算的結果，
        // 找不到欄位時安全地回傳空清單，避免版本差異中斷 agent。
        #pragma warning disable S3011 // RimWorld 私有欄位與方法存取為 Mod 運作必需
        private static readonly FieldInfo ActiveAlertsField =
            typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic);
        #pragma warning restore S3011

        public IReadOnlyList<AlertInfo> GetAlerts()
        {
            var alerts = ActiveAlertsField?.GetValue(Find.Alerts) as IEnumerable<Alert>;
            if (alerts == null) return Array.Empty<AlertInfo>();
            return alerts.Where(a => a != null && a.Active).Select(a => new AlertInfo
            {
                Label = a.Label.ToString(),
                Priority = a.Priority.ToString()
            }).ToList();
        }

        public IReadOnlyList<LetterInfo> GetRecentLetters()
        {
            return Find.LetterStack.LettersListForReading
                .Where(l => l != null)
                .Take(20)
                .Select(l => new LetterInfo { Label = l.Label.ToString() })
                .ToList();
        }

        // 抉擇對話框：DiaOption 的文字與觸發、對話框目前的節點與標題都是 protected。
        // 只讀取、觸發遊戲原生的選項；找不到成員時視為沒有可回應的抉擇，避免版本差異中斷 agent。
        #pragma warning disable S3011 // RimWorld 私有欄位與方法存取為 Mod 運作必需
        private static readonly FieldInfo DiaOptionTextField =
            typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo DiaOptionActivateMethod =
            typeof(DiaOption).GetMethod("Activate", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NodeTreeCurNodeField =
            typeof(Dialog_NodeTree).GetField("curNode", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NodeTreeTitleField =
            typeof(Dialog_NodeTree).GetField("title", BindingFlags.Instance | BindingFlags.NonPublic);
        #pragma warning restore S3011
        private const string LetterChoicePrefix = "letter-";
        private const string DialogChoicePrefix = "dialog-";
        private const int ChoiceTextLimit = 600;
        private const string ChoiceNotFound = "no pending choice with that id. Call list_pending_choices for the current ids.";

        public IReadOnlyList<PendingChoiceInfo> GetPendingChoices()
        {
            var result = new List<PendingChoiceInfo>();
            foreach (Dialog_NodeTree dialog in Find.WindowStack.Windows.OfType<Dialog_NodeTree>())
            {
                DiaNode node = CurrentNode(dialog);
                if (node == null) continue;
                result.Add(new PendingChoiceInfo
                {
                    Id = DialogChoicePrefix + dialog.ID,
                    Title = NodeTreeTitleField?.GetValue(dialog) as string ?? string.Empty,
                    Text = LimitChoiceText(node.text.Resolve()),
                    Options = ToChoiceOptions(node.options)
                });
            }
            foreach (Dialog_MessageBox box in Find.WindowStack.Windows.OfType<Dialog_MessageBox>())
            {
                result.Add(new PendingChoiceInfo
                {
                    Id = DialogChoicePrefix + box.ID,
                    Title = box.title ?? string.Empty,
                    Text = LimitChoiceText(box.text.Resolve()),
                    Options = MessageBoxButtons(box)
                        .Select(b => new ChoiceOptionInfo { Number = b.Number, Text = b.Text }).ToList()
                });
            }
            int now = Find.TickManager.TicksGame;
            foreach (ChoiceLetter letter in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>())
            {
                if (!IsDecisionLetter(letter)) continue;
                result.Add(new PendingChoiceInfo
                {
                    Id = LetterChoicePrefix + letter.ID,
                    Title = letter.Label.Resolve(),
                    Text = LimitChoiceText(letter.Text.Resolve()),
                    ExpiresInTicks = letter.TimeoutActive ? letter.disappearAtTick - now : (int?)null,
                    Options = ToChoiceOptions(letter.Choices.ToList())
                });
            }
            return result;
        }

        public CommandResult ChooseOption(string choiceId, int option)
        {
            string id = choiceId?.Trim() ?? string.Empty;
            if (TryParseChoiceId(id, DialogChoicePrefix, out int windowId))
            {
                Window window = Find.WindowStack.Windows.FirstOrDefault(w => w.ID == windowId);
                if (window is Dialog_MessageBox box) return PressMessageBoxButton(box, option);
                return window is Dialog_NodeTree dialog ? ActivateOption(dialog, option, false) : CommandResult.Fail(ChoiceNotFound);
            }
            if (TryParseChoiceId(id, LetterChoicePrefix, out int letterId))
            {
                ChoiceLetter letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>()
                    .FirstOrDefault(l => l.ID == letterId && IsDecisionLetter(l));
                if (letter == null) return CommandResult.Fail(ChoiceNotFound);
                // 照玩家的流程：打開信件，由遊戲組好對話框並設好每個選項的歸屬，再按選項。
                var before = new HashSet<Window>(Find.WindowStack.Windows);
                letter.OpenLetter();
                Dialog_NodeTree dialog = Find.WindowStack.Windows.OfType<Dialog_NodeTree>().LastOrDefault(w => !before.Contains(w));
                return dialog == null
                    ? CommandResult.Fail("the letter did not open a choice dialog")
                    : ActivateOption(dialog, option, true);
            }
            return CommandResult.Fail(ChoiceNotFound);
        }

        /// <summary>
        /// 需要玩家做決定的信件。一般通知、死訊只有關閉／跳轉之類的選項；
        /// 任務邀請要到任務分頁接受，信件本身沒有接受選項。
        /// </summary>
        private static bool IsDecisionLetter(ChoiceLetter letter)
        {
            return !(letter is StandardLetter || letter is DeathLetter || letter is NewQuestLetter);
        }

        private static bool TryParseChoiceId(string id, string prefix, out int number)
        {
            number = 0;
            return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                   && int.TryParse(id.Substring(prefix.Length), out number);
        }

        private static DiaNode CurrentNode(Dialog_NodeTree dialog)
        {
            return NodeTreeCurNodeField?.GetValue(dialog) as DiaNode;
        }

        private static string LimitChoiceText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= ChoiceTextLimit ? text : text.Substring(0, ChoiceTextLimit) + "…";
        }

        private static IReadOnlyList<ChoiceOptionInfo> ToChoiceOptions(IList<DiaOption> options)
        {
            var result = new List<ChoiceOptionInfo>();
            for (int i = 0; i < options.Count; i++)
            {
                DiaOption option = options[i];
                // 資訊卡連結只開說明視窗，不是抉擇。
                if (option.hyperlink.def != null) continue;
                result.Add(new ChoiceOptionInfo
                {
                    Number = i + 1,
                    Text = OptionText(option),
                    Disabled = option.disabled,
                    DisabledReason = option.disabledReason ?? string.Empty
                });
            }
            return result;
        }

        private static string OptionText(DiaOption option)
        {
            return DiaOptionTextField?.GetValue(option) as string ?? "?";
        }

        /// <summary>確認框的按鈕：1＝A（通常是確定）、2＝B（通常是取消）、3＝C；沒有的按鈕不列。</summary>
        private static List<(int Number, string Text, Action Action, bool Close)> MessageBoxButtons(Dialog_MessageBox box)
        {
            var buttons = new List<(int, string, Action, bool)> { (1, box.buttonAText ?? "OK", box.buttonAAction, true) };
            if (box.buttonBText != null) buttons.Add((2, box.buttonBText, box.buttonBAction, true));
            if (!box.buttonCText.NullOrEmpty()) buttons.Add((3, box.buttonCText, box.buttonCAction, box.buttonCClose));
            return buttons;
        }

        /// <summary>照 Dialog_MessageBox 按鈕被點的順序：先跑按鈕動作，再關視窗。</summary>
        private static CommandResult PressMessageBoxButton(Dialog_MessageBox box, int option)
        {
            foreach ((int number, string text, Action action, bool close) in MessageBoxButtons(box))
            {
                if (number != option) continue;
                action?.Invoke();
                if (close) box.Close();
                return CommandResult.Ok(text);
            }
            return CommandResult.Fail($"option {option} does not exist");
        }

        private static CommandResult ActivateOption(Dialog_NodeTree dialog, int option, bool closeOnFail)
        {
            if (DiaOptionActivateMethod == null)
            {
                if (closeOnFail) dialog.Close(false);
                return CommandResult.Fail("this game version does not expose dialog options");
            }
            List<DiaOption> options = CurrentNode(dialog)?.options;
            DiaOption chosen = options != null && option >= 1 && option <= options.Count ? options[option - 1] : null;
            string error = null;
            if (chosen == null) error = $"option {option} does not exist";
            else if (chosen.hyperlink.def != null) error = $"option {option} only opens an info card";
            else if (chosen.disabled)
            {
                error = $"option {option} is disabled" + (string.IsNullOrEmpty(chosen.disabledReason) ? string.Empty : ": " + chosen.disabledReason);
            }
            if (error != null)
            {
                // 為了按選項才打開的信件對話框要關回去，不留一個擋住遊戲的視窗。
                if (closeOnFail) dialog.Close(false);
                return CommandResult.Fail(error);
            }
            string label = OptionText(chosen);
            DiaOptionActivateMethod.Invoke(chosen, null);
            return CommandResult.Ok(label);
        }

        #pragma warning disable S1168 // null 代表未在地圖上找到該實體
        public IReadOnlyList<GizmoInfo> GetGizmos(int thingId)
        {
            Thing thing = FindThing(thingId);
            if (thing == null || thing.Fogged()) return null;
            List<Command> commands = VisibleCommands(thing);
            var result = new List<GizmoInfo>(commands.Count);
            for (int i = 0; i < commands.Count; i++)
            {
                Command command = commands[i];
                result.Add(new GizmoInfo
                {
                    Number = i + 1,
                    Label = CommandLabel(command),
                    Description = LimitChoiceText(command.Desc),
                    Kind = DetermineGizmoKind(command),
                    On = (command as Command_Toggle)?.isActive?.Invoke() ?? false,
                    Disabled = command.Disabled,
                    DisabledReason = command.disabledReason ?? string.Empty
                });
            }
            return result;
        }
        #pragma warning restore S1168

        private static string DetermineGizmoKind(Command command)
        {
            if (command is Command_Toggle) return GizmoInfo.ToggleKind;
            if (command is Command_Action) return GizmoInfo.ActionKind;
            return GizmoInfo.UnsupportedKind;
        }

        public CommandResult UseGizmo(int thingId, int gizmoNumber, string label)
        {
            Thing thing = FindThing(thingId);
            if (thing == null || thing.Fogged()) return CommandResult.Fail("thing not found on the map");
            List<Command> commands = VisibleCommands(thing);
            CommandResult validation = ValidateGizmoCommand(thing, gizmoNumber, label, commands, out Command command);
            if (validation != null) return validation;

            var before = new HashSet<Window>(Find.WindowStack.Windows);
            command.ProcessInput(null);
            CommandResult windowFailure = CloseUnsupportedWindows(before, label);
            if (windowFailure != null) return windowFailure;

            return CommandResult.Ok(GetToggleCommandResultSuffix(command));
        }

        private static CommandResult ValidateGizmoCommand(Thing thing, int gizmoNumber, string label, List<Command> commands, out Command command)
        {
            command = gizmoNumber >= 1 && gizmoNumber <= commands.Count ? commands[gizmoNumber - 1] : null;
            if (command == null || !string.Equals(CommandLabel(command), label, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"{thing.LabelShort}'s button list changed; call list_gizmos again");
            if (command.Disabled)
                return CommandResult.Fail($"\"{label}\" is disabled" + (command.disabledReason.NullOrEmpty() ? string.Empty : ": " + command.disabledReason));
            if (!(command is Command_Action) && !(command is Command_Toggle))
                return CommandResult.Fail($"\"{label}\" needs a map target or a menu choice");
            return null;
        }

        private static CommandResult CloseUnsupportedWindows(HashSet<Window> beforeWindows, string label)
        {
            List<Window> opened = Find.WindowStack.Windows.Where(w => !beforeWindows.Contains(w)).ToList();
            List<Window> unsupported = opened.Where(w => !(w is Dialog_NodeTree) && !(w is Dialog_MessageBox)).ToList();
            foreach (Window window in unsupported) window.Close(false);
            if (unsupported.Count > 0)
            {
                return CommandResult.Fail($"\"{label}\" opened a {(unsupported[0] is FloatMenu ? "menu" : "window")} to pick from, which is not supported yet; it was closed");
            }
            return null;
        }

        private static string GetToggleCommandResultSuffix(Command command)
        {
            if (command is Command_Toggle toggle)
            {
                return toggle.isActive() ? "now on" : "now off";
            }
            return string.Empty;
        }

        /// <summary>選取時會畫出來的指令按鈕；純狀態顯示的 gizmo（護盾條之類）與開發者模式按鈕不算。</summary>
        private static List<Command> VisibleCommands(Thing thing)
        {
            return thing.GetGizmos().OfType<Command>()
                .Where(c => c.Visible && !IsDevLabel(CommandLabel(c)))
                .ToList();
        }

        private static string CommandLabel(Command command)
        {
            return command.LabelCap ?? string.Empty;
        }

        private static bool IsDevLabel(string label)
        {
            return label.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Debug:", StringComparison.OrdinalIgnoreCase);
        }

        public PowerInfo GetPower()
        {
            IReadOnlyList<PowerNet> nets = Map.powerNetManager.AllNetsListForReading;
            return new PowerInfo
            {
                NetworkCount = nets.Count,
                NetWatts = nets.Sum(n => n.CurrentEnergyGainRate()),
                StoredEnergy = nets.Sum(n => n.CurrentStoredEnergy())
            };
        }

        public IReadOnlyList<ThreatInfo> GetThreats()
        {
            Map map = Map;
            var threats = new List<ThreatInfo>();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead || !pawn.HostileTo(Faction.OfPlayer)) continue;
                string kind = ResolveThreatKind(pawn);
                threats.Add(new ThreatInfo
                {
                    Id = pawn.thingIDNumber,
                    Label = pawn.LabelShort,
                    Kind = kind,
                    Faction = pawn.Faction?.Name ?? string.Empty,
                    X = pawn.Position.x,
                    Z = pawn.Position.z,
                    Downed = pawn.Downed
                });
            }
            foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
            {
                threats.Add(new ThreatInfo { Id = fire.thingIDNumber, Label = fire.Label, Kind = "Fire", X = fire.Position.x, Z = fire.Position.z });
            }
            return threats;
        }

        /// <summary>
        /// 依 RaceProps 分類，不以「不是類人就是動物」推定：機兵、Anomaly 實體與模組的機器人／異種族都不是動物。
        /// 不用 RaceProps.Animal（要求血肉之軀），模組的非血肉野獸也照動物報。
        /// </summary>
        private static string ResolveThreatKind(Pawn pawn)
        {
            RaceProperties race = pawn.RaceProps;
            if (race.IsMechanoid) return "Mechanoid";
            if (race.IsAnomalyEntity) return "Entity";
            if (race.ToolUser) return "HostilePawn";
            return pawn.InMentalState ? "Manhunter" : "HostileAnimal";
        }

        public IReadOnlyList<TraderInfo> GetTraders()
        {
            Map map = Map;
            var result = new List<TraderInfo>();
            foreach (TradeShip ship in map.passingShipManager.passingShips.OfType<TradeShip>())
            {
                if (ship.CanTradeNow)
                {
                    result.Add(CreateOrbitalTraderInfo(ship));
                }
            }
            // 來訪商隊（Visitor）：同一 Lord 共用整隊庫存，只列一次；不拿軌道商人頂替。
            // CanTradeNow 已排除死亡、倒地、俘虜、玩家派系與敵對派系，只剩地圖歸屬要自己檢查。
            foreach (IGrouping<object, Pawn> group in map.mapPawns.AllPawnsSpawned
                         .Where(p => p.Map == map && p.trader != null && p.trader.traderKind != null && p.trader.CanTradeNow)
                         .GroupBy(p => (object)p.GetLord() ?? p))
            {
                result.Add(CreateVisitorTraderInfo(group.First()));
            }
            return result;
        }

        private static List<TradeGoodsInfo> ExtractGoods(IEnumerable<Thing> rawGoods, out int silver)
        {
            var goods = new List<TradeGoodsInfo>();
            silver = 0;
            foreach (Thing t in rawGoods)
            {
                if (t == null || t.Destroyed) continue;
                if (t.def == ThingDefOf.Silver)
                {
                    silver += t.stackCount;
                    continue;
                }
                Pawn goodsPawn = t as Pawn;
                goods.Add(new TradeGoodsInfo
                {
                    DefName = t.def.defName,
                    Label = goodsPawn != null ? goodsPawn.LabelShort : t.def.label,
                    Count = Math.Max(1, t.stackCount),
                    MarketValue = t.MarketValue
                });
            }
            return goods
                .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.DefName, StringComparer.Ordinal)
                .ToList();
        }

        private static TraderInfo CreateOrbitalTraderInfo(TradeShip ship)
        {
            var goods = ExtractGoods(ship.Goods, out _);
            return new TraderInfo
            {
                Kind = TraderInfo.OrbitalKind,
                Id = ship.GetUniqueLoadID(),
                Name = ship.FullTitle,
                Faction = ship.Faction?.Name ?? string.Empty,
                Silver = ship.Silver,
                Goods = goods
            };
        }

        private static TraderInfo CreateVisitorTraderInfo(Pawn leader)
        {
            var goods = ExtractGoods(leader.trader.Goods, out int silver);
            return new TraderInfo
            {
                Kind = TraderInfo.VisitorKind,
                Id = leader.GetUniqueLoadID(),
                Name = leader.LabelShort + " (" + leader.trader.traderKind.label + ")",
                Faction = leader.Faction?.Name ?? string.Empty,
                Silver = silver,
                Goods = goods
            };
        }

        public ResearchInfo GetResearch()
        {
            ResearchProjectDef current = Find.ResearchManager.GetProject();
            return new ResearchInfo
            {
                Current = current == null ? null : ToResearch(current),
                Available = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .Where(r => !r.IsFinished && r.CanStartNow)
                    .OrderBy(r => r.CostApparent)
                    .Select(ToResearch)
                    .ToList()
            };
        }

        /// <summary>
        /// 尚未完成的研發目錄。不快取：研究完成後目錄即變，
        /// 每次都重讀 DefDatabase（約百來個 def，可接受）。
        /// </summary>
        [SuppressMessage("Major Code Smell", "S2365:Properties should not copy collections", Justification = "Implements IGameWorld property contract which reflects dynamic game state")]
        public IReadOnlyList<DefInfo> ResearchProjects =>
            DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(r => !r.IsFinished)
                .Select(r => new DefInfo(r.defName, r.label))
                .ToList();

        [SuppressMessage("Major Code Smell", "S2365:Properties should not copy collections", Justification = "Implements IGameWorld property contract which reflects dynamic game state")]
        public IReadOnlyList<DefInfo> FinishedResearchProjects =>
            DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(r => r.IsFinished)
                .Select(r => new DefInfo(r.defName, r.label))
                .ToList();

        public CommandResult SetResearch(string researchDefName)
        {
            string want = (researchDefName ?? string.Empty).Trim();
            if (want.Length == 0 || string.Equals(want, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(want, "stop", StringComparison.OrdinalIgnoreCase))
            {
                ResearchProjectDef current = Find.ResearchManager.GetProject();
                if (current == null) return CommandResult.Ok("No research is running.");
                Find.ResearchManager.StopProject(current);
                return CommandResult.Ok($"Stopped research {current.label}.");
            }
            ResearchProjectDef def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(want);
            if (def == null) return CommandResult.Fail($"unknown research {want}");
            if (def.IsFinished) return CommandResult.Fail($"{def.label} is already finished");
            if (!def.CanStartNow) return CommandResult.Fail($"{def.label} cannot start yet: finish its prerequisites first");
            if (Find.ResearchManager.GetProject() == def) return CommandResult.Ok($"Research {def.label} is already running.");
            Find.ResearchManager.SetCurrentProject(def);
            return CommandResult.Ok($"Research switched to {def.label}.");
        }

        public IReadOnlyList<MapInfo> GetMaps()
        {
            Map current = Map;
            return Find.Maps.Select(m => new MapInfo
            {
                Id = m.uniqueID,
                Label = m.Parent?.LabelCap ?? ("Map " + m.uniqueID),
                Tile = m.Tile,
                ColonistCount = m.mapPawns.FreeColonistsSpawnedCount,
                IsCurrent = m == current
            }).ToList();
        }

        public CommandResult SetCurrentMap(int mapId)
        {
            Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
            if (map == null) return CommandResult.Fail($"no map has id {mapId}");
            string label = map.Parent?.LabelCap ?? ("Map " + map.uniqueID);
            if (map == Map) return CommandResult.Ok($"{label} is already the current map.");
            Current.Game.CurrentMap = map;
            return CommandResult.Ok($"Switched to {label} [{mapId}]. Later tools now operate on this map.");
        }

        public IReadOnlyList<DefInfo> TimeSpeeds => TimeSpeedDirectory.Default;

        public TimeSpeedInfo GetTimeSpeed()
        {
            return new TimeSpeedInfo { Current = Find.TickManager.CurTimeSpeed.ToString(), Available = TimeSpeedDirectory.Default };
        }

        public CommandResult SetTimeSpeed(string speedDefName)
        {
            object parsed;
            try
            {
                parsed = Enum.Parse(typeof(TimeSpeed), (speedDefName ?? string.Empty).Trim(), true);
            }
            catch (ArgumentException)
            {
                return CommandResult.Fail($"unknown time speed {speedDefName}");
            }
            var speed = (TimeSpeed)parsed;
            if (!Enum.IsDefined(typeof(TimeSpeed), speed)) return CommandResult.Fail($"unknown time speed {speedDefName}");
            if (speed == TimeSpeed.Ultrafast) return CommandResult.Fail("Ultrafast is only for development; use Paused, Normal, Fast or Superfast");
            TimeSpeed previous = Find.TickManager.CurTimeSpeed;
            if (previous == speed) return CommandResult.Ok($"Time speed is already {speed}.");
            Find.TickManager.CurTimeSpeed = speed;
            return CommandResult.Ok($"Time speed set to {speed}.");
        }

        public IReadOnlyList<ThingInfo> FindThings(ThingQuery query)
        {
            Map map = Map;
            var result = new List<ThingInfo>();
            bool hasDefName = !string.IsNullOrEmpty(query.DefName);
            bool hasNeedle = !string.IsNullOrEmpty(query.LabelContains);
            // 便宜的檢查排前面：座標是整數比較，Label 要走 GenLabel 配字串，全圖數萬個 Thing 逐一算會卡一幀。
            foreach (Thing t in CandidateThings(map, query.Kind))
            {
                if (result.Count >= query.Limit) break;
                if (MatchesQuery(t, query, hasDefName, hasNeedle))
                {
                    result.Add(ToThing(t, map));
                }
            }
            return result;
        }

        private static bool MatchesQuery(Thing t, ThingQuery query, bool hasDefName, bool hasNeedle)
        {
            if (!t.Spawned) return false;
            if (query.Area.HasValue && !query.Area.Value.Contains(t.Position.x, t.Position.z)) return false;
            ThingKind? kind = KindOf(t);
            if (kind == null || (query.Kind != ThingKind.Any && kind != query.Kind)) return false;
            if (hasDefName && !string.Equals(t.def.defName, query.DefName, StringComparison.OrdinalIgnoreCase)) return false;
            if (hasNeedle && !MatchesLabel(t, query.LabelContains)) return false;
            return true;
        }

        /// <summary>
        /// 依 kind 縮小掃描範圍：pawn 與植物各有現成清單（Plant 群組就是 category == Plant），
        /// 其餘才掃全圖（物品沒有恰好對應 category == Item 的群組，HaulableEver 會漏掉不可搬運的物品）。
        /// </summary>
        private static IEnumerable<Thing> CandidateThings(Map map, ThingKind kind)
        {
            switch (kind)
            {
                case ThingKind.Pawn: return map.mapPawns.AllPawnsSpawned;
                case ThingKind.Plant: return map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);
                default: return map.listerThings.AllThings;
            }
        }

        private static bool MatchesLabel(Thing t, string needle)
        {
            return t.def.defName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                   || (t.def.label ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                   || t.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public IReadOnlyList<ThingInfo> GetSelection()
        {
            Map map = Map;
            return Find.Selector.SelectedObjects.OfType<Thing>()
                .Where(t => t.Spawned && t.Map == map && KindOf(t) != null)
                .Select(t => ToThing(t, map))
                .ToList();
        }

        public MapBounds GetMapBounds()
        {
            IntVec3 size = Map.Size;
            return new MapBounds { Width = size.x, Height = size.z };
        }

        public CellInfo GetCell(int x, int z)
        {
            return ToCell(new IntVec3(x, 0, z), Map, new Dictionary<int, RoomInfo>());
        }

        public IReadOnlyList<CellInfo> GetCells(CellRectSpec rect)
        {
            Map map = Map;
            var result = new List<CellInfo>(rect.CellCount);
            // 同一個房間在矩形裡會出現很多次，RoomInfo 只建一份（外框要走遍房間所有格子）。
            var rooms = new Dictionary<int, RoomInfo>();
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            {
                for (int x = rect.MinX; x <= rect.MaxX; x++)
                {
                    result.Add(ToCell(new IntVec3(x, 0, z), map, rooms));
                }
            }
            return result;
        }

        private static CellInfo ToCell(IntVec3 c, Map map, Dictionary<int, RoomInfo> rooms)
        {
            TerrainDef terrain = c.GetTerrain(map);
            bool fogged = c.Fogged(map);
            Zone zone = c.GetZone(map);
            return new CellInfo
            {
                X = c.x,
                Z = c.z,
                TerrainDefName = terrain.defName,
                Terrain = terrain.label ?? terrain.defName,
                Fertility = map.fertilityGrid.FertilityAt(c),
                Roof = RoofLabel(c.GetRoof(map)),
                Walkable = c.Walkable(map),
                Fogged = fogged,
                Zone = zone != null ? ToZone(zone) : null,
                Room = ToRoom(c.GetRoom(map), rooms),
                // 迷霧裡的東西玩家看不到，工具也不透露。藍圖不是 Building（KindOf 會濾掉），
                // 但它占著格子，規劃建造時得看得到，這裡當建築列出。
                Things = fogged
                    ? (IReadOnlyList<ThingInfo>)Array.Empty<ThingInfo>()
                    : c.GetThingList(map).Where(t => KindOf(t) != null || t is Blueprint).Select(t => ToThing(t, map)).ToList()
            };
        }

        private static string RoofLabel(RoofDef roof)
        {
            if (roof == null) return string.Empty;
            if (!roof.isNatural) return "constructed";
            return roof.isThickRoof ? "overhead mountain" : "natural";
        }

        private static RoomInfo ToRoom(Room room, Dictionary<int, RoomInfo> cache)
        {
            if (room == null) return null;
            if (cache.TryGetValue(room.ID, out RoomInfo cached)) return cached;
            CellRect extents = room.ExtentsClose;
            var info = new RoomInfo
            {
                Id = room.ID,
                Role = room.Role?.label ?? string.Empty,
                CellCount = room.CellCount,
                Outdoors = room.PsychologicallyOutdoors,
                TemperatureC = room.Temperature,
                Extents = new CellRectSpec(extents.minX, extents.minZ, extents.maxX, extents.maxZ)
            };
            cache[room.ID] = info;
            return info;
        }

        public string GetNotReadyReason()
        {
            // 讀檔期間 ProgramState 會先變成 MapInitializing，這一條已經擋住；不看長事件，
            // 自動存檔排入的那一幀與背景生成新地圖期間當前地圖都還能碰，擋了只會誤殺指令。
            if (Current.Game == null || Current.ProgramState != ProgramState.Playing) return "no game is being played";
            if (Find.CurrentMap == null) return "there is no current map";
            return null;
        }

        private const int WaitPollMilliseconds = 100;

        public async System.Threading.Tasks.Task<CommandResult> WaitTicksAsync(int ticks, System.Threading.CancellationToken cancellationToken)
        {
            Verse.Game game = Current.Game;
            int target = Find.TickManager.TicksGame + Math.Max(0, ticks);
            while (true)
            {
                string notReady = GetNotReadyReason();
                // 等待期間讀了別的檔：tick 已換一條時間軸，目標不再有意義。
                if (notReady != null || Current.Game != game) return CommandResult.Fail(notReady ?? "a different game was loaded");
                if (Find.TickManager.TicksGame >= target) return CommandResult.Ok(string.Empty);
                if (Find.TickManager.Paused) return CommandResult.Fail("the game is paused");
                // 不加 ConfigureAwait(false)：續行要回主線程才能再讀 TickManager。
                await System.Threading.Tasks.Task.Delay(WaitPollMilliseconds, cancellationToken);
            }
        }

        private LogMessage _lastSeenLog;
        private int _lastSeenLogRepeats;

        public IReadOnlyList<string> TakeNewGameErrors()
        {
            // Log 把連續相同訊息合併成一則並累加 repeats，所以「新錯誤」有兩種：上次看過的那則又重複了，
            // 以及排在它後面的新訊息。上次那則已被清掉或擠出佇列時，整個佇列都算新的。
            var errors = new List<string>();
            // Log 的鎖是私有的，背景執行緒隨時可能 Log.Error；列舉撞上修改就當這次沒有新錯誤，不能讓指令因此失敗。
            List<LogMessage> messages;
            try
            {
                messages = Log.Messages.ToList();
            }
            catch (InvalidOperationException)
            {
                return errors;
            }
            bool afterLastSeen = _lastSeenLog == null || !messages.Contains(_lastSeenLog);
            LogMessage last = null;
            foreach (LogMessage message in messages)
            {
                last = message;
                if (!afterLastSeen)
                {
                    if (!ReferenceEquals(message, _lastSeenLog)) continue;
                    afterLastSeen = true;
                    if (message.type == LogMessageType.Error && message.repeats > _lastSeenLogRepeats)
                    {
                        errors.Add(FormatLogError(message, message.repeats - _lastSeenLogRepeats));
                    }
                    continue;
                }
                if (message.type == LogMessageType.Error) errors.Add(FormatLogError(message, message.repeats));
            }
            _lastSeenLog = last;
            _lastSeenLogRepeats = last?.repeats ?? 0;
            return errors;
        }

        private static string FormatLogError(LogMessage message, int occurrences)
        {
            string text = (message.text ?? string.Empty).Trim();
            int newline = text.IndexOf('\n');
            if (newline >= 0) text = text.Substring(0, newline);
            return occurrences > 1 ? $"(x{occurrences}) {text}" : text;
        }

        public IReadOnlyList<DefInfo> WorkTypes =>
            _workTypes ?? (_workTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                .Where(w => w.visible)
                .Select(w => new DefInfo(w.defName, w.labelShort ?? w.label))
                .ToList());

        public IReadOnlyList<DefInfo> Buildables =>
            _buildables ?? (_buildables = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer).Select(d => new DefInfo(d.defName, d.label))
                .Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer).Select(d => new DefInfo(d.defName, d.label)))
                .ToList());

        public IReadOnlyList<DefInfo> Stuffs =>
            _stuffs ?? (_stuffs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.IsStuff)
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public IReadOnlyList<DefInfo> TimeAssignments =>
            _timeAssignments ?? (_timeAssignments = DefDatabase<TimeAssignmentDef>.AllDefsListForReading
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public IReadOnlyList<DefInfo> TradeGoods =>
            _tradeGoods ?? (_tradeGoods = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.category == ThingCategory.Item)
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public IReadOnlyList<DefInfo> MedicalCareLevels =>
            _medicalCareLevels ?? (_medicalCareLevels = Enum.GetValues(typeof(MedicalCareCategory))
                .Cast<MedicalCareCategory>()
                .Select(c => new DefInfo(c.ToString(), MedicalCareUtility.GetLabel(c)))
                .ToList());

        public CommandResult SetMedicalCare(int pawnId, string careDefName)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (pawn.playerSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no medical care settings");
            object parsed;
            try
            {
                parsed = Enum.Parse(typeof(MedicalCareCategory), careDefName, true);
            }
            catch (ArgumentException)
            {
                return CommandResult.Fail($"unknown medical care level {careDefName}");
            }
            if (!Enum.IsDefined(typeof(MedicalCareCategory), parsed)) return CommandResult.Fail($"unknown medical care level {careDefName}");
            pawn.playerSettings.medCare = (MedicalCareCategory)parsed;
            return CommandResult.Ok();
        }

        public IReadOnlyList<DefInfo> SurgeryRecipes =>
            _surgeryRecipes ?? (_surgeryRecipes = DefDatabase<RecipeDef>.AllDefsListForReading
                .Where(r => r.workerClass != null && typeof(Recipe_Surgery).IsAssignableFrom(r.workerClass))
                .Select(r => new DefInfo(r.defName, r.label))
                .ToList());

        public CommandResult ScheduleSurgery(SurgeryRequest request)
        {
            Pawn pawn = FindColonist(request.PawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(request.RecipeDefName);
            if (recipe == null || recipe.workerClass == null || !typeof(Recipe_Surgery).IsAssignableFrom(recipe.workerClass))
                return CommandResult.Fail($"unknown surgery {request.RecipeDefName}");
            CommandResult researchFail = CheckSurgeryResearch(recipe);
            if (researchFail != null) return researchFail;
            CommandResult partFail = ResolveSurgeryTargetPart(recipe, pawn, request.BodyPartLabel, out BodyPartRecord part);
            if (partFail != null) return partFail;
            if (!recipe.AvailableOnNow(pawn, part))
                return CommandResult.Fail($"{recipe.label} is not available on {pawn.LabelShort} right now");
            if (pawn.BillStack.Bills.OfType<Bill_Medical>().Any(b => b.recipe == recipe && b.Part == part))
                return CommandResult.Fail($"{recipe.label} is already scheduled on {pawn.LabelShort}");
            var bill = new Bill_Medical(recipe, null) { Part = part };
            pawn.BillStack.AddBill(bill);
            string detail = $"Scheduled {recipe.label} on {pawn.LabelShort}";
            if (part != null) detail += $" ({part.LabelCap})";
            return CommandResult.Ok(detail + ".");
        }

        private static CommandResult CheckSurgeryResearch(RecipeDef recipe)
        {
            if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished)
                return CommandResult.Fail($"{recipe.label} needs research {recipe.researchPrerequisite.label} first");
            if (recipe.researchPrerequisites != null)
            {
                foreach (ResearchProjectDef prerequisite in recipe.researchPrerequisites)
                {
                    if (prerequisite != null && !prerequisite.IsFinished)
                        return CommandResult.Fail($"{recipe.label} needs research {prerequisite.label} first");
                }
            }
            return null;
        }

        private static CommandResult ResolveSurgeryTargetPart(RecipeDef recipe, Pawn pawn, string wantLabel, out BodyPartRecord part)
        {
            part = null;
            if (!recipe.targetsBodyPart) return null;
            List<BodyPartRecord> options = recipe.Worker.GetPartsToApplyOn(pawn, recipe).ToList();
            if (options.Count == 0) return CommandResult.Fail($"{recipe.label} does not apply to {pawn.LabelShort}");
            string want = (wantLabel ?? string.Empty).Trim();
            // 截肢、摘除器官這類手術不能替玩家挑部位：沒指定就列出候選要模型問清楚。
            if (want.Length == 0)
            {
                return CommandResult.Fail($"{recipe.label} needs a body part. Ask the player which one: " +
                                          string.Join(", ", options.Select(p => p.LabelCap).Distinct().ToArray()));
            }
            part = options.FirstOrDefault(p => MatchesBodyPart(p, want));
            if (part == null) return CommandResult.Fail($"{pawn.LabelShort} has no body part matching \"{want}\" for {recipe.label}");
            return null;
        }

        private static bool MatchesBodyPart(BodyPartRecord part, string want)
        {
            return string.Equals(part.LabelCap, want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(part.Label, want, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(part.customLabel) && string.Equals(part.customLabel, want, StringComparison.OrdinalIgnoreCase))
                || (part.def != null && string.Equals(part.def.label, want, StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<PrisonerInfo> GetPrisoners()
        {
            return Map.mapPawns.PrisonersOfColonySpawned.Select(ToPrisoner).ToList();
        }

        private static PrisonerInfo ToPrisoner(Pawn pawn)
        {
            var triple = pawn.Name as NameTriple;
            string nick = triple?.Nick ?? string.Empty;
            string first = triple?.First ?? pawn.Name?.ToStringShort ?? pawn.LabelShort;
            PrisonerInteractionModeDef mode = pawn.guest?.ExclusiveInteractionMode;
            return new PrisonerInfo
            {
                Id = pawn.thingIDNumber,
                Name = pawn.LabelShort,
                Nickname = !string.IsNullOrEmpty(nick) && nick != first ? nick : string.Empty,
                FullName = pawn.Name?.ToStringFull ?? pawn.LabelShort,
                Resistance = pawn.guest?.Resistance ?? -1f,
                Recruitable = pawn.guest?.Recruitable ?? false,
                InteractionMode = mode?.defName ?? string.Empty,
                InteractionModeLabel = mode?.label ?? string.Empty,
                HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Downed = pawn.Downed
            };
        }

        public IReadOnlyList<DefInfo> PrisonerInteractionModes =>
            _prisonerInteractionModes ?? (_prisonerInteractionModes = DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public CommandResult SetPrisonerInteraction(int pawnId, string modeDefName)
        {
            Pawn pawn = FindPrisoner(pawnId);
            if (pawn == null) return CommandResult.Fail("prisoner not found");
            PrisonerInteractionModeDef mode = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(modeDefName);
            if (mode == null) return CommandResult.Fail($"unknown prisoner interaction mode {modeDefName}");
            pawn.guest.SetExclusiveInteraction(mode);
            return CommandResult.Ok();
        }

        public CommandResult ReleasePrisoner(int pawnId)
        {
            Pawn pawn = FindPrisoner(pawnId);
            if (pawn == null) return CommandResult.Fail("prisoner not found");
            GenGuest.PrisonerRelease(pawn);
            return CommandResult.Ok($"{pawn.LabelShort} was released and left the colony.");
        }

        private static Pawn FindPrisoner(int id)
        {
            return Map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == id);
        }

        public IReadOnlyList<AnimalInfo> GetAnimals()
        {
            return Map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal).Select(ToAnimal).ToList();
        }

        private static AnimalInfo ToAnimal(Pawn pawn)
        {
            return new AnimalInfo
            {
                Id = pawn.thingIDNumber,
                Name = pawn.LabelShort,
                SpeciesDefName = pawn.def.defName,
                Species = pawn.def.label,
                Wild = pawn.Faction != Faction.OfPlayer,
                Area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label ?? string.Empty,
                HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Downed = pawn.Downed
            };
        }

        public IReadOnlyList<DefInfo> AnimalTraining =>
            _animalTraining ?? (_animalTraining = DefDatabase<TrainableDef>.AllDefsListForReading
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        /// <summary>區域是每張地圖的執行期資料（玩家隨時新增、切圖就換），不快取。</summary>
        [SuppressMessage("Major Code Smell", "S2365:Properties should not copy collections", Justification = "Implements IGameWorld property contract border reflects dynamic game state")]
        public IReadOnlyList<DefInfo> AnimalAreas =>
            Map.areaManager.AllAreas
                .Select(a => new DefInfo(a.Label, a.Label))
                .ToList();

        public CommandResult TameAnimal(int pawnId)
        {
            Map map = Map;
            Pawn pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == pawnId && p.RaceProps.Animal);
            if (pawn == null) return CommandResult.Fail("animal not found");
            if (pawn.Dead) return CommandResult.Fail($"{pawn.LabelShort} is dead");
            if (pawn.Faction == Faction.OfPlayer) return CommandResult.Fail($"{pawn.LabelShort} is already tame");
            if (map.designationManager.DesignationOn(pawn, DesignationDefOf.Tame) != null)
                return CommandResult.Fail($"{pawn.LabelShort} is already marked for taming");
            map.designationManager.AddDesignation(new Designation(pawn, DesignationDefOf.Tame));
            return CommandResult.Ok($"Marked {pawn.LabelShort} for taming. A handler will attempt it when available.");
        }

        public CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable)
        {
            Pawn pawn = FindColonyAnimal(pawnId);
            if (pawn == null) return CommandResult.Fail("colony animal not found");
            TrainableDef training = DefDatabase<TrainableDef>.GetNamedSilentFail(trainableDefName);
            if (training == null) return CommandResult.Fail($"unknown animal training {trainableDefName}");
            if (pawn.training == null) return CommandResult.Fail($"{pawn.LabelShort} cannot be trained");
            if (enable && !pawn.training.CanBeTrained(training))
                return CommandResult.Fail($"{pawn.LabelShort} cannot learn {training.label}");
            pawn.training.SetWantedRecursive(training, enable);
            return CommandResult.Ok(enable
                ? $"{pawn.LabelShort} will now be trained in {training.label}."
                : $"{pawn.LabelShort} will no longer be trained in {training.label}.");
        }

        public CommandResult SetAnimalArea(int pawnId, string areaLabel)
        {
            Pawn pawn = FindColonyAnimal(pawnId);
            if (pawn == null) return CommandResult.Fail("colony animal not found");
            if (pawn.playerSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no area settings");
            string label = (areaLabel ?? string.Empty).Trim();
            if (label.Length == 0)
            {
                pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                return CommandResult.Ok($"{pawn.LabelShort}'s allowed area is now unrestricted.");
            }
            Area area = Map.areaManager.AllAreas.FirstOrDefault(a => string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase));
            if (area == null) return CommandResult.Fail($"unknown animal area {label}");
            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            return CommandResult.Ok($"{pawn.LabelShort}'s allowed area is now {area.Label}.");
        }

        private static Pawn FindColonyAnimal(int id)
        {
            return Map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id && p.RaceProps.Animal && p.Faction == Faction.OfPlayer);
        }

        public IReadOnlyList<ZoneInfo> GetZones()
        {
            return Map.zoneManager.AllZones.Select(ToZone).ToList();
        }

        private static ZoneInfo ToZone(Zone zone)
        {
            return new ZoneInfo
            {
                Id = zone.ID,
                Label = zone.label,
                Kind = ResolveZoneKind(zone),
                CellCount = zone.CellCount
            };
        }

        private static string ResolveZoneKind(Zone zone)
        {
            if (zone is Zone_Stockpile) return "Stockpile";
            if (zone is Zone_Growing) return "Growing";
            return zone.GetType().Name;
        }

        public IReadOnlyList<DefInfo> StorageFilterables =>
            _storageFilterables ?? (_storageFilterables = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.category == ThingCategory.Item)
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public IReadOnlyList<DefInfo> GrowablePlants =>
            _growablePlants ?? (_growablePlants = DefDatabase<ThingDef>.AllDefsListForReading
                // 種植區只收 Ground 標籤（PlantUtility.CanSowOnGrower）；裝飾花卉、僅水耕的作物不算。
                .Where(d => d.category == ThingCategory.Plant && d.plant != null && d.plant.Sowable
                            && d.plant.sowTags.Contains("Ground"))
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName)
        {
            Map map = Map;
            bool stockpile = string.Equals(kind, "Stockpile", StringComparison.OrdinalIgnoreCase);
            bool growing = string.Equals(kind, "Growing", StringComparison.OrdinalIgnoreCase);
            if (!stockpile && !growing) return CommandResult.Fail($"unknown zone kind {kind}. Use stockpile or growing.");

            CommandResult plantResult = TryResolveGrowingPlant(growing, plantDefName, out ThingDef plant);
            if (plantResult != null) return plantResult;

            // 格子能不能畫區域交給原版 designator 判斷（迷霧、地圖邊緣禁區、不可重疊的物件、種植區的肥沃度）。
            Designator_ZoneAdd designator = stockpile
                ? (Designator_ZoneAdd)new Designator_ZoneAddStockpile_Resources()
                : new Designator_ZoneAdd_Growing();

            CommandResult cellResult = ValidateZoneCells(map, rect, designator, growing, kind, out List<IntVec3> cells);
            if (cellResult != null) return cellResult;

            Zone zone = CreateZoneInstance(map, stockpile, plant);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 cell in cells) zone.AddCell(cell);
            return CommandResult.Ok($"Added {zone.label} [{zone.ID}] covering {cells.Count} cells.");
        }

        private static CommandResult TryResolveGrowingPlant(bool growing, string plantDefName, out ThingDef plant)
        {
            plant = null;
            if (growing && !string.IsNullOrEmpty(plantDefName))
            {
                plant = DefDatabase<ThingDef>.GetNamedSilentFail(plantDefName);
                if (plant == null || plant.category != ThingCategory.Plant || plant.plant == null)
                    return CommandResult.Fail($"{plantDefName} is not a growable plant");
            }
            return null;
        }

        private static CommandResult ValidateZoneCells(Map map, CellRectSpec rect, Designator_ZoneAdd designator, bool growing, string kind, out List<IntVec3> cells)
        {
            cells = new List<IntVec3>();
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                for (int z = rect.MinZ; z <= rect.MaxZ; z++)
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!cell.InBounds(map)) return CommandResult.Fail($"cell ({x},{z}) is outside the map");
                    Zone existing = map.zoneManager.ZoneAt(cell);
                    if (existing != null) return CommandResult.Fail($"cell ({x},{z}) already belongs to zone \"{existing.label}\"");
                    AcceptanceReport report = designator.CanDesignateCell(cell);
                    if (!report.Accepted)
                    {
                        string why = GetZoneRejectionReason(report, growing);
                        return CommandResult.Fail($"cell ({x},{z}) cannot hold a {kind.ToLowerInvariant()} zone: {why}");
                    }
                    cells.Add(cell);
                }
            }
            return null;
        }

        private static string GetZoneRejectionReason(AcceptanceReport report, bool growing)
        {
            if (!string.IsNullOrEmpty(report.Reason)) return report.Reason;
            return growing ? "not fertile enough, fogged, blocked or at the map edge" : "fogged, blocked or at the map edge";
        }

        private static Zone CreateZoneInstance(Map map, bool stockpile, ThingDef plant)
        {
            if (stockpile)
            {
                return new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            }
            var growingZone = new Zone_Growing(map.zoneManager);
            if (plant != null) growingZone.SetPlantDefToGrow(plant);
            return growingZone;
        }

        public CommandResult DeleteZone(int zoneId)
        {
            Zone zone = Map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
            if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
            string label = zone.label;
            // 原生刪除路徑：Zone.Delete() 清格子並經 Deregister() 走 ZoneManager.DeregisterZone。
            zone.Delete();
            return CommandResult.Ok($"Deleted zone [{zoneId}] {label}. Stored items stay on the ground.");
        }

        public CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames)
        {
            Zone zone = Map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
            if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
            var stockpile = zone as Zone_Stockpile;
            if (stockpile == null) return CommandResult.Fail($"{zone.label} is not a stockpile and has no storage filter");
            List<string> allows = (allowDefNames ?? Array.Empty<string>()).ToList();
            List<string> disallows = (disallowDefNames ?? Array.Empty<string>()).ToList();
            if (allows.Count == 0 && disallows.Count == 0) return CommandResult.Fail("nothing to allow or disallow");
            var applied = new List<ThingDef>();
            var removed = new List<ThingDef>();
            foreach (string defName in allows.Concat(disallows))
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def == null) return CommandResult.Fail($"unknown stored item {defName}");
            }
            foreach (string defName in allows)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
                stockpile.settings.filter.SetAllow(def, true);
                applied.Add(def);
            }
            foreach (string defName in disallows)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
                stockpile.settings.filter.SetAllow(def, false);
                removed.Add(def);
            }
            var parts = new List<string>();
            if (applied.Count > 0) parts.Add("allows " + string.Join(", ", applied.Select(d => d.label).ToArray()));
            if (removed.Count > 0) parts.Add("disallows " + string.Join(", ", removed.Select(d => d.label).ToArray()));
            return CommandResult.Ok($"Zone [{zoneId}] {zone.label} now {string.Join("; ", parts.ToArray())}.");
        }

        public IReadOnlyList<WorkTableInfo> GetWorkTables()
        {
            return Map.listerBuildings.allBuildingsColonist
                .Where(b => b is IBillGiver)
                .Select(b => new WorkTableInfo
                {
                    Id = b.thingIDNumber,
                    Label = b.LabelCap,
                    X = b.Position.x,
                    Z = b.Position.z,
                    BillCount = ((IBillGiver)b).BillStack.Bills.Count
                })
                .ToList();
        }

        public IReadOnlyList<BillInfo> GetBills(int tableId)
        {
            IBillGiver giver = FindBillGiver(tableId);
            if (giver == null) return Array.Empty<BillInfo>();
            var bills = new List<BillInfo>();
            for (int i = 0; i < giver.BillStack.Bills.Count; i++)
            {
                Bill bill = giver.BillStack.Bills[i];
                bills.Add(new BillInfo { Index = i + 1, Label = bill.LabelCap, Repeat = RepeatLabel(bill) });
            }
            return bills;
        }

        private static string RepeatLabel(Bill bill)
        {
            var production = bill as Bill_Production;
            if (production == null) return string.Empty;
            if (production.repeatMode == BillRepeatModeDefOf.Forever) return "forever";
            if (production.repeatMode == BillRepeatModeDefOf.TargetCount) return $"until {production.targetCount}";
            if (production.repeatMode == BillRepeatModeDefOf.RepeatCount) return $"{production.repeatCount}x";
            return production.repeatMode?.label ?? string.Empty;
        }

        /// <summary>
        /// 只列這張台自己的配方：模組包常有好幾個配方產出同一物品、分屬不同工作台，
        /// 對全遊戲配方解析會撞名或挑到這張台做不了的那一個。
        /// </summary>
        public IReadOnlyList<DefInfo> GetTableRecipes(int tableId)
        {
            var table = FindBillGiver(tableId) as Thing;
            if (table == null) return Array.Empty<DefInfo>();
            return table.def.AllRecipes
                .Where(r => r.ProducedThingDef != null && (r.workerClass == null || !typeof(Recipe_Surgery).IsAssignableFrom(r.workerClass)))
                .Select(r => new DefInfo(r.defName, r.ProducedThingDef.label))
                .ToList();
        }

        public CommandResult AddBill(int tableId, string recipeDefName, int count)
        {
            IBillGiver giver = FindBillGiver(tableId);
            if (giver == null) return CommandResult.Fail($"no work table has id {tableId}");
            var table = (Thing)giver;
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
            if (recipe == null) return CommandResult.Fail($"unknown bill recipe {recipeDefName}");
            if (!table.def.AllRecipes.Contains(recipe))
                return CommandResult.Fail($"{table.LabelCap} cannot make {recipe.ProducedThingDef?.label ?? recipe.label}");
            if (!recipe.AvailableOnNow(table))
                return CommandResult.Fail($"{table.LabelCap} cannot make {recipe.ProducedThingDef?.label ?? recipe.label} yet: missing research or facilities");
            if (count < 1) return CommandResult.Fail($"count must be at least 1, not {count}");
            var bill = new Bill_Production(recipe)
            {
                repeatMode = BillRepeatModeDefOf.RepeatCount,
                repeatCount = count
            };
            giver.BillStack.AddBill(bill);
            return CommandResult.Ok($"Added bill [{giver.BillStack.Bills.Count}] {bill.LabelCap} x{count} on {table.LabelCap}.");
        }

        public CommandResult DeleteBill(int tableId, int index)
        {
            IBillGiver giver = FindBillGiver(tableId);
            if (giver == null) return CommandResult.Fail($"no work table has id {tableId}");
            var table = (Thing)giver;
            if (index < 1 || index > giver.BillStack.Bills.Count)
                return CommandResult.Fail($"bill [{index}] does not exist on {table.LabelCap}. List the bills again to get valid numbers.");
            Bill bill = giver.BillStack.Bills[index - 1];
            string label = bill.LabelCap;
            giver.BillStack.Delete(bill);
            return CommandResult.Ok($"Deleted bill {label} from {table.LabelCap}. Materials already gathered stay where they are.");
        }

        private static IBillGiver FindBillGiver(int id)
        {
            return Map.listerBuildings.allBuildingsColonist
                .Where(b => b is IBillGiver)
                .Cast<IBillGiver>()
                .FirstOrDefault(g => ((Thing)g).thingIDNumber == id);
        }

        public CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (pawn.workSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no work settings");
            WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workTypeDefName);
            if (workType == null) return CommandResult.Fail($"unknown work type {workTypeDefName}");
            if (pawn.WorkTypeIsDisabled(workType)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of {workType.label}");

            string note = string.Empty;
            bool needsManualPriorities = priority != 0 && priority != 3;
            if (needsManualPriorities && !Current.Game.playSettings.useWorkPriorities)
            {
                // 1/2/4 只有在手動優先級開啟時才有意義；否則遊戲只認勾選／不勾選。
                Current.Game.playSettings.useWorkPriorities = true;
                note = "Manual work priorities were turned on for the colony.";
            }
            pawn.workSettings.SetPriority(workType, priority);
            return CommandResult.Ok(note);
        }

        public CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (pawn.timetable == null) return CommandResult.Fail($"{pawn.LabelShort} has no schedule");
            TimeAssignmentDef assignment = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(assignmentDefName);
            if (assignment == null) return CommandResult.Fail($"unknown time assignment {assignmentDefName}");
            if (hour < 0 || hour > 23) return CommandResult.Fail("hour must be 0-23");
            pawn.timetable.SetAssignment(hour, assignment);
            return CommandResult.Ok();
        }

        public CommandResult AddDesignationOnCell(int x, int z, string designationDefName)
        {
            Map map = Map;
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            if (def == null) return CommandResult.Fail($"unknown designation {designationDefName}");
            if (map.designationManager.DesignationAt(cell, def) != null) return CommandResult.Ok();
            if (def.defName == MineDefName && cell.GetFirstMineable(map) == null) return CommandResult.Fail("nothing to mine there");

            map.designationManager.AddDesignation(new Designation(cell, def));
            return CommandResult.Ok();
        }

        public CommandResult AddDesignationOnThing(int thingId, string designationDefName)
        {
            Map map = Map;
            Thing thing = FindThing(thingId);
            if (thing == null) return CommandResult.Fail("object not found on the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            if (def == null) return CommandResult.Fail($"unknown designation {designationDefName}");
            if (map.designationManager.DesignationOn(thing, def) != null) return CommandResult.Ok();

            string blocked = WhyNotDesignatable(thing, def);
            if (blocked != null) return CommandResult.Fail(blocked);

            map.designationManager.AddDesignation(new Designation(thing, def));
            return CommandResult.Ok();
        }

        public CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName)
        {
            Map map = Map;
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            Designation existing = def == null ? null : map.designationManager.DesignationAt(cell, def);
            if (existing == null) return CommandResult.Fail("no such designation there");
            map.designationManager.RemoveDesignation(existing);
            return CommandResult.Ok();
        }

        public CommandResult RemoveDesignationOnThing(int thingId, string designationDefName)
        {
            Map map = Map;
            Thing thing = FindThing(thingId);
            if (thing == null) return CommandResult.Fail("object not found on the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            Designation existing = def == null ? null : map.designationManager.DesignationOn(thing, def);
            if (existing == null) return CommandResult.Fail("no such designation on it");
            map.designationManager.RemoveDesignation(existing);
            return CommandResult.Ok();
        }

        public CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName)
        {
            Map map = Map;
            BuildableDef def = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(buildableDefName)
                               ?? DefDatabase<TerrainDef>.GetNamedSilentFail(buildableDefName);
            if (def == null) return CommandResult.Fail($"unknown buildable {buildableDefName}");
            if (!def.BuildableByPlayer) return CommandResult.Fail($"{def.label} cannot be built by the player");
            if (!def.IsResearchFinished) return CommandResult.Fail($"{def.label} requires research that is not finished");

            ThingDef stuff = null;
            if (def.MadeFromStuff)
            {
                stuff = string.IsNullOrEmpty(stuffDefName)
                    ? GenStuff.DefaultStuffFor(def)
                    : DefDatabase<ThingDef>.GetNamedSilentFail(stuffDefName);
                if (stuff == null) return CommandResult.Fail($"unknown material {stuffDefName}");
                if (!GenStuff.AllowedStuffsFor(def).Contains(stuff)) return CommandResult.Fail($"{def.label} cannot be made of {stuff.label}");
            }

            var cell = new IntVec3(x, 0, z);
            var rot = new Rot4(rotation);
            AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, false, null, null, stuff);
            if (!report.Accepted) return CommandResult.Fail(string.IsNullOrEmpty(report.Reason) ? "cannot place there" : report.Reason);

            GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
            return CommandResult.Ok();
        }

        public CommandResult SetDrafted(int pawnId, bool drafted)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (pawn.drafter == null) return CommandResult.Fail($"{pawn.LabelShort} cannot be drafted");
            if (pawn.Downed && drafted) return CommandResult.Fail($"{pawn.LabelShort} is downed");
            pawn.drafter.Drafted = drafted;
            return CommandResult.Ok();
        }

        public CommandResult OrderMoveTo(int pawnId, int x, int z)
        {
            Map map = Map;
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (!pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is not drafted");
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
            if (!cell.Standable(map)) return CommandResult.Fail("cell is not standable");
            if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach that cell");

            Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.playerForced = true;
            return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
                ? CommandResult.Ok()
                : CommandResult.Fail($"{pawn.LabelShort} refused the move order");
        }

        public CommandResult OrderAttack(int pawnId, int targetId)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (!pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is not drafted");
            Thing target = FindThing(targetId);
            if (target == null) return CommandResult.Fail("target not found on the map");
            // 不打自己人、囚犯與友好派系：模型一次幻覺就能開火，那是外交事件。無派系的野生動物照舊可打（狩獵）。
            if (target.Faction == Faction.OfPlayer) return CommandResult.Fail($"{target.LabelShort} belongs to the colony");
            if ((target as Pawn)?.IsPrisonerOfColony == true) return CommandResult.Fail($"{target.LabelShort} is the colony's prisoner");
            if (target.Faction != null && !target.HostileTo(Faction.OfPlayer)) return CommandResult.Fail($"{target.LabelShort} belongs to a faction that is not hostile to the colony");

            bool ranged = pawn.equipment?.Primary?.def.IsRangedWeapon == true;
            Job job = JobMaker.MakeJob(ranged ? JobDefOf.AttackStatic : JobDefOf.AttackMelee, target);
            job.playerForced = true;
            return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
                ? CommandResult.Ok()
                : CommandResult.Fail($"{pawn.LabelShort} refused the attack order");
        }

        public CommandResult OrderJob(int pawnId, string job, int targetId)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (pawn.Downed) return CommandResult.Fail($"{pawn.LabelShort} is downed");
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return CommandResult.Fail($"{pawn.LabelShort} cannot use their hands");
            Thing target = FindThing(targetId);
            if (target == null) return CommandResult.Fail("target not found on the map");
            if (!pawn.CanReach(target, PathEndMode.ClosestTouch, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach {target.LabelShort}");

            switch (job)
            {
                case "rescue": return OrderRescue(pawn, target as Pawn);
                case "capture": return OrderCapture(pawn, target as Pawn);
                case "tend": return OrderTend(pawn, target as Pawn);
                case "strip": return OrderStrip(pawn, target);
                case "equip": return OrderEquip(pawn, target);
                case "enter_casket": return OrderEnterCasket(pawn, target);
                default: return CommandResult.Fail($"unknown job \"{job}\"");
            }
        }

        // 以下檢查照遊戲的右鍵選單（FloatMenuOptionProvider_*），只拒絕會惹惱派系或需要玩家另外確認的情況。
        private static CommandResult OrderRescue(Pawn pawn, Pawn patient)
        {
            if (patient == null) return CommandResult.Fail("only pawns can be rescued");
            // 任務地圖上等人救的難民走「提供幫助」，救了就加入。
            if (patient.mindState.WillJoinColonyIfRescued)
            {
                return TakeOrderedJob(pawn, JobMaker.MakeJob(JobDefOf.OfferHelp, patient), $"offering help to {patient.LabelShort}; they join once helped");
            }
            if (!patient.Downed) return CommandResult.Fail($"{patient.LabelShort} is not downed");
            if (patient.Faction != null && patient.Faction.HostileTo(Faction.OfPlayer)) return CommandResult.Fail($"{patient.LabelShort} is hostile; use capture to take them prisoner");
            if (patient.IsPrisonerOfColony || patient.IsSlaveOfColony) return CommandResult.Fail($"{patient.LabelShort} is the colony's prisoner or slave; wardens and doctors handle them");
            if (!HealthAIUtility.CanRescueNow(pawn, patient, forced: true)) return CommandResult.Fail($"{pawn.LabelShort} cannot rescue {patient.LabelShort} right now");
            if (patient.playerSettings?.medCare == MedicalCareCategory.NoCare) return CommandResult.Fail($"{patient.LabelShort}'s medical care is set to no care");

            Building_Bed bed = RestUtility.FindBedFor(patient, pawn, checkSocialProperness: false)
                               ?? RestUtility.FindBedFor(patient, pawn, checkSocialProperness: false, ignoreOtherReservations: true);
            if (bed == null) return CommandResult.Fail(patient.RaceProps.Animal ? "no free animal bed" : "no free bed for them; build or free a bed first");
            Job rescue = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
            rescue.count = 1;
            return TakeOrderedJob(pawn, rescue, $"carrying {patient.LabelShort} to {bed.LabelShort} at ({bed.Position.x}, {bed.Position.z})");
        }

        private static CommandResult OrderCapture(Pawn pawn, Pawn prisoner)
        {
            if (prisoner == null) return CommandResult.Fail("only pawns can be captured");
            if (!prisoner.CanBeCaptured()) return CommandResult.Fail($"{prisoner.LabelShort} cannot be captured (they must be downed and not already the colony's)");
            // 抓非敵對派系的人會惹惱整個派系，那是外交決定，不讓模型一句話就做。
            if (prisoner.Faction != null && prisoner.Faction != Faction.OfPlayer && !prisoner.Faction.Hidden && !prisoner.Faction.HostileTo(Faction.OfPlayer))
            {
                return CommandResult.Fail($"capturing {prisoner.LabelShort} would anger {prisoner.Faction.Name}; refused");
            }
            if (!HealthAIUtility.CanRescueNow(pawn, prisoner, forced: true)) return CommandResult.Fail($"{pawn.LabelShort} cannot capture {prisoner.LabelShort} right now");

            Building_Bed bed = RestUtility.FindBedFor(prisoner, pawn, false, false, GuestStatus.Prisoner)
                               ?? RestUtility.FindBedFor(prisoner, pawn, false, true, GuestStatus.Prisoner);
            if (bed == null) return CommandResult.Fail("no free prisoner bed; set a bed for prisoners first");
            Job capture = JobMaker.MakeJob(JobDefOf.Capture, prisoner, bed);
            capture.count = 1;
            string note = $"taking {prisoner.LabelShort} to {bed.LabelShort} at ({bed.Position.x}, {bed.Position.z})";
            if (!prisoner.guest.Recruitable) note += "; they are unrecruitable";
            return TakeOrderedJob(pawn, capture, note);
        }

        private static CommandResult OrderTend(Pawn doctor, Pawn patient)
        {
            if (!ValidateTendOrder(doctor, patient, out string failReason))
            {
                return CommandResult.Fail(failReason);
            }

            // 找藥與建工作同 WorkGiver_Tend：藥在別人身上（背包、架子）時第三個目標是它的持有者。
            Thing medicine = HealthAIUtility.FindBestMedicine(doctor, patient);
            Job tend = CreateTendJob(patient, medicine);
            tend.count = 1;
            tend.draftedTend = doctor.Drafted;
            return TakeOrderedJob(doctor, tend, $"tending {patient.LabelShort} " + (medicine == null ? "without medicine" : "with " + medicine.def.label));
        }

        private static bool ValidateTendOrder(Pawn doctor, Pawn patient, out string failReason)
        {
            failReason = null;
            if (patient == null) { failReason = "only pawns can be tended"; return false; }
            if (doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)) { failReason = $"{doctor.LabelShort} is incapable of doctoring"; return false; }
            if (!patient.health.HasHediffsNeedingTend()) { failReason = $"{patient.LabelShort} does not need tending"; return false; }
            bool ours = patient.IsColonist || patient.IsQuestLodger() || patient.IsPrisonerOfColony || patient.IsSlaveOfColony
                        || (patient.Faction == Faction.OfPlayer && patient.IsAnimal);
            if (!patient.Downed && (patient.HostileTo(doctor.Faction) || !ours)) { failReason = $"{patient.LabelShort} will not accept treatment"; return false; }
            if (patient == doctor && doctor.playerSettings != null && !doctor.playerSettings.selfTend) { failReason = $"self-tend is disabled for {doctor.LabelShort}"; return false; }
            if (patient.InAggroMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria)) { failReason = $"{patient.LabelShort} is in an aggressive mental state"; return false; }
            return true;
        }

        private static Job CreateTendJob(Pawn patient, Thing medicine)
        {
            if (medicine == null)
            {
                return JobMaker.MakeJob(JobDefOf.TendPatient, patient);
            }
            if (medicine.SpawnedParentOrMe != medicine)
            {
                return JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine, medicine.SpawnedParentOrMe);
            }
            return JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
        }

        private static CommandResult OrderStrip(Pawn pawn, Thing target)
        {
            if (!StrippableUtility.CanBeStrippedByColony(target)) return CommandResult.Fail($"{target.LabelShort} cannot be stripped by the colony");
            if (target is Pawn living && living.HasExtraHomeFaction()) return CommandResult.Fail($"{target.LabelShort} is quest-related");
            Pawn owner = target as Pawn ?? (target as Corpse)?.InnerPawn;
            if (owner?.Faction != null && owner.Faction != Faction.OfPlayer && !owner.Faction.HostileTo(Faction.OfPlayer))
            {
                return CommandResult.Fail($"stripping {target.LabelShort} would hurt relations with {owner.Faction.Name}; refused");
            }
            return TakeOrderedJobUnforbiddingTarget(pawn, JobMaker.MakeJob(JobDefOf.Strip, target), target,
                $"stripping {target.LabelShort}");
        }

        private static CommandResult OrderEquip(Pawn pawn, Thing item)
        {
            if (pawn.equipment == null || !item.HasComp<CompEquippable>()) return CommandResult.Fail($"{item.LabelShort} is not a weapon or equipment");
            if (item.def.IsWeapon && pawn.WorkTagIsDisabled(WorkTags.Violent)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of violence");
            if (item.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of shooting");
            if (item.IsBurning()) return CommandResult.Fail($"{item.LabelShort} is burning");
            if (pawn.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanEquip(item, pawn)) return CommandResult.Fail($"{pawn.LabelShort} cannot equip it (quest-related)");
            if (!EquipmentUtility.CanEquip(item, pawn, out string reason, checkBonded: false)) return CommandResult.Fail(reason);
            // 靈魂綁定武器在遊戲裡要玩家按確認，不代答。
            if (EquipmentUtility.AlreadyBondedToWeapon(item, pawn) || !EquipmentUtility.GetPersonaWeaponConfirmationText(item, pawn).NullOrEmpty())
            {
                return CommandResult.Fail($"{item.LabelShort} is a persona weapon; bonding needs the player's confirmation in-game");
            }
            return TakeOrderedJobUnforbiddingTarget(pawn, JobMaker.MakeJob(JobDefOf.Equip, item), item,
                $"equipping {item.LabelShort}");
        }

        private static CommandResult OrderEnterCasket(Pawn pawn, Thing target)
        {
            if (!(target is Building_CryptosleepCasket casket)) return CommandResult.Fail($"{target.LabelShort} is not a cryptosleep casket");
            if (pawn.IsQuestLodger()) return CommandResult.Fail("guests cannot use cryptosleep caskets");
            if (casket.HasAnyContents) return CommandResult.Fail($"{casket.LabelShort} is already occupied");
            if (!pawn.CanReach(casket, PathEndMode.InteractionCell, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach {casket.LabelShort}");
            // 心靈羈絆的伴侶就在附近時，遊戲會跳確認（分開會有負面效果），不代答。
            if (ModsConfig.BiotechActive
                && pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.PsychicBond) is Hediff_PsychicBond bond
                && ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson(pawn, bond))
            {
                return CommandResult.Fail($"entering would separate {pawn.LabelShort} from their psychic bond partner; that needs the player's confirmation in-game");
            }
            return TakeOrderedJob(pawn, JobMaker.MakeJob(JobDefOf.EnterCryptosleepCasket, casket),
                $"entering {casket.LabelShort} at ({casket.Position.x}, {casket.Position.z})");
        }

        private static CommandResult TakeOrderedJobUnforbiddingTarget(Pawn pawn, Job job, Thing target, string note)
        {
            bool wasForbidden = target.IsForbidden(Faction.OfPlayer);
            target.SetForbidden(false, warnOnFail: false);
            bool accepted = false;
            try
            {
                CommandResult result = TakeOrderedJob(pawn, job, note);
                accepted = result.Success;
                return result;
            }
            finally
            {
                if (!accepted && wasForbidden) target.SetForbidden(true, warnOnFail: false);
            }
        }

        private static CommandResult TakeOrderedJob(Pawn pawn, Job job, string note)
        {
            job.playerForced = true;
            return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
                ? CommandResult.Ok(note)
                : CommandResult.Fail($"{pawn.LabelShort} refused the order");
        }

        public CommandResult SetForbidden(CellRectSpec? area, string labelContains, bool forbidden)
        {
            Map map = Map;
            bool hasNeedle = !string.IsNullOrEmpty(labelContains);
            var changed = new List<Thing>();
            // HaulableEver 涵蓋物品與屍體；迷霧裡的東西玩家還看不到，不動。
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => CanSetForbidden(t, area, hasNeedle, labelContains, forbidden)).ToList())
            {
                t.SetForbidden(forbidden, warnOnFail: false);
                changed.Add(t);
            }
            if (changed.Count == 0)
            {
                string state = forbidden ? "allowed" : "forbidden";
                return CommandResult.Fail($"no visible item or corpse there is currently {state}" + (hasNeedle ? $" matching \"{labelContains}\"" : string.Empty));
            }
            IEnumerable<string> labels = changed.Take(10).Select(t => t.LabelShort);
            return CommandResult.Ok($"{changed.Count} item(s): {string.Join(", ", labels)}{(changed.Count > 10 ? ", …" : string.Empty)}");
        }

        private static bool CanSetForbidden(Thing t, CellRectSpec? area, bool hasNeedle, string labelContains, bool targetForbidden)
        {
            if (!t.Spawned || t.Fogged()) return false;
            if (area.HasValue && !area.Value.Contains(t.Position.x, t.Position.z)) return false;
            if (t.TryGetComp<CompForbiddable>() == null || t.IsForbidden(Faction.OfPlayer) == targetForbidden) return false;
            if (hasNeedle && !MatchesLabel(t, labelContains)) return false;
            return true;
        }

        public CommandResult ExecuteTrade(TradeRequest request)
        {
            CommandResult linesResult = ValidateTradeLines(request, out IReadOnlyList<TradeLine> buys, out IReadOnlyList<TradeLine> sells);
            if (linesResult != null) return linesResult;

            // 玩家手動開著交易窗時不搶 TradeSession，否則對方的結算會被掀掉。
            if (TradeSession.Active) return CommandResult.Fail("a trade window is already open. Close it first.");

            ITrader trader = FindTrader(request.TraderId);
            CommandResult traderResult = ValidateTrader(trader, request.TraderId);
            if (traderResult != null) return traderResult;

            Pawn negotiator = FindColonist(request.NegotiatorPawnId);
            CommandResult negResult = ValidateNegotiator(negotiator, trader);
            if (negResult != null) return negResult;

            // 之後全走遊戲的原生結算：價格、銀兩與物品交割都由 TradeDeal 處理，
            // 這裡只做「先驗證全部、再一次交割」，餘額或庫存不足整筆不做。
            TradeSession.SetupWith(trader, negotiator, false);
            try
            {
                TradeDeal deal = TradeSession.deal;
                CommandResult allocResult = ApplyAllocations(deal, trader, buys, sells);
                if (allocResult != null) return allocResult;

                CommandResult affordResult = ValidateAffordability(deal, trader, out int colonySilver, out int postDealSilver);
                if (affordResult != null) return affordResult;

                // 交割前先把收據算好：銀兩實際變動取自貨幣行的公式值，品項數量即交割數量。
                string receipt = $"Traded with {TraderLabel(trader)} (negotiated by {negotiator.LabelShort}): " +
                    string.Join("; ", ReceiptParts(buys, sells)) +
                    $". Colony silver {colonySilver} -> {postDealSilver}.";
                if (!deal.TryExecute(out _))
                {
                    return CommandResult.Fail("the trade was blocked before anything changed hands.");
                }
                return CommandResult.Ok(receipt);
            }
            finally
            {
                TradeSession.Close();
            }
        }

        private static CommandResult ApplyAllocations(TradeDeal deal, ITrader trader, IReadOnlyList<TradeLine> buys, IReadOnlyList<TradeLine> sells)
        {
            var buyAllocations = new Dictionary<Tradeable, int>();
            var sellAllocations = new Dictionary<Tradeable, int>();
            foreach (TradeLine line in buys)
            {
                string fail = Allocate(buyAllocations, deal, line, true, TraderLabel(trader));
                if (fail != null) return CommandResult.Fail(fail);
            }
            foreach (TradeLine line in sells)
            {
                string fail = Allocate(sellAllocations, deal, line, false, TraderLabel(trader));
                if (fail != null) return CommandResult.Fail(fail);
            }
            foreach (Tradeable row in buyAllocations.Keys.Concat(sellAllocations.Keys).Distinct())
            {
                row.AdjustTo(row.CountToTransfer + Allocated(buyAllocations, row) - Allocated(sellAllocations, row));
            }
            return null;
        }

        private static CommandResult ValidateAffordability(TradeDeal deal, ITrader trader, out int colonySilver, out int postDealSilver)
        {
            deal.UpdateCurrencyCount();
            Tradeable currency = deal.CurrencyTradeable;
            colonySilver = currency != null ? currency.CountHeldBy(Transactor.Colony) : 0;
            postDealSilver = currency != null ? currency.CountPostDealFor(Transactor.Colony) : 0;
            if (currency == null || postDealSilver < 0)
            {
                int need = colonySilver - postDealSilver;
                return CommandResult.Fail($"the colony cannot afford this: needs {need} silver but has {colonySilver}.");
            }
            if (!deal.DoesTraderHaveEnoughSilver())
            {
                return CommandResult.Fail($"{TraderLabel(trader)} cannot afford this: needs more silver to pay for the colony's goods.");
            }
            return null;
        }

        public CommandResult FormCaravan(CaravanRequest request)
        {
            if (request == null) return CommandResult.Fail("no caravan requested");
            List<int> ids = (request.PawnIds ?? Array.Empty<int>()).ToList();
            if (ids.Count == 0) return CommandResult.Fail("no colonists named. Give at least one member, e.g. \"Ali, Bob\".");
            Direction8Way dir;
            string display;
            if (!TryParseHeading(request.Heading, out dir, out display))
                return CommandResult.Fail($"unknown heading \"{request.Heading}\". Use N, NE, E, SE, S, SW, W or NW.");
            Map map = Map;
            var pawns = new List<Pawn>();
            foreach (int id in ids)
            {
                Pawn pawn = FindColonist(id);
                if (pawn == null) return CommandResult.Fail($"no colonist has id {id} (prisoners and visitors cannot form a caravan)");
                if (pawn.Dead) return CommandResult.Fail($"{pawn.LabelShort} is dead");
                if (pawn.Downed) return CommandResult.Fail($"{pawn.LabelShort} is downed and cannot join a caravan");
                if (pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is drafted; undraft them first");
                pawns.Add(pawn);
            }
            Caravan caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(pawns, Faction.OfPlayer, map.Tile, dir, PlanetTile.Invalid);
            if (caravan == null) return CommandResult.Fail("the game could not form a caravan from the current map");
            string names = string.Join(", ", pawns.Select(p => p.LabelShort).ToArray());
            return CommandResult.Ok($"Formed {caravan.Name} with {names}, heading {display}.");
        }

        private static bool TryParseHeading(string heading, out Direction8Way dir, out string display)
        {
            string needle = (heading ?? string.Empty).Trim().ToUpperInvariant();
            switch (needle)
            {
                case "N":
                case "NORTH": dir = Direction8Way.North; display = "north"; return true;
                case "NE":
                case "NORTHEAST":
                case "NORTH-EAST": dir = Direction8Way.NorthEast; display = "north-east"; return true;
                case "E":
                case "EAST": dir = Direction8Way.East; display = "east"; return true;
                case "SE":
                case "SOUTHEAST":
                case "SOUTH-EAST": dir = Direction8Way.SouthEast; display = "south-east"; return true;
                case "S":
                case "SOUTH": dir = Direction8Way.South; display = "south"; return true;
                case "SW":
                case "SOUTHWEST":
                case "SOUTH-WEST": dir = Direction8Way.SouthWest; display = "south-west"; return true;
                case "W":
                case "WEST": dir = Direction8Way.West; display = "west"; return true;
                case "NW":
                case "NORTHWEST":
                case "NORTH-WEST": dir = Direction8Way.NorthWest; display = "north-west"; return true;
                default: dir = Direction8Way.Invalid; display = string.Empty; return false;
            }
        }

        /// <summary>
        /// 把一行買入賣出需求按方向分到同 def 的各行（同 def 可能因品質／材料分多行）。
        /// 全部吃不下回傳原因字串；成功回傳 null。只算數不動遊戲狀態。
        /// </summary>
        private static string Allocate(Dictionary<Tradeable, int> allocations, TradeDeal deal, TradeLine line, bool buy, string traderLabel)
        {
            List<Tradeable> rows = deal.AllTradeables
                .Where(r => !r.IsCurrency && r.ThingDef != null && string.Equals(r.ThingDef.defName, line.DefName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            int rest = line.Count;
            foreach (Tradeable row in rows)
            {
                if (rest <= 0) break;
                int capacity = buy ? row.GetMaximumToTransfer() : -row.GetMinimumToTransfer();
                int room = capacity - Allocated(allocations, row);
                int take = Math.Min(rest, Math.Max(0, room));
                if (take <= 0) continue;
                allocations[row] = Allocated(allocations, row) + take;
                rest -= take;
            }
            if (rest > 0)
            {
                int available = line.Count - rest;
                return buy
                    ? $"{traderLabel} only has {available} {ItemLabel(line.DefName)} for sale, not {line.Count}."
                    : $"the colony only has {available} sellable {ItemLabel(line.DefName)}, not {line.Count}.";
            }
            return null;
        }

        private static int Allocated(Dictionary<Tradeable, int> allocations, Tradeable row)
        {
            return allocations.TryGetValue(row, out int n) ? n : 0;
        }

        private static IEnumerable<string> ReceiptParts(IReadOnlyList<TradeLine> buys, IReadOnlyList<TradeLine> sells)
        {
            if (buys.Count > 0) yield return "bought " + string.Join(", ", buys.Select(l => $"{ItemLabel(l.DefName)} ({l.DefName}) x{l.Count}"));
            if (sells.Count > 0) yield return "sold " + string.Join(", ", sells.Select(l => $"{ItemLabel(l.DefName)} ({l.DefName}) x{l.Count}"));
        }

        private static string ItemLabel(string defName)
        {
            return DefDatabase<ThingDef>.GetNamedSilentFail(defName)?.label ?? defName;
        }

        private static string TraderLabel(ITrader trader)
        {
            TradeShip ship = trader as TradeShip;
            if (ship != null) return ship.FullTitle;
            Pawn pawn = trader as Pawn;
            if (pawn != null) return pawn.LabelShort;
            return trader.TraderName;
        }

        private static CommandResult ValidateTradeLines(TradeRequest request, out IReadOnlyList<TradeLine> buys, out IReadOnlyList<TradeLine> sells)
        {
            buys = request?.Buys ?? Array.Empty<TradeLine>();
            sells = request?.Sells ?? Array.Empty<TradeLine>();
            if (request == null) return CommandResult.Fail("no trade requested");
            if (string.IsNullOrEmpty(request.TraderId)) return CommandResult.Fail("no trader specified");
            if (buys.Count == 0 && sells.Count == 0) return CommandResult.Fail("nothing to buy or sell");
            foreach (TradeLine line in buys.Concat(sells))
            {
                if (string.Equals(line.DefName, ThingDefOf.Silver.defName, StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("silver moves automatically as payment; do not list it");
                if (line.Count <= 0) return CommandResult.Fail($"invalid count {line.Count} for {line.DefName}");
            }
            return null;
        }

        private static CommandResult ValidateTrader(ITrader trader, string traderId)
        {
            if (trader == null) return CommandResult.Fail($"trader \"{traderId}\" was not found. List the traders again to get a valid id.");
            if (!trader.CanTradeNow) return CommandResult.Fail($"{TraderLabel(trader)} cannot trade right now.");
            if (trader.TradeCurrency != TradeCurrency.Silver) return CommandResult.Fail($"{TraderLabel(trader)} does not trade in silver.");
            return null;
        }

        private static CommandResult ValidateNegotiator(Pawn negotiator, ITrader trader)
        {
            if (negotiator == null) return CommandResult.Fail("negotiator was not found among the colonists.");
            if (negotiator.Downed) return CommandResult.Fail($"{negotiator.LabelShort} is downed and cannot negotiate.");
            // 沿用原版交易的門檻：要能說話、社交沒被禁用；軌道商人還要一台有電的通訊台。
            if (negotiator.health?.capacities?.CapableOf(PawnCapacityDefOf.Talking) == false)
                return CommandResult.Fail($"{negotiator.LabelShort} is incapable of talking and cannot negotiate.");
            if (negotiator.skills?.GetSkill(SkillDefOf.Social)?.TotallyDisabled == true)
                return CommandResult.Fail($"{negotiator.LabelShort} is incapable of social work and cannot negotiate.");
            if (trader is TradeShip && !Map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>().Any(c => c.CanUseCommsNow))
                return CommandResult.Fail("trading with an orbital trader needs a powered comms console.");
            return null;
        }

        private static ITrader FindTrader(string id)
        {
            Map map = Map;
            TradeShip ship = map.passingShipManager.passingShips.OfType<TradeShip>()
                .FirstOrDefault(s => s.GetUniqueLoadID() == id);
            if (ship != null) return ship;

            return map.mapPawns.AllPawnsSpawned
                .FirstOrDefault(p => p.trader != null && p.trader.traderKind != null && p.GetUniqueLoadID() == id);
        }

        // ---- 轉換 ----

        private static ColonistInfo ToColonist(Pawn pawn)
        {
            var triple = pawn.Name as NameTriple;
            string nick = triple?.Nick ?? string.Empty;
            string first = triple?.First ?? pawn.Name?.ToStringShort ?? pawn.LabelShort;
            bool hasDistinctNick = !string.IsNullOrEmpty(nick) && nick != first;
            var unreadable = new List<string>();

            return new ColonistInfo
            {
                Id = pawn.thingIDNumber,
                Name = pawn.LabelShort,
                Nickname = hasDistinctNick ? nick : string.Empty,
                FullName = pawn.Name?.ToStringFull ?? pawn.LabelShort,
                Age = pawn.ageTracker?.AgeBiologicalYears ?? 0,
                X = pawn.Position.x,
                Z = pawn.Position.z,
                HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Downed = pawn.Downed,
                MoodPercent = pawn.needs?.mood?.CurLevelPercentage ?? 0f,
                MinorBreakThreshold = pawn.mindState?.mentalBreaker?.BreakThresholdMinor ?? 0f,
                InMentalState = pawn.InMentalState,
                MentalStateLabel = pawn.MentalStateDef?.label ?? string.Empty,
                CurrentActivity = SafeReport(pawn),
                Drafted = pawn.Drafted,
                MedicalCare = pawn.playerSettings != null ? pawn.playerSettings.medCare.ToString() : string.Empty,
                // 以下各段會跑到其他模組的程式（武器、需求、想法、技能、工作類型），各自隔離：
                // 一段丟例外只讓那段變空，殖民者仍列得出來、叫得動。
                Equipment = ReadOrDefault(pawn, unreadable, "equipment", () => pawn.equipment?.Primary?.LabelCap ?? string.Empty, string.Empty),
                Needs = ReadOrDefault(pawn, unreadable, "needs", () => pawn.needs?.AllNeeds.Select(n => new NeedInfo
                {
                    Label = n.LabelCap,
                    Percent = n.CurLevelPercentage
                }).ToList() ?? (IReadOnlyList<NeedInfo>)Array.Empty<NeedInfo>(), Array.Empty<NeedInfo>()),
                Thoughts = ReadOrDefault(pawn, unreadable, "thoughts", () => pawn.needs?.mood?.thoughts?.memories?.Memories
                    .Select(t => t.LabelCap).Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(12).ToList()
                    ?? (IReadOnlyList<string>)Array.Empty<string>(), Array.Empty<string>()),
                Skills = ReadOrDefault(pawn, unreadable, "skills", () => pawn.skills?.skills.Select(s => new SkillInfo
                {
                    DefName = s.def.defName,
                    Label = s.def.label,
                    Level = s.Level,
                    Passion = (int)s.passion,
                    Disabled = s.TotallyDisabled
                }).ToList() ?? (IReadOnlyList<SkillInfo>)Array.Empty<SkillInfo>(), Array.Empty<SkillInfo>()),
                WorkPriorities = pawn.workSettings == null
                    ? (IReadOnlyList<WorkPriorityInfo>)Array.Empty<WorkPriorityInfo>()
                    : ReadOrDefault<IReadOnlyList<WorkPriorityInfo>>(pawn, unreadable, ColonistInfo.WorkPrioritiesPart, () => DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).Select(w => ToWorkPriority(pawn, w)).ToList(),
                        Array.Empty<WorkPriorityInfo>()),
                Timetable = pawn.timetable == null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : Enumerable.Range(0, 24).Select(h => pawn.timetable.GetAssignment(h)?.defName ?? string.Empty).ToList(),
                // 初始設定式依序求值，放最後才收得到上面每一段的失敗。
                UnreadableParts = unreadable
            };
        }

        /// <summary>
        /// 讀一段可能跑到其他模組程式的資料；丟例外就回傳預設值、把段名記進 <paramref name="unreadable"/>
        /// （工具據此告訴模型「讀不到」而非「沒有」），並對這個 pawn 的這一段只警告一次。
        /// </summary>
        private static T ReadOrDefault<T>(Pawn pawn, List<string> unreadable, string part, Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                WarnModError(pawn, part, ex);
                unreadable.Add(part);
                return fallback;
            }
        }

        private static void WarnModError(Pawn pawn, string part, Exception ex)
        {
            // ThingID 只是 defName + 編號，不會再跑進出錯的模組程式。
            string who = pawn?.ThingID ?? "a pawn";
            Log.WarningOnce($"RimAgent: could not read {part} of {who}, most likely because of another mod; skipped it. {ex}",
                ("RimAgent:" + part + ":" + who).GetHashCode());
        }

        private static WorkPriorityInfo ToWorkPriority(Pawn pawn, WorkTypeDef w)
        {
            bool disabled = pawn.WorkTypeIsDisabled(w);
            return new WorkPriorityInfo
            {
                WorkTypeDefName = w.defName,
                Label = w.labelShort ?? w.label,
                Priority = disabled ? 0 : pawn.workSettings.GetPriority(w),
                Disabled = disabled
            };
        }

        private static string SafeReport(Pawn pawn)
        {
            try
            {
                return pawn.jobs?.curDriver?.GetReport() ?? pawn.CurJob?.def.label ?? string.Empty;
            }
            catch (Exception)
            {
                // 某些 JobDriver 的 GetReport 會在狀態不完整時丟例外；活動描述只是輔助資訊。
                return pawn.CurJob?.def.label ?? string.Empty;
            }
        }

        private static bool IsNearMentalBreak(Pawn pawn)
        {
            Need_Mood mood = pawn.needs?.mood;
            MentalBreaker breaker = pawn.mindState?.mentalBreaker;
            return mood != null && breaker != null && mood.CurLevel < breaker.BreakThresholdMinor;
        }

        private static ResearchProjectInfo ToResearch(ResearchProjectDef def)
        {
            return new ResearchProjectInfo
            {
                DefName = def.defName,
                Label = def.label,
                ProgressPercent = def.ProgressPercent,
                Cost = def.CostApparent
            };
        }

        private static ThingKind? KindOf(Thing thing)
        {
            if (thing is Pawn) return ThingKind.Pawn;
            if (thing is Plant) return ThingKind.Plant;
            if (thing.def.mineable) return ThingKind.Mineable;
            if (thing is Building) return ThingKind.Building;
            if (thing.def.category == ThingCategory.Item) return ThingKind.Item;
            return null;
        }

        private static ThingInfo ToThing(Thing thing, Map map)
        {
            var designations = map.designationManager.AllDesignationsOn(thing).Select(d => d.def.defName).ToList();
            if (thing.def.mineable)
            {
                designations.AddRange(map.designationManager.AllDesignationsAt(thing.Position).Select(d => d.def.defName));
            }
            return new ThingInfo
            {
                Id = thing.thingIDNumber,
                DefName = thing.def.defName,
                Label = thing.LabelCap,
                Kind = KindOf(thing) ?? (thing is Blueprint ? ThingKind.Building : ThingKind.Any),
                X = thing.Position.x,
                Z = thing.Position.z,
                StackCount = thing.stackCount,
                PlayerOwned = thing.Faction == Faction.OfPlayer,
                Harvestable = (thing as Plant)?.HarvestableNow ?? false,
                Wild = thing is Pawn pawn && IsWildAnimal(pawn),
                Forbidden = thing.IsForbidden(Faction.OfPlayer),
                Designations = designations.Distinct().ToList()
            };
        }

        private static string WhyNotDesignatable(Thing thing, DesignationDef def)
        {
            switch (def.defName)
            {
                case "CutPlant":
                case "HarvestPlant":
                    return thing is Plant ? null : $"{thing.LabelCap} is not a plant";
                case "Deconstruct":
                    if (!(thing is Building)) return $"{thing.LabelCap} is not a building";
                    if (thing.Faction != Faction.OfPlayer) return $"{thing.LabelCap} does not belong to the colony";
                    if (thing.def.building != null && !thing.def.building.IsDeconstructible) return $"{thing.LabelCap} cannot be deconstructed";
                    return null;
                case "Haul":
                    return thing.def.EverHaulable ? null : $"{thing.LabelCap} cannot be hauled";
                case "Hunt":
                    return thing is Pawn pawn && IsWildAnimal(pawn)
                        ? null : $"{thing.LabelCap} is not a huntable wild animal";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 無派系的動物。用 RaceProps.Animal 而非「不是類人」，無派系的實體、模組機械生物不算可狩獵；
        /// 與原版 Designator_Hunt（AnimalOrWildMan）一致，非血肉野獸原版也不能狩獵。
        /// </summary>
        private static bool IsWildAnimal(Pawn pawn)
        {
            return pawn.RaceProps.Animal && pawn.Faction == null;
        }

        private static Pawn FindColonist(int id)
        {
            return Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
        }

        private static Thing FindThing(int id)
        {
            return Map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id && t.Spawned);
        }

        public ScreenshotInfo TakeScreenshot(string filename = null)
        {
            try
            {
                string baseName = Tools.ObserveTools.ResolveScreenshotName(filename);
                string folder = GenFilePaths.ScreenshotFolderPath;
                string safeName = baseName;
                string fullPath = System.IO.Path.Combine(folder, safeName + ".png");

                int counter = 1;
                while (System.IO.File.Exists(fullPath))
                {
                    safeName = $"{baseName}_{counter++}";
                    fullPath = System.IO.Path.Combine(folder, safeName + ".png");
                }

                ScreenshotTaker.TakeNonSteamShot(safeName);

                return new ScreenshotInfo
                {
                    Success = true,
                    FilePath = fullPath,
                    Width = UnityEngine.Screen.width,
                    Height = UnityEngine.Screen.height
                };
            }
            catch (Exception ex)
            {
                Log.Error("RimAgent: TakeScreenshot failed: " + ex);
                return new ScreenshotInfo
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }
}
