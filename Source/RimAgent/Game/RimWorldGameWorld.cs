using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
                    downed = IsMedicalEmergency(pawn);
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
            fireCount += FireCount(map);
            if (!anyPower && HasWorkingPower(map)) anyPower = true;
            int bedlessBefore = accumulator.Bedless;
            accumulator.AccumulateColonists(map.mapPawns.FreeColonistsSpawned);
            // 床蓋好之後殖民者要等到去睡覺才會認領；只看認領的話，床已經夠了訊號仍報沒床，
            // 排程器就會在問題其實已解決時重試。同一張地圖上還沒人認領的床位先抵掉沒床的人。
            int mapBedless = accumulator.Bedless - bedlessBefore;
            if (mapBedless > 0) accumulator.Bedless -= Math.Min(mapBedless, FreeColonistBedSlots(map));
        }

        /// <summary>殖民者可以睡、還沒被認領的床位數（雙人床算兩位；不含囚犯、奴隸、醫療與嬰兒床）。</summary>
        private static int FreeColonistBedSlots(Map map)
        {
            int free = 0;
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (!(building is Building_Bed bed) || !bed.def.building.bed_humanlike) continue;
                if (!bed.ForColonists || bed.Medical || bed.ForHumanBabies) continue;
                free += Math.Max(0, bed.SleepingSlotsCount - bed.OwnersForReading.Count);
            }
            return free;
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

        public IReadOnlyList<int> GetColonistsInMedicalEmergency()
        {
            var ids = new List<int>();
            foreach (Pawn pawn in Map.mapPawns.FreeColonistsSpawned)
            {
                // 與排程器訊號一樣逐個隔離：其他模組的 pawn 丟例外就跳過，不讓等待工具整個失敗。
                try
                {
                    if (IsMedicalEmergency(pawn)) ids.Add(pawn.thingIDNumber);
                }
                catch (Exception ex)
                {
                    WarnModError(pawn, "wait checks", ex);
                }
            }
            return ids;
        }

        public int GetFireCount() => FireCount(Map);

        /// <summary>排程器的火災訊號與 wait_for_time 的提早結束共用同一個算法。</summary>
        private static int FireCount(Map map) => map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;

        /// <summary>流血到失血致死不到這麼久才算急診；小擦傷算出來要十幾天，不該打斷等待或起急診任務。</summary>
        private const int LifeThreateningBleedTicks = GenDate.TicksPerDay;

        /// <summary>
        /// 急診的判定，排程器訊號與 wait_for_time 的提早結束共用：倒地，或流血到一天內會失血致死。
        /// 麻醉中的不算：手術時病人本來就會倒地流血，那是排好的醫療行為，不是意外。
        /// </summary>
        private static bool IsMedicalEmergency(Pawn pawn)
        {
            if (pawn.health?.hediffSet == null) return pawn.Downed;
            if (pawn.health.hediffSet.HasHediff(HediffDefOf.Anesthetic)) return false;
            return pawn.Downed || HealthUtility.TicksUntilDeathDueToBloodLoss(pawn) < LifeThreateningBleedTicks;
        }

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

        private const string QuestNotFound = "no available or active quest with that id. Call list_quests for the current ids.";
        private static readonly Regex MarkupTags = new Regex("<[^>]+>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>任務分頁會列出的任務：可接受與進行中，不含已結束、已過期與隱藏的。</summary>
        public IReadOnlyList<QuestInfo> GetQuests()
        {
            var result = new List<QuestInfo>();
            foreach (Quest quest in Find.QuestManager.QuestsListForReading)
            {
                if (quest.hidden || quest.hiddenInUI) continue;
                if (quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing) continue;
                result.Add(ToQuestInfo(quest));
            }
            return result;
        }

        private static QuestInfo ToQuestInfo(Quest quest)
        {
            bool available = quest.State == QuestState.NotYetAccepted;
            var info = new QuestInfo
            {
                Id = quest.id,
                Name = PlainText(quest.name),
                State = available ? QuestInfo.AvailableState : QuestInfo.ActiveState,
                Dismissed = quest.dismissed,
                Description = QuestDescription(quest),
                AccepterName = quest.AccepterPawnLabelCap ?? string.Empty,
                RequiresAccepter = quest.RequiresAccepter,
                RewardOptions = QuestRewardOptions(quest)
            };
            if (!available)
            {
                info.Deadlines = quest.PartsListForReading.OfType<QuestPartActivable>()
                    .Where(p => p.State == QuestPartState.Enabled && !p.ExpiryInfoPart.NullOrEmpty())
                    .OrderBy(p => p.EnableTick)
                    .Select(p => PlainText(p.ExpiryInfoPart)).ToList();
                return info;
            }
            if (quest.TicksUntilExpiry > 0) info.ExpiresInTicks = quest.TicksUntilExpiry;
            AcceptanceReport report = QuestUtility.CanAcceptQuest(quest);
            info.CanAccept = report.Accepted;
            if (!report.Accepted) info.UnmetRequirements = UnmetQuestRequirements(quest, report);
            if (quest.RequiresAccepter || info.RewardOptions.Any(o => o.RequiresAccepter))
            {
                info.Accepters = QuestAccepterCandidates(quest)
                    .Select(p => new QuestAccepterInfo
                    {
                        Id = p.thingIDNumber,
                        Name = p.LabelShort,
                        FullName = p.Name?.ToStringFull ?? p.LabelShort,
                        Warning = AccepterWarning(quest, p)
                    }).ToList();
            }
            return info;
        }

        /// <summary>照任務分頁的說明區：任務描述，接著是已啟用的各段落補充（依啟用時間）。</summary>
        private static string QuestDescription(Quest quest)
        {
            var sb = new StringBuilder();
            if (!quest.description.RawText.NullOrEmpty()) sb.Append(quest.description.Resolve());
            foreach (QuestPart part in quest.PartsListForReading.OrderBy(p => p is QuestPartActivable a ? a.EnableTick : 0))
            {
                if (part is QuestPartActivable activable && activable.State != QuestPartState.Enabled) continue;
                string text = part.DescriptionPart;
                if (text.NullOrEmpty()) continue;
                if (sb.Length != 0) sb.Append("\n\n");
                sb.Append(text);
            }
            return PlainText(sb.ToString());
        }

        private static List<string> UnmetQuestRequirements(Quest quest, AcceptanceReport overall)
        {
            var reasons = new List<string>();
            foreach (QuestPart_RequirementsToAccept part in quest.PartsListForReading.OfType<QuestPart_RequirementsToAccept>())
            {
                AcceptanceReport report = part.CanAccept();
                string reason = PlainText(report.Reason);
                if (!report.Accepted && reason.Length > 0 && !reasons.Contains(reason)) reasons.Add(reason);
            }
            string fallback = PlainText(overall.Reason);
            if (reasons.Count == 0 && fallback.Length > 0) reasons.Add(fallback);
            return reasons;
        }

        /// <summary>獎勵選項：照任務分頁「以此獎勵接受」的每一組；描述用遊戲自己的獎勵說明文字。</summary>
        private static List<QuestRewardInfo> QuestRewardOptions(Quest quest)
        {
            var result = new List<QuestRewardInfo>();
            QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
            if (choice == null) return result;
            for (int i = 0; i < choice.choices.Count; i++)
            {
                result.Add(new QuestRewardInfo
                {
                    Number = i + 1,
                    Rewards = choice.choices[i].rewards.Select(RewardText).Where(t => t.Length > 0).ToList(),
                    RequiresAccepter = ChoiceRequiresAccepter(quest, choice, choice.choices[i])
                });
            }
            return result;
        }

        private static string RewardText(Reward reward)
        {
            try
            {
                return PlainText(reward.GetDescription(default(RewardsGeneratorParams)));
            }
            catch (Exception)
            {
                return reward.GetType().Name;
            }
        }

        /// <summary>選了這一組獎勵之後，留下的任務段落有沒有要接受者（與任務分頁相同的算法：扣掉只屬於其他選項的段落）。</summary>
        private static bool ChoiceRequiresAccepter(Quest quest, QuestPart_Choice choice, QuestPart_Choice.Choice picked)
        {
            var remaining = new List<QuestPart>(quest.PartsListForReading);
            foreach (QuestPart_Choice.Choice other in choice.choices)
            {
                if (other == picked) continue;
                foreach (QuestPart part in other.questParts)
                {
                    if (!picked.questParts.Contains(part)) remaining.Remove(part);
                }
            }
            return remaining.Any(p => p.RequiresAccepter);
        }

        private static IEnumerable<Pawn> QuestAccepterCandidates(Quest quest)
        {
            return PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended
                .Where(p => QuestUtility.CanPawnAcceptQuest(p, quest));
        }

        /// <summary>任務分頁在給接受者皇家恩惠前會跳的確認框內容（社交能力、自負個性、影響通靈連結的個性）；沒有則回傳空字串。</summary>
        private static string AccepterWarning(Quest quest, Pawn pawn)
        {
            QuestPart_GiveRoyalFavor favor = quest.PartsListForReading.OfType<QuestPart_GiveRoyalFavor>().FirstOrDefault();
            if (favor == null || !favor.giveToAccepter) return string.Empty;
            NamedArgument pawnArg = pawn.Named("PAWN");
            NamedArgument factionArg = favor.faction.Named("FACTION");
            var warnings = new List<string>();
            if (pawn.skills?.GetSkill(SkillDefOf.Social)?.TotallyDisabled == true)
                warnings.Add("RoyalIncapableOfSocial".Translate(pawnArg, factionArg).Resolve());
            IEnumerable<Trait> conceited = RoyalTitleUtility.GetConceitedTraits(pawn);
            if (conceited.Any())
                warnings.Add("RoyalWithConceitedTrait".Translate(pawnArg, factionArg, conceited.Select(t => t.Label).ToCommaList(useAnd: true)).Resolve());
            IEnumerable<Trait> psylinkTraits = RoyalTitleUtility.GetTraitsAffectingPsylinkNegatively(pawn);
            if (!pawn.HasPsylink && psylinkTraits.Any())
                warnings.Add("RoyalWithTraitAffectingPsylinkNegatively".Translate(pawnArg, factionArg, psylinkTraits.Select(t => t.Label).ToCommaList(useAnd: true)).Resolve());
            return PlainText(string.Join(" ", warnings));
        }

        private static string PlainText(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : MarkupTags.Replace(text, string.Empty).Trim();
        }

        private static Quest FindQuest(int questId)
        {
            return Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == questId && !q.hidden && !q.hiddenInUI);
        }

        /// <summary>照任務分頁接受鈕：先過接受條件，有獎勵選項的先選定，需要接受者的再核對人選，最後 Quest.Accept。</summary>
        public CommandResult AcceptQuest(int questId, int rewardNumber, int accepterPawnId)
        {
            Quest quest = FindQuest(questId);
            if (quest == null) return CommandResult.Fail(QuestNotFound);
            if (quest.State == QuestState.Ongoing) return CommandResult.Fail("the quest is already accepted");
            if (quest.State != QuestState.NotYetAccepted) return CommandResult.Fail("the quest is no longer available: it expired or ended");
            AcceptanceReport report = QuestUtility.CanAcceptQuest(quest);
            if (!report.Accepted)
            {
                List<string> reasons = UnmetQuestRequirements(quest, report);
                return CommandResult.Fail("the game does not allow accepting this quest: "
                    + (reasons.Count > 0 ? string.Join("; ", reasons) : "MessageCannotAcceptQuest".Translate().Resolve()));
            }

            // 有獎勵選項的任務沒有一般的接受鈕，只能用「以此獎勵接受」；只有一組時不必指定。
            QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
            QuestPart_Choice.Choice picked = null;
            bool requiresAccepter = quest.RequiresAccepter;
            if (choice != null)
            {
                if (rewardNumber >= 1 && rewardNumber <= choice.choices.Count) picked = choice.choices[rewardNumber - 1];
                else if (rewardNumber == 0 && choice.choices.Count == 1) picked = choice.choices[0];
                if (picked == null) return CommandResult.Fail($"pick a reward choice from 1 to {choice.choices.Count}");
                requiresAccepter = ChoiceRequiresAccepter(quest, choice, picked);
            }

            Pawn accepter = null;
            if (requiresAccepter)
            {
                accepter = QuestAccepterCandidates(quest).FirstOrDefault(p => p.thingIDNumber == accepterPawnId);
                if (accepter == null) return CommandResult.Fail("the accepter is not a colonist who can accept this quest");
            }

            if (picked != null) choice.Choose(picked);
            quest.Accept(accepter);
            if (accepter != null)
            {
                Messages.Message("MessageQuestAccepted".Translate(accepter, quest.name), accepter, MessageTypeDefOf.TaskCompletion, historical: false);
            }
            return CommandResult.Ok(quest.name);
        }

        /// <summary>照任務分頁的拒絕鈕：切換 dismissed，子任務一起跟著。已結束的任務該鈕是刪除，這裡不做。</summary>
        public CommandResult SetQuestDismissed(int questId, bool dismissed)
        {
            Quest quest = FindQuest(questId);
            if (quest == null) return CommandResult.Fail(QuestNotFound);
            if (quest.Historical) return CommandResult.Fail("the quest has already expired or ended");
            quest.dismissed = dismissed;
            foreach (Quest subquest in quest.GetSubquests())
            {
                subquest.dismissed = dismissed;
            }
            return CommandResult.Ok();
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
                string kind = DetermineGizmoKind(command, out string targetHint);
                result.Add(new GizmoInfo
                {
                    Number = i + 1,
                    Label = CommandLabel(command),
                    Description = LimitChoiceText(command.Desc),
                    Kind = kind,
                    TargetHint = targetHint,
                    On = (command as Command_Toggle)?.isActive?.Invoke() ?? false,
                    Disabled = command.Disabled,
                    DisabledReason = command.disabledReason ?? string.Empty
                });
            }
            return result;
        }
        #pragma warning restore S1168

        /// <summary>
        /// 按鈕種類。Command_Action 可能在按下後才開始選目標（互動物件、虛空巨石柱、使用物品都是），
        /// 光看型別分不出來，一律算 action，按下去才知道（見 UseGizmo）。
        /// </summary>
        private static string DetermineGizmoKind(Command command, out string targetHint)
        {
            targetHint = string.Empty;
            if (command is Command_Toggle) return GizmoInfo.ToggleKind;
            if (command is Command_Action) return GizmoInfo.ActionKind;
            if (command is Command_Ability abilityCommand)
            {
                AbilityDef def = abilityCommand.Ability?.def;
                // 世界地圖選目標（傳送到別處）沒有地圖格子可指，不支援。
                if (def == null || def.targetWorldCell) return GizmoInfo.UnsupportedKind;
                // 不需要目標的能力按下去就生效，和一般 action 一樣。
                if (!def.targetRequired) return GizmoInfo.ActionKind;
                return TargetKindOf(abilityCommand.Ability.verb as ITargetingSource, out targetHint);
            }
            if (command is Command_VerbTarget verbCommand) return TargetKindOf(verbCommand.verb, out targetHint);
            if (command is Command_Target targetCommand) return TargetKindOf(targetCommand.targetingParams, out targetHint);
            return GizmoInfo.UnsupportedKind;
        }

        private static string TargetKindOf(ITargetingSource source, out string targetHint)
        {
            targetHint = string.Empty;
            // 不可選目標的 verb（對自己施放）按下去就直接執行。
            if (source == null) return GizmoInfo.UnsupportedKind;
            if (!source.Targetable) return GizmoInfo.ActionKind;
            return TargetKindOf(source.targetParams, out targetHint);
        }

        private static string TargetKindOf(TargetingParameters parameters, out string targetHint)
        {
            targetHint = parameters == null ? string.Empty : DescribeTargets(parameters);
            return parameters == null ? GizmoInfo.UnsupportedKind : GizmoInfo.TargetKind;
        }

        /// <summary>把 TargetingParameters 轉成白話的「可以指什麼」；細項（距離、視線、可否到達）由遊戲在指定時驗證。</summary>
        private static string DescribeTargets(TargetingParameters p)
        {
            var kinds = new List<string>();
            if (p.canTargetLocations) kinds.Add("a cell");
            if (p.canTargetPawns) kinds.Add(DescribePawnTargets(p));
            if (p.canTargetBuildings) kinds.Add("a building");
            if (p.canTargetItems) kinds.Add("an item");
            if (p.canTargetPlants) kinds.Add("a plant");
            if (p.canTargetFires) kinds.Add("a fire");
            if (p.canTargetCorpses || p.onlyTargetCorpses) kinds.Add("a corpse");
            return kinds.Count == 0 ? "see the game's targeting rules" : string.Join(", ", kinds);
        }

        private static string DescribePawnTargets(TargetingParameters p)
        {
            if (p.onlyTargetColonists) return "a colonist";
            if (p.onlyTargetPrisonersOfColony) return "a prisoner of the colony";
            if (p.onlyTargetColonistsOrPrisoners) return "a colonist or prisoner";
            if (p.onlyTargetColonistsOrPrisonersOrSlaves || p.onlyTargetColonistsOrPrisonersOrSlavesAllowMinorMentalBreaks) return "a colonist, prisoner or slave";
            if (p.onlyTargetControlledPawns) return "a controlled pawn";
            return "a pawn";
        }

        // 選目標器（Targeter）的目標回呼與目標規則都是私有欄位：玩家按下按鈕後它保存「點到目標時做什麼」。
        // 沒有滑鼠可點，就讀出這些欄位照同一套規則自己「點」；找不到欄位時回報不支援，避免版本差異中斷 agent。
        #pragma warning disable S3011 // RimWorld 私有欄位存取為 Mod 運作必需
        private static readonly FieldInfo TargeterActionField =
            typeof(Targeter).GetField("action", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TargeterParamsField =
            typeof(Targeter).GetField("targetParams", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TargeterValidatorField =
            typeof(Targeter).GetField("targetValidator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo LiveMessagesField =
            typeof(Messages).GetField("liveMessages", BindingFlags.Static | BindingFlags.NonPublic);
        #pragma warning restore S3011
        private const int MaxGameMessages = 3;
        private const int MaxGameMessageChars = 200;

        /// <summary>
        /// 記下開始時螢幕上的遊戲訊息，之後取出新增的：原版把「不能這樣做的原因」都以訊息（Messages.Message）顯示，
        /// 沒有別的管道，所以拿它當原因回報給模型。同樣文字的訊息還在畫面上時遊戲不新增，只重置計時
        /// （startingFrame 會變），所以新增或被重置的都算。
        /// </summary>
        private sealed class MessageWatch
        {
            private readonly Dictionary<Message, int> _before = Live().ToDictionary(m => m, m => m.startingFrame);

            private static List<Message> Live()
            {
                return (LiveMessagesField?.GetValue(null) as List<Message>)?.ToList() ?? new List<Message>();
            }

            public List<string> NewTexts()
            {
                return Live().Where(m => !string.IsNullOrEmpty(m.text) && (!_before.TryGetValue(m, out int frame) || frame != m.startingFrame))
                    .Select(m => m.text.StripTags().Flatten().Trim())
                    .Where(t => t.Length > 0)
                    .Select(t => t.Length <= MaxGameMessageChars ? t : t.Substring(0, MaxGameMessageChars) + "…")
                    .Distinct().Take(MaxGameMessages).ToList();
            }
        }

        private static string WithGameMessages(string text, MessageWatch watch)
        {
            List<string> messages = watch.NewTexts();
            if (messages.Count == 0) return text;
            string clause = "game message: " + string.Join(" / ", messages);
            return string.IsNullOrEmpty(text) ? clause : text + "; " + clause;
        }

        private static string DescribeTarget(LocalTargetInfo target)
        {
            return target.HasThing ? $"{target.Thing.LabelShort} [{target.Thing.thingIDNumber}]" : $"cell ({target.Cell.x},{target.Cell.z})";
        }

        /// <summary>把工具給的目標對到遊戲裡的物件或格子；回傳 false 時 error 為原因。</summary>
        private static bool TryResolveTarget(MapTarget target, out LocalTargetInfo resolved, out string error)
        {
            resolved = LocalTargetInfo.Invalid;
            error = null;
            if (target.IsCell)
            {
                var cell = new IntVec3(target.X, 0, target.Z);
                if (!cell.InBounds(Map))
                {
                    error = $"cell ({target.X},{target.Z}) is outside the map";
                    return false;
                }
                resolved = new LocalTargetInfo(cell);
                return true;
            }
            Thing thing = FindThing(target.ThingId);
            if (thing == null || thing.Fogged())
            {
                error = $"target [{target.ThingId}] not found on the map";
                return false;
            }
            resolved = new LocalTargetInfo(thing);
            return true;
        }

        public CommandResult UseGizmo(int thingId, int gizmoNumber, string label, MapTarget target)
        {
            Thing thing = FindThing(thingId);
            if (thing == null || thing.Fogged()) return CommandResult.Fail("thing not found on the map");
            List<Command> commands = VisibleCommands(thing);
            CommandResult validation = ValidateGizmoCommand(thing, gizmoNumber, label, commands, out Command command);
            if (validation != null) return validation;

            var localTarget = LocalTargetInfo.Invalid;
            if (target != null && !TryResolveTarget(target, out localTarget, out string targetError)) return CommandResult.Fail(targetError);

            // 先收掉殘留的選目標狀態，之後 IsTargeting 才代表「這顆按鈕開始選目標」。
            StopAllTargeting();
            var before = new HashSet<Window>(Find.WindowStack.Windows);
            var watch = new MessageWatch();
            command.ProcessInput(null);
            CommandResult targeting = FinishTargeting(label, target == null ? (LocalTargetInfo?)null : localTarget, watch, out bool askedForTarget);
            CommandResult windowFailure = CloseUnsupportedWindows(before, label);
            if (targeting != null && !targeting.Success) return targeting;
            if (windowFailure != null) return windowFailure;

            if (askedForTarget) return targeting;
            string suffix = GetToggleCommandResultSuffix(command);
            if (target != null) suffix = "the button did not ask for a target, so " + DescribeTarget(localTarget) + " was not used";
            return CommandResult.Ok(WithGameMessages(suffix, watch));
        }

        /// <summary>
        /// 按鈕按下後若開始選目標，就把給的目標「點」下去（照原版 Targeter.ProcessInputEvents 的順序驗證），
        /// 沒給目標就取消並回報要什麼；無論如何都收掉選目標狀態。按鈕沒有開始選目標時回傳 null。
        /// </summary>
        private static CommandResult FinishTargeting(string label, LocalTargetInfo? target, MessageWatch watch, out bool askedForTarget)
        {
            askedForTarget = false;
            if (Find.WorldTargeter.IsTargeting)
            {
                askedForTarget = true;
                Find.WorldTargeter.StopTargeting();
                return CommandResult.Fail($"\"{label}\" asks for a target on the world map, which is not supported yet; it was cancelled");
            }
            Targeter targeter = Find.Targeter;
            if (!targeter.IsTargeting) return null;
            askedForTarget = true;
            try
            {
                if (!target.HasValue)
                {
                    TargetingParameters parameters = ActiveTargetParams(targeter);
                    string hint = parameters == null ? "a thing or a cell" : DescribeTargets(parameters);
                    return CommandResult.Fail($"\"{label}\" asks for a target ({hint}); pass targetId (a thing id) or targetX and targetZ (a cell). Nothing was done");
                }
                return ApplyTarget(targeter, label, target.Value, watch);
            }
            finally
            {
                // 失敗或成功都要收掉，否則玩家的畫面會一直停在選目標狀態。
                targeter.StopTargeting();
            }
        }

        private static TargetingParameters ActiveTargetParams(Targeter targeter)
        {
            return targeter.targetingSource?.targetParams ?? TargeterParamsField?.GetValue(targeter) as TargetingParameters;
        }

        private static CommandResult ApplyTarget(Targeter targeter, string label, LocalTargetInfo target, MessageWatch watch)
        {
            ITargetingSource source = targeter.targetingSource;
            TargetingParameters parameters = ActiveTargetParams(targeter);
            if (parameters == null) return CommandResult.Fail($"\"{label}\" started a kind of targeting that cannot be driven without the mouse");
            string described = DescribeTarget(target);

            // 1) 能不能指這種東西：原版以 TargetingParameters.CanTarget 篩（GenUI.TargetsAt）。
            string untargetable = UntargetableReason(parameters, source, target);
            if (untargetable != null) return CommandResult.Fail($"{described} cannot be targeted by \"{label}\": {untargetable}");

            if (source == null)
            {
                // Command_Target 這類：回呼加選用的額外驗證器，點到合法目標才執行。
                var action = TargeterActionField?.GetValue(targeter) as Action<LocalTargetInfo>;
                var validator = TargeterValidatorField?.GetValue(targeter) as Func<LocalTargetInfo, bool>;
                if (action == null) return CommandResult.Fail($"\"{label}\" started a kind of targeting that cannot be driven without the mouse");
                if (validator != null && !validator(target)) return CommandResult.Fail($"{described} was rejected by the game's target check for \"{label}\"");
                action(target);
                return CommandResult.Ok(WithGameMessages("targeted " + described, watch));
            }

            // 兩段式選目標（先選目標再選落點）要第二個目標，這裡只給一個。
            if (source.DestinationSelector != null)
                return CommandResult.Fail($"\"{label}\" needs a second target (a destination) after {described}, which is not supported yet");
            if (target.HasThing && target.Thing == source.Caster && !parameters.canTargetSelf)
                return CommandResult.Fail($"\"{label}\" cannot target its own source");

            // 2) 目標來源自己的驗證（例如殖民者忙碌、倒地、走不到）；原因只存在於它顯示的訊息裡。
            if (!source.ValidateTarget(target, showMessages: false))
            {
                var reasonWatch = new MessageWatch();
                source.ValidateTarget(target, showMessages: true);
                string reason = reasonWatch.NewTexts().FirstOrDefault();
                return CommandResult.Fail($"the game rejected {described} for \"{label}\"" + (reason == null ? " (it gave no reason)" : ": " + reason));
            }

            // 3) 打得到嗎：原版 CurrentTargetUnderMouse(mustBeHittableNowIfNotMelee: true) 對非 pawn 目標要求可命中，否則視為無效。
            if (!(target.Thing is Pawn) && !source.IsMeleeAttack && !source.CanHitTarget(target))
                return CommandResult.Fail($"{described} cannot be hit by \"{label}\"" + RangeNote(source, target));

            // 4) 下令：照原版 Targeter.OrderVerbForceTarget。
            if (source.CasterIsPawn)
            {
                source.OrderForceTarget(target);
            }
            else if (source.Caster is Building_Turret turret && turret.Map == Find.CurrentMap)
            {
                turret.OrderAttack(target);
                if (turret.ForcedTarget != target)
                    return CommandResult.Fail(WithGameMessages($"{turret.LabelShort} did not take {described} as its target", watch));
            }
            else
            {
                return CommandResult.Fail($"the game's targeter would not issue an order for \"{label}\"");
            }
            return CommandResult.Ok(WithGameMessages("targeted " + described, watch));
        }

        private static string UntargetableReason(TargetingParameters parameters, ITargetingSource source, LocalTargetInfo target)
        {
            string accepts = $"it accepts {DescribeTargets(parameters)}";
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                if (!parameters.CanTarget(thing, source)) return accepts;
                // GenUI.ThingsUnderMouse / TargetsAt 對隱形或躲起來的 pawn 的過濾。
                if (thing is Pawn pawn)
                {
                    if (pawn.IsHiddenFromPlayer()) return "that pawn is hidden";
                    Thing caster = source?.Caster;
                    if (pawn.IsPsychologicallyInvisible() && caster != null && caster.Faction != pawn.Faction) return "that pawn is not visible";
                }
                return null;
            }
            IntVec3 cell = target.Cell;
            if (!cell.InBounds(Map, parameters.mapBoundsContractedBy)) return "that cell is too close to the map edge";
            return parameters.CanTarget(new TargetInfo(cell, Map), source) ? null : accepts;
        }

        private static string RangeNote(ITargetingSource source, LocalTargetInfo target)
        {
            Verb verb = source.GetVerb;
            if (verb == null) return string.Empty;
            float distance = (target.Cell - source.Caster.Position).LengthHorizontal;
            float min = verb.verbProps.EffectiveMinRange(target, source.Caster);
            return $" (distance {distance:0.#}, range {min:0.#} to {verb.EffectiveRange:0.#}, line of sight may also be blocked)";
        }

        private static void StopAllTargeting()
        {
            if (Find.Targeter.IsTargeting) Find.Targeter.StopTargeting();
            if (Find.WorldTargeter.IsTargeting) Find.WorldTargeter.StopTargeting();
        }

        private static CommandResult ValidateGizmoCommand(Thing thing, int gizmoNumber, string label, List<Command> commands, out Command command)
        {
            command = gizmoNumber >= 1 && gizmoNumber <= commands.Count ? commands[gizmoNumber - 1] : null;
            if (command == null || !string.Equals(CommandLabel(command), label, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"{thing.LabelShort}'s button list changed; call list_gizmos again");
            if (command.Disabled)
                return CommandResult.Fail($"\"{label}\" is disabled" + (command.disabledReason.NullOrEmpty() ? string.Empty : ": " + command.disabledReason));
            if (DetermineGizmoKind(command, out _) == GizmoInfo.UnsupportedKind)
                return CommandResult.Fail($"\"{label}\" needs a menu choice or another kind of input that use_gizmo cannot give");
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

        public FloatMenuInfo GetFloatMenuOptions(int colonistId, MapTarget target)
        {
            Pawn pawn = FindColonist(colonistId);
            if (pawn == null) return new FloatMenuInfo { Error = ColonistNotFound };
            var info = new FloatMenuInfo { PawnName = pawn.LabelShort, Drafted = pawn.Drafted };
            if (!TryResolveTarget(target, out LocalTargetInfo resolved, out string error))
            {
                info.Error = error;
                return info;
            }
            info.TargetLabel = DescribeTarget(resolved);
            List<FloatMenuOption> options = BuildFloatMenu(pawn, resolved, out error);
            if (options == null)
            {
                info.Error = error;
                return info;
            }
            info.Options = options.Select((o, i) => new FloatMenuOptionInfo { Number = i + 1, Label = o.Label.StripTags(), Disabled = o.Disabled }).ToList();
            return info;
        }

        public CommandResult UseFloatMenuOption(int colonistId, MapTarget target, int optionNumber, string label)
        {
            Pawn pawn = FindColonist(colonistId);
            if (pawn == null) return CommandResult.Fail(ColonistNotFound);
            if (!TryResolveTarget(target, out LocalTargetInfo resolved, out string error)) return CommandResult.Fail(error);
            List<FloatMenuOption> options = BuildFloatMenu(pawn, resolved, out error);
            if (options == null) return CommandResult.Fail(error);

            FloatMenuOption option = optionNumber >= 1 && optionNumber <= options.Count ? options[optionNumber - 1] : null;
            if (option == null || !string.Equals(option.Label.StripTags(), label, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"{pawn.LabelShort}'s right-click menu changed; call list_right_click_menu again");
            if (option.Disabled) return CommandResult.Fail($"\"{label}\" is disabled");

            StopAllTargeting();
            var before = new HashSet<Window>(Find.WindowStack.Windows);
            var watch = new MessageWatch();
            try
            {
                option.Chosen(colonistOrdering: true, null);
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: right-click option \"" + label + "\" threw: " + ex);
                return CommandResult.Fail($"\"{label}\" failed in the game: {ex.Message}");
            }

            // 選項若接著要再選一個目標或開別的視窗，這裡沒辦法回應，收掉並回報。
            bool targeting = Find.Targeter.IsTargeting || Find.WorldTargeter.IsTargeting;
            StopAllTargeting();
            CommandResult windowFailure = CloseUnsupportedWindows(before, label);
            if (targeting) return CommandResult.Fail($"\"{label}\" asks for a further target, which is not supported yet; it was cancelled");
            if (windowFailure != null) return windowFailure;
            return CommandResult.Ok(WithGameMessages(string.Empty, watch));
        }

        /// <summary>
        /// 照原版右鍵（Selector.HandleMapClicks）的路徑：FloatMenuMakerMap.GetOptions 以點擊位置產生選項，
        /// 不需要滑鼠或框選，只要給殖民者與世界座標；排序同 FloatMenu 建構子（優先度高者在前）。
        /// 殖民者不能下令時原版只丟一則訊息，這裡先問 ShouldGenerateFloatMenuForPawn 取得原因。
        /// </summary>
        private static List<FloatMenuOption> BuildFloatMenu(Pawn pawn, LocalTargetInfo target, out string error)
        {
            error = null;
            AcceptanceReport allowed = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn);
            if (!allowed.Accepted)
            {
                error = allowed.Reason.NullOrEmpty() ? $"{pawn.LabelShort} cannot be given orders right now" : allowed.Reason.StripTags();
                return null;
            }
            if (!pawn.CanTakeOrder)
            {
                error = $"{pawn.LabelShort} cannot take orders";
                return null;
            }
            // 點擊位置：pawn 用繪製位置（原版先以 DrawPos 距離找 pawn），其他物件用所在格的中心。
            UnityEngine.Vector3 clickPosition = target.HasThing
                ? (target.Thing is Pawn targetPawn ? targetPawn.DrawPos : target.Thing.Position.ToVector3Shifted())
                : target.Cell.ToVector3Shifted();
            try
            {
                // GetOptions 會改動傳入的清單，所以給一份新的。
                List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, clickPosition, out FloatMenuContext _);
                return (options ?? new List<FloatMenuOption>())
                    .OrderByDescending(o => o.Priority).ThenByDescending(o => o.orderInPriority).ToList();
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: building the right-click menu failed: " + ex);
                error = "the game could not build the right-click menu: " + ex.Message;
                return null;
            }
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

        /// <summary>沙包與路障是 0.55；家具多在 0.4 上下，不算進網格的掩體。</summary>
        private const float CoverFillPercent = 0.5f;

        public IReadOnlyList<MapGridCell> GetMapGrid(CellRectSpec rect)
        {
            Map map = Map;
            var result = new List<MapGridCell>(rect.CellCount);
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            {
                for (int x = rect.MinX; x <= rect.MaxX; x++)
                {
                    result.Add(ToGridCell(new IntVec3(x, 0, z), map));
                }
            }
            return result;
        }

        private static MapGridCell ToGridCell(IntVec3 c, Map map)
        {
            // 迷霧裡的東西玩家看不到，其他圖層一律不填（與 ToCell 同一原則）。
            if (c.Fogged(map)) return new MapGridCell { Fogged = true };

            TerrainDef terrain = c.GetTerrain(map);
            Zone zone = c.GetZone(map);
            RoofDef roof = c.GetRoof(map);
            var cell = new MapGridCell
            {
                Terrain = terrain.IsWater
                    ? (terrain.passability == Traversability.Impassable ? GridTerrain.DeepWater : GridTerrain.ShallowWater)
                    : (terrain.IsFloor || terrain.bridge || terrain.IsSubstructure ? GridTerrain.Floor : GridTerrain.Ground),
                Zone = zone is Zone_Stockpile ? GridZone.Stockpile : zone is Zone_Growing ? GridZone.Growing : GridZone.None,
                Roof = roof == null ? GridRoof.None : !roof.isNatural ? GridRoof.Constructed : roof.isThickRoof ? GridRoof.Mountain : GridRoof.Natural
            };

            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t is Pawn pawn)
                {
                    GridPawn kind = pawn.HostileTo(Faction.OfPlayer) ? GridPawn.Hostile : pawn.IsColonist ? GridPawn.Colonist : GridPawn.Other;
                    if (kind > cell.Pawn) cell.Pawn = kind;
                }
                else if (t is Plant plant)
                {
                    // 玩家點不到的植物（草、苔、蒲公英）是地表覆蓋，畫出來只會淹掉真正要看的東西。
                    if (!plant.def.selectable) continue;
                    GridPlant kind = plant.def.plant != null && plant.def.plant.IsTree ? GridPlant.Tree : GridPlant.Other;
                    if (kind > cell.Plant) cell.Plant = kind;
                }
                else if (t is Blueprint || t is Frame)
                {
                    // 地板的藍圖與框架蓋滿整間房，只會遮住家具與物品；地板在地形圖層看得到成品，這裡不畫。
                    if (t.def.entityDefToBuild is TerrainDef) continue;
                    GridBuilding kind = t is Frame ? GridBuilding.Frame : GridBuilding.Blueprint;
                    if (kind > cell.Building) cell.Building = kind;
                }
                else if (t.def.mineable)
                {
                    cell.Rock = true;
                }
                else if (t is Building)
                {
                    // 電線與管線貼著整片地面，畫出來沒有佈局資訊。
                    if (t.def.altitudeLayer == AltitudeLayer.Conduits) continue;
                    GridBuilding kind = t.def.IsWall ? GridBuilding.Wall
                        : t.def.IsDoor ? GridBuilding.Door
                        : t is Building_Turret ? GridBuilding.Turret
                        : t.def.passability == Traversability.PassThroughOnly && t.def.fillPercent >= CoverFillPercent ? GridBuilding.Cover
                        : GridBuilding.Other;
                    if (kind > cell.Building) cell.Building = kind;
                }
                else if (t.def.category == ThingCategory.Item)
                {
                    cell.HasItem = true;
                }
            }
            return cell;
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
            Map map = Find.CurrentMap;
            int target = Find.TickManager.TicksGame + Math.Max(0, ticks);
            while (true)
            {
                string notReady = GetNotReadyReason();
                // 等待期間讀了別的檔：tick 已換一條時間軸，目標不再有意義。
                if (notReady != null || Current.Game != game) return CommandResult.Fail(notReady ?? "a different game was loaded");
                // 工具都作用在當前地圖：玩家切到別張地圖後，等待中的檢查與之後的指令都會換成那張圖，先回來讓模型知道。
                if (Find.CurrentMap != map) return CommandResult.Fail("the current map changed");
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
                NotifyUseWorkPrioritiesChanged();
                note = "Manual work priorities were turned on for the colony.";
            }
            pawn.workSettings.SetPriority(workType, priority);
            return CommandResult.Ok(note);
        }

        /// <summary>照原版工作分頁的勾選框：切換手動優先級後，每個玩家 pawn 的工作順序快取都要重建。</summary>
        internal static void NotifyUseWorkPrioritiesChanged()
        {
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
            {
                if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null) pawn.workSettings.Notify_UseWorkPrioritiesChanged();
            }
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

        // ---- 組商隊：照 Dialog_FormCaravan 的「送出」 ----

        /// <summary>原版組隊視窗對背包的算法（非重組模式）。</summary>
        private const IgnorePawnsInventoryMode CaravanInventoryMode = IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload;

        /// <summary>
        /// 組隊視窗的草稿：選好的成員與貨物（原版計算器吃 <see cref="TransferableOneWay"/>）、算好的規劃，
        /// 以及送出所需的起始格、出口與集合點。成員一人一個 transferable，避免原版把同種動物疊在一起後只取前 N 隻。
        /// </summary>
        private sealed class CaravanDraft
        {
            public readonly List<Pawn> Pawns = new List<Pawn>();
            public readonly List<TransferableOneWay> Transferables = new List<TransferableOneWay>();
            public PlanetTile Destination = PlanetTile.Invalid;
            public PlanetTile StartingTile = PlanetTile.Invalid;
            public IntVec3 ExitSpot = IntVec3.Invalid;
            public IntVec3 MeetingPoint = IntVec3.Invalid;
            public CaravanPlanInfo Plan = new CaravanPlanInfo();
        }

        public IReadOnlyList<CaravanCargoInfo> GetCaravanCargo()
        {
            // AllReachableColonyItems 回傳共用的暫存清單，先複製再分組。
            return CaravanFormingUtility.AllReachableColonyItems(Map, false, false, false).ToList()
                .GroupBy(t => t.GetInnerIfMinified().def)
                .Select(g => new CaravanCargoInfo
                {
                    DefName = g.Key.defName,
                    Label = string.IsNullOrEmpty(g.Key.label) ? g.Key.defName : g.Key.label,
                    Count = g.Sum(t => t.stackCount),
                    MassEach = g.Key.BaseMass
                })
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public CaravanPlanInfo PlanCaravan(CaravanRequest request)
        {
            return BuildCaravanDraft(request).Plan;
        }

        public CommandResult FormCaravan(CaravanRequest request)
        {
            CaravanDraft draft = BuildCaravanDraft(request);
            CaravanPlanInfo plan = draft.Plan;
            if (!string.IsNullOrEmpty(plan.Error)) return CommandResult.Fail(plan.Error);
            if (plan.Blockers.Count > 0) return CommandResult.Fail(string.Join("; ", plan.Blockers.ToArray()));

            // 與視窗 TryFormAndSendCaravan 最後一步相同：倒地的另外列，由同伴扛到出口。
            CaravanFormingUtility.StartFormingCaravan(
                draft.Pawns.Where(p => !p.Downed).ToList(), draft.Pawns.Where(p => p.Downed).ToList(), Faction.OfPlayer,
                draft.Transferables, draft.MeetingPoint, draft.ExitSpot, draft.StartingTile, draft.Destination);
            Messages.Message("CaravanFormationProcessStarted".Translate(), draft.Pawns[0], MessageTypeDefOf.PositiveEvent, historical: false);

            string members = string.Join(", ", draft.Pawns.Select(p => p.LabelShort).ToArray());
            string cargo = plan.Cargo.Count == 0 ? "no cargo" : string.Join(", ", plan.Cargo.Select(c => c.Label + " x" + c.Count).ToArray());
            return CommandResult.Ok($"Started forming a caravan with {members}; they gather {cargo} at ({draft.MeetingPoint.x},{draft.MeetingPoint.z}) " +
                                    $"and then leave the map at ({draft.ExitSpot.x},{draft.ExitSpot.z}) for tile {draft.Destination.tileId}");
        }

        private static CaravanDraft RejectCaravan(CaravanDraft draft, string error)
        {
            draft.Plan = new CaravanPlanInfo { Error = error };
            return draft;
        }

        private static CaravanDraft BuildCaravanDraft(CaravanRequest request)
        {
            var draft = new CaravanDraft();
            if (request == null) return RejectCaravan(draft, "no caravan requested");
            Map map = Map;
            if (map == null) return RejectCaravan(draft, "there is no current map");

            // 成員：只收組隊視窗會列出的人（AllSendablePawns），其餘說明為什麼不行。
            List<Pawn> sendable = Dialog_FormCaravan.AllSendablePawns(map, false);
            foreach (int id in (request.PawnIds ?? Array.Empty<int>()).Distinct())
            {
                Pawn pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
                if (pawn == null) return RejectCaravan(draft, $"no pawn with id {id} is on this map");
                if (!sendable.Contains(pawn)) return RejectCaravan(draft, $"{pawn.LabelShort} cannot join a caravan: {WhyNotSendable(pawn)}");
                // 被徵召的人不吃集結的職責（原版「強制出發」也會先解除徵召）。
                if (pawn.Drafted) return RejectCaravan(draft, $"{pawn.LabelShort} is drafted; undraft them first");
                draft.Pawns.Add(pawn);
                var transferable = new TransferableOneWay();
                transferable.things.Add(pawn);
                transferable.AdjustTo(1);
                draft.Transferables.Add(transferable);
            }

            // 貨物：與視窗物品頁同一份清單（AddItemsToTransferables），同種物品依原版規則併成一行。
            var items = new List<TransferableOneWay>();
            foreach (Thing thing in CaravanFormingUtility.AllReachableColonyItems(map, false, false, false).ToList())
            {
                TransferableOneWay transferable = TransferableUtility.TransferableMatching(thing, items, TransferAsOneMode.PodsOrCaravanPacking);
                if (transferable == null)
                {
                    transferable = new TransferableOneWay();
                    items.Add(transferable);
                }
                if (!transferable.things.Contains(thing)) transferable.things.Add(thing);
            }
            var cargoLines = new List<CaravanPlanCargo>();
            foreach (TradeLine line in request.Cargo ?? Array.Empty<TradeLine>())
            {
                int remaining = line.Count;
                ThingDef def = null;
                foreach (TransferableOneWay transferable in items)
                {
                    ThingDef inner = transferable.AnyThing.GetInnerIfMinified().def;
                    if (remaining <= 0 || !string.Equals(inner.defName, line.DefName, StringComparison.OrdinalIgnoreCase)) continue;
                    def = inner;
                    int take = Math.Min(remaining, transferable.GetMaximumToTransfer() - transferable.CountToTransfer);
                    if (take <= 0) continue;
                    transferable.AdjustTo(transferable.CountToTransfer + take);
                    remaining -= take;
                }
                if (def == null)
                    return RejectCaravan(draft, $"the colony has no {line.DefName} to carry; the game only lists items in storage or the home area that nobody has reserved");
                string label = string.IsNullOrEmpty(def.label) ? def.defName : def.label;
                if (remaining > 0)
                    return RejectCaravan(draft, $"the colony can only supply {line.Count - remaining} {label} for the caravan (asked for {line.Count}); the game only lists items in storage or the home area that nobody has reserved");
                cargoLines.Add(new CaravanPlanCargo { DefName = def.defName, Label = label, Count = line.Count, MassEach = def.BaseMass });
            }
            draft.Transferables.AddRange(items.Where(t => t.CountToTransfer > 0));

            FillCaravanPlan(draft, request, map, cargoLines);
            return draft;
        }

        /// <summary>為什麼 AllSendablePawns 不列這個 pawn（依那個方法的條件逐項對照）。</summary>
        private static string WhyNotSendable(Pawn pawn)
        {
            if (pawn.Dead) return "it is dead";
            if (pawn.Faction != Faction.OfPlayer && !pawn.IsPrisonerOfColony) return "it does not belong to the colony";
            if (!pawn.RaceProps.allowedOnCaravan) return "this kind of creature cannot travel with caravans";
            if (pawn.IsQuestHelper() || pawn.IsQuestLodger()) return "quest guests cannot join caravans";
            if (pawn.InMentalState) return "it is in a mental break";
            if (pawn.IsPrisoner && !pawn.guest.PrisonerIsSecure) return "the prisoner is not secured in a cell";
            if (pawn.GetLord() != null) return "it is busy with a group activity";
            return "the game does not offer it for caravans";
        }

        /// <summary>
        /// 算出視窗顯示的數字，並照 CheckForErrors／TryFormAndSendCaravan／TrySend 收集拒絕原因與警告。
        /// 出口與集合點的搜尋也在這裡做，讓規劃能事先說出「找不到出口」。
        /// </summary>
        private static void FillCaravanPlan(CaravanDraft draft, CaravanRequest request, Map map, List<CaravanPlanCargo> cargoLines)
        {
            var blockers = new List<string>();
            var warnings = new List<string>();
            PlanetTile origin = map.Tile;
            var plan = new CaravanPlanInfo { Members = draft.Pawns.Select(ToCaravanMember).ToList(), Cargo = cargoLines };

            if (!origin.LayerDef.canFormCaravans) blockers.Add("caravans cannot be formed on this world layer");

            // 目的地與起始格（視窗選好路線後：startingTile = BestExitTileToGoTo）。
            PlanetTile destination = PlanetTile.Invalid;
            if (request.DestinationTile < 0)
            {
                blockers.Add("MessageMustChooseRouteFirst".Translate().Resolve());
            }
            else
            {
                PlanetTile asked = ToPlanetTile(request.DestinationTile, origin);
                if (!TileInWorld(asked)) blockers.Add($"tile {request.DestinationTile} does not exist on this layer of the world");
                else if (!Find.WorldReachability.CanReach(origin, asked)) blockers.Add($"tile {asked.tileId} cannot be reached by land from here");
                else destination = asked;
                plan.DestinationTile = request.DestinationTile;
                plan.DestinationLabel = TileInWorld(asked) ? DescribeWorldTile(asked) : string.Empty;
            }
            plan.DestinationReachable = destination.Valid;
            if (destination.Valid)
            {
                draft.Destination = destination;
                draft.StartingTile = CaravanExitMapUtility.BestExitTileToGoTo(destination, map);
                plan.DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(origin, destination);
                if (!draft.StartingTile.Valid) blockers.Add("MessageNoValidExitTile".Translate().Resolve());
            }

            // 數字：與 Dialog_FormCaravan 的 MassUsage／MassCapacity／TilesPerDay／DaysWorthOfFood／ForagedFoodPerDay／Visibility／TicksToArrive 同一組呼叫。
            List<TransferableOneWay> transferables = draft.Transferables;
            plan.MassUsage = CollectionsMassCalculator.MassUsageTransferables(transferables, CaravanInventoryMode, false, false);
            plan.MassCapacity = CollectionsMassCalculator.CapacityTransferables(transferables, new StringBuilder());
            if (draft.Pawns.Count > 0)
            {
                plan.TilesPerDay = TilesPerDayCalculator.ApproxTilesPerDay(transferables, plan.MassUsage, plan.MassCapacity, origin, draft.StartingTile, false, null);
                plan.TicksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(draft.Pawns, plan.MassUsage, plan.MassCapacity, false, null);
            }
            if (destination.Valid)
            {
                using (WorldPath path = origin.Layer.Pather.FindPath(origin, destination, null))
                {
                    plan.FoodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, origin, CaravanInventoryMode, Faction.OfPlayer, path, 0f, plan.TicksPerMove);
                    plan.DaysUntilRot = DaysUntilRotCalculator.ApproxDaysUntilRot(transferables, origin, CaravanInventoryMode, path, 0f, plan.TicksPerMove);
                    plan.EtaTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin, destination, path, 0f, plan.TicksPerMove, Find.TickManager.TicksAbs);
                }
            }
            else
            {
                plan.FoodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, origin, CaravanInventoryMode, Faction.OfPlayer);
                plan.DaysUntilRot = DaysUntilRotCalculator.ApproxDaysUntilRot(transferables, origin, CaravanInventoryMode);
            }
            var foraged = ForagedFoodPerDayCalculator.ForagedFoodPerDay(transferables, map.Biome, Faction.OfPlayer, new StringBuilder());
            plan.ForagedFoodLabel = foraged.Item1 != null ? foraged.Item1.label : string.Empty;
            plan.ForagedPerDay = foraged.Item2;
            plan.Visibility = CaravanVisibilityCalculator.Visibility(transferables, new StringBuilder());

            // 送出時的拒絕（CheckForErrors）。
            if (!draft.Pawns.Any(p => CaravanUtility.IsOwner(p, Faction.OfPlayer) && !p.Downed))
                blockers.Add((ModsConfig.IdeologyActive ? "CaravanMustHaveAtLeastOneNonSlaveColonist" : "CaravanMustHaveAtLeastOneColonist").Translate().Resolve());
            if (plan.MassUsage > plan.MassCapacity) blockers.Add("TooBigCaravanMassUsage".Translate().Resolve());
            foreach (TransferableOneWay transferable in transferables)
            {
                if (transferable.ThingDef.category != ThingCategory.Item || transferable.CountToTransfer <= 0) continue;
                int reachable = 0;
                foreach (Thing thing in transferable.things)
                {
                    if (!thing.Spawned || draft.Pawns.Any(p => p.IsColonist && p.CanReach(thing, PathEndMode.Touch, Danger.Deadly))) reachable += thing.stackCount;
                    if (reachable >= transferable.CountToTransfer) break;
                }
                if (reachable >= transferable.CountToTransfer) continue;
                blockers.Add(transferable.CountToTransfer == 1
                    ? "CaravanItemIsUnreachableSingle".Translate(transferable.ThingDef.label).Resolve()
                    : "CaravanItemIsUnreachableMulti".Translate(transferable.CountToTransfer, transferable.ThingDef.label).Resolve());
            }

            // 警告（TrySend 的「確定嗎」清單，只收我們能原樣算的幾項）。
            if (plan.FoodDays < 5f)
                warnings.Add(plan.FoodDays < 0.1f
                    ? "DaysWorthOfFoodWarningDialog_NoFood".Translate().Resolve()
                    : "DaysWorthOfFoodWarningDialog".Translate(plan.FoodDays.ToString("0.#")).Resolve());
            else if (MostCaravanFoodWillRotSoon(transferables, origin))
                warnings.Add("CaravanFoodWillRotSoonWarningDialog".Translate().Resolve());
            if (!draft.Pawns.Any(p => CaravanUtility.IsOwner(p, Faction.OfPlayer) && !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled))
                warnings.Add("CaravanIncapableOfSocial".Translate().Resolve());

            // 出口與集合點（TryFormAndSendCaravan）。起始格或隊伍不成立時不必找。
            if (draft.StartingTile.Valid && draft.Pawns.Any(p => !p.Downed))
                FindCaravanSpots(draft, map, blockers, warnings);

            plan.Blockers = blockers;
            plan.Warnings = warnings;
            draft.Plan = plan;
        }

        /// <summary>視窗的 MostFoodWillRotSoon：帶的食物營養有七成五以上不到五天就壞。</summary>
        private static bool MostCaravanFoodWillRotSoon(List<TransferableOneWay> transferables, PlanetTile tile)
        {
            float soon = 0f;
            float later = 0f;
            foreach (TransferableOneWay transferable in transferables)
            {
                if (!transferable.HasAnyThing || transferable.CountToTransfer <= 0 || !transferable.ThingDef.IsNutritionGivingIngestible || transferable.AnyThing is Corpse) continue;
                float days = 600f;
                CompRottable rottable = transferable.AnyThing.TryGetComp<CompRottable>();
                if (rottable != null) days = DaysUntilRotCalculator.ApproxTicksUntilRot_AssumeTimePassesBy(rottable, tile) / 60000f;
                float nutrition = transferable.ThingDef.GetStatValueAbstract(StatDefOf.Nutrition) * transferable.CountToTransfer;
                if (days < 5f) soon += nutrition;
                else later += nutrition;
            }
            return soon + later > 0f && soon / (soon + later) >= 0.75f;
        }

        /// <summary>找出口與集合點；找不到就加進拒絕原因。邏輯照 Dialog_FormCaravan.TryFormAndSendCaravan（它們是私有方法）。</summary>
        private static void FindCaravanSpots(CaravanDraft draft, Map map, List<string> blockers, List<string> warnings)
        {
            List<Pawn> pawns = draft.Pawns;
            IntVec3 root = pawns.Aggregate(IntVec3.Zero, (acc, p) => acc + p.Position) / pawns.Count;
            string direction = Find.WorldGrid.GetDirection8WayFromTo(map.Tile, draft.StartingTile).LabelShort();
            IntVec3 exit;
            if (!RCellFinder.TryFindClosestEdgeCellTo(root, map, out exit) && !TryFindCaravanExitSpot(map, pawns, draft.StartingTile, true, out exit))
            {
                if (!TryFindCaravanExitSpot(map, pawns, draft.StartingTile, false, out exit))
                {
                    blockers.Add("CaravanCouldNotFindExitSpot".Translate(direction).Resolve());
                    return;
                }
                warnings.Add("CaravanCouldNotFindReachableExitSpot".Translate(direction).Resolve());
            }
            IntVec3 meeting;
            if (!TryFindCaravanPackingSpot(map, exit, out meeting))
            {
                blockers.Add("CaravanCouldNotFindPackingSpot".Translate(direction).Resolve());
                return;
            }
            Pawn roamer = pawns.Find(ropee => AnimalPenUtility.NeedsToBeManagedByRope(ropee) && !pawns.Any(roper => roper.IsColonist
                && GatherAnimalsAndSlavesForCaravanUtility.CanRoperTakeAnimalToDest(roper, ropee, exit)
                && GatherAnimalsAndSlavesForCaravanUtility.CanRoperTakeAnimalToDest(roper, ropee, meeting)));
            if (roamer != null)
            {
                blockers.Add("CaravanRoamerCannotReachSpots".Translate(roamer.LabelShort, roamer).Resolve());
                return;
            }
            draft.ExitSpot = exit;
            draft.MeetingPoint = meeting;
        }

        private static bool TryFindCaravanExitSpot(Map map, List<Pawn> pawns, PlanetTile startingTile, bool reachableForEveryColonist, out IntVec3 spot)
        {
            CaravanExitMapUtility.GetExitMapEdges(Find.WorldGrid, map.Tile, startingTile, out Rot4 primary, out Rot4 secondary);
            spot = IntVec3.Invalid;
            if (primary != Rot4.Invalid && TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, primary, out spot)) return true;
            if (secondary != Rot4.Invalid && TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, secondary, out spot)) return true;
            if (TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, primary.Rotated(RotationDirection.Clockwise), out spot)) return true;
            return TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, primary.Rotated(RotationDirection.Counterclockwise), out spot);
        }

        private static bool TryFindCaravanExitSpotOnEdge(Map map, List<Pawn> pawns, bool reachableForEveryColonist, Rot4 exitDirection, out IntVec3 spot)
        {
            if (reachableForEveryColonist)
            {
                return CellFinder.TryFindRandomEdgeCellWith(
                    c => !c.Fogged(map) && c.Standable(map) && pawns.All(p => !p.IsColonist || p.Downed || p.CanReach(c, PathEndMode.Touch, Danger.Deadly)),
                    map, exitDirection, CellFinder.EdgeRoadChance_Always, out spot);
            }
            IntVec3 best = IntVec3.Invalid;
            int bestReachable = -1;
            foreach (IntVec3 cell in CellRect.WholeMap(map).GetEdgeCells(exitDirection).InRandomOrder())
            {
                if (cell.Fogged(map) || !cell.Standable(map)) continue;
                int reachable = pawns.Count(p => p.IsColonist && !p.Downed && p.CanReach(cell, PathEndMode.Touch, Danger.Deadly));
                if (reachable > bestReachable)
                {
                    bestReachable = reachable;
                    best = cell;
                }
            }
            spot = best;
            return best.IsValid;
        }

        private static bool TryFindCaravanPackingSpot(Map map, IntVec3 exitSpot, out IntVec3 packingSpot)
        {
            TraverseParms traverseParams = TraverseParms.For(TraverseMode.PassDoors);
            List<Thing> spots = map.listerThings.ThingsOfDef(ThingDefOf.CaravanPackingSpot)
                .Where(t => map.reachability.CanReach(exitSpot, t, PathEndMode.OnCell, traverseParams)).ToList();
            if (spots.Count > 0)
            {
                packingSpot = spots.RandomElement().Position;
                return true;
            }
            return RCellFinder.TryFindRandomSpotJustOutsideColony(exitSpot, map, out packingSpot);
        }

        public IReadOnlyList<FormingCaravanInfo> GetFormingCaravans()
        {
            var result = new List<FormingCaravanInfo>();
            foreach (Map map in Find.Maps)
            {
                foreach (Lord lord in map.lordManager.lords)
                {
                    var job = lord.LordJob as LordJob_FormAndSendCaravan;
                    if (job == null || lord.faction != Faction.OfPlayer) continue;
                    result.Add(new FormingCaravanInfo
                    {
                        Id = lord.loadID,
                        Status = PlainText(job.Status),
                        Members = lord.ownedPawns.Concat(job.downedPawns).Distinct().Select(ToCaravanMember).ToList(),
                        Cargo = job.transferables.Where(t => t.HasAnyThing && t.CountToTransfer > 0)
                            .Select(t => t.Label + " x" + t.CountToTransfer).ToList(),
                        ExitX = job.ExitSpot.x,
                        ExitZ = job.ExitSpot.z
                    });
                }
            }
            return result;
        }

        public CommandResult CancelFormingCaravan(int formingId)
        {
            foreach (Map map in Find.Maps)
            {
                Lord lord = map.lordManager.lords.FirstOrDefault(l => l.loadID == formingId && l.LordJob is LordJob_FormAndSendCaravan);
                if (lord == null) continue;
                string names = string.Join(", ", lord.ownedPawns.Select(p => p.LabelShort).ToArray());
                CaravanFormingUtility.StopFormingCaravan(lord);
                return CommandResult.Ok($"Stopped forming the caravan ({names}); they put down what they were carrying and go back to their usual work");
            }
            return CommandResult.Fail($"no caravan is being formed with id {formingId}");
        }

        // ---- 世界地圖：商隊與世界物件 ----

        private static Caravan FindPlayerCaravan(int id)
        {
            return Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == id && c.IsPlayerControlled);
        }

        /// <summary>
        /// 查詢與距離的起點：指定的商隊，否則當前地圖所在的格子。找不到起點時回傳無效格（呼叫端要檢查 Valid）。
        /// </summary>
        private static PlanetTile ResolveOrigin(int originCaravanId, out Caravan caravan, out string label)
        {
            caravan = originCaravanId >= 0 ? FindPlayerCaravan(originCaravanId) : null;
            if (caravan != null)
            {
                label = caravan.LabelCap;
                return caravan.Tile;
            }
            Map map = Find.CurrentMap;
            if (map == null)
            {
                label = "no map";
                return PlanetTile.Invalid;
            }
            label = map.Parent?.LabelCap ?? ("Map " + map.uniqueID);
            return map.Tile;
        }

        /// <summary>工具給的格子編號對到起點同一個星球層的格子（預設地表）。</summary>
        private static PlanetTile ToPlanetTile(int id, PlanetTile origin)
        {
            return origin.Valid ? new PlanetTile(id, origin.Layer) : new PlanetTile(id);
        }

        private static bool TileInWorld(PlanetTile tile)
        {
            return tile.Valid && Find.WorldGrid.InBounds(tile);
        }

        /// <summary>格子的白話名稱：上面有世界物件就用物件名與編號（排除商隊），否則是格子編號加生態。</summary>
        private static string DescribeWorldTile(PlanetTile tile)
        {
            if (!TileInWorld(tile)) return "no tile";
            WorldObject obj = Find.WorldObjects.ObjectsAt(tile).FirstOrDefault(o => !(o is Caravan));
            string text = "tile " + tile.tileId + " (" + tile.Tile.PrimaryBiome.LabelCap + ")";
            return obj == null ? text : obj.LabelCap + " [" + obj.ID + "], " + text;
        }

        public IReadOnlyList<CaravanInfo> GetCaravans()
        {
            return Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled).Select(ToCaravanInfo).ToList();
        }

        private static CaravanInfo ToCaravanInfo(Caravan caravan)
        {
            // 就地建立的可否與原因直接讀原版商隊按鈕（Caravan.GetGizmos 的同一組判斷）。
            Command settle = SettleInEmptyTileUtility.SettleCommand(caravan);
            Command camp = SettleInEmptyTileUtility.SetupCamp(caravan);
            // 沒在走時 Destination 是上次的殘值（struct 預設是有效的 0 號格），只在 Moving 時讀。
            bool moving = caravan.pather.Moving;
            PlanetTile destination = moving ? caravan.pather.Destination : PlanetTile.Invalid;
            return new CaravanInfo
            {
                Id = caravan.ID,
                Name = caravan.LabelCap,
                Tile = caravan.Tile,
                Terrain = caravan.Tile.Tile.PrimaryBiome.LabelCap + ", " + caravan.Tile.Tile.hilliness.GetLabelCap(),
                Members = caravan.PawnsListForReading.Select(ToCaravanMember).ToList(),
                Moving = moving,
                Paused = caravan.pather.Paused,
                Resting = caravan.NightResting,
                Immobilized = caravan.ImmobilizedByMass,
                AllOwnersDowned = caravan.AllOwnersDowned,
                AllOwnersInMentalBreak = caravan.AllOwnersHaveMentalBreak,
                DestinationTile = destination.Valid ? destination.tileId : -1,
                DestinationLabel = destination.Valid ? DescribeWorldTile(destination) : string.Empty,
                ArrivalAction = moving ? SafeArrivalActionLabel(caravan.pather.ArrivalAction) : string.Empty,
                EtaTicks = moving ? CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan, allowCaching: false) : 0,
                FoodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(caravan),
                MassUsage = caravan.MassUsage,
                MassCapacity = caravan.MassCapacity,
                TicksPerMove = caravan.TicksPerMove,
                CanSettle = !settle.Disabled,
                SettleBlockedReason = settle.Disabled ? PlainText(settle.disabledReason) : string.Empty,
                CanCamp = !camp.Disabled,
                CampBlockedReason = camp.Disabled ? PlainText(camp.disabledReason) : string.Empty,
                ObjectsHere = Find.WorldObjects.ObjectsAt(caravan.Tile).Where(o => o != caravan).Select(o => o.LabelCap + " [" + o.ID + "]").ToList()
            };
        }

        /// <summary>抵達動作的標籤；模組的動作可能在欄位為空時丟例外，當作沒有。</summary>
        private static string SafeArrivalActionLabel(CaravanArrivalAction action)
        {
            if (action == null) return string.Empty;
            try
            {
                return PlainText(action.Label);
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: could not read a caravan arrival action label: " + ex.Message);
                return string.Empty;
            }
        }

        private static CaravanMemberInfo ToCaravanMember(Pawn pawn)
        {
            string kind = CaravanMemberInfo.OtherKind;
            if (pawn.IsPrisoner) kind = CaravanMemberInfo.PrisonerKind;
            else if (pawn.IsSlave) kind = CaravanMemberInfo.SlaveKind;
            else if (pawn.RaceProps.Animal) kind = CaravanMemberInfo.AnimalKind;
            else if (pawn.RaceProps.IsMechanoid) kind = CaravanMemberInfo.MechKind;
            else if (pawn.IsColonist) kind = CaravanMemberInfo.ColonistKind;
            return new CaravanMemberInfo
            {
                Id = pawn.thingIDNumber,
                Name = pawn.LabelShort,
                Kind = kind,
                Downed = pawn.Downed,
                InMentalState = pawn.InMentalState,
                HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f
            };
        }

        public WorldSurveyInfo GetWorldSurvey(int originCaravanId)
        {
            PlanetTile origin = ResolveOrigin(originCaravanId, out Caravan fromCaravan, out string label);
            Dictionary<int, List<WorldObjectQuestLink>> quests = QuestLinksByWorldObject();
            var objects = new List<WorldObjectInfo>();
            foreach (WorldObject obj in Find.WorldObjects.AllWorldObjects)
            {
                if (!obj.Spawned || !obj.Tile.Valid) continue;
                if (origin.Valid && obj.Tile.Layer != origin.Layer) continue;
                if (obj is RoutePlannerWaypoint) continue;
                if (obj is Caravan own && own.IsPlayerControlled) continue;
                objects.Add(ToWorldObjectInfo(obj, origin, quests));
            }
            return new WorldSurveyInfo
            {
                OriginTile = origin.Valid ? origin.tileId : -1,
                OriginLabel = label,
                OriginIsCaravan = fromCaravan != null,
                Objects = objects
            };
        }

        private static WorldObjectInfo ToWorldObjectInfo(WorldObject obj, PlanetTile origin, Dictionary<int, List<WorldObjectQuestLink>> quests)
        {
            Faction faction = obj.Faction;
            string kind = WorldObjectInfo.OtherKind;
            if (obj is Settlement) kind = faction == Faction.OfPlayer ? WorldObjectInfo.PlayerSettlementKind : WorldObjectInfo.FactionSettlementKind;
            else if (obj is Site) kind = WorldObjectInfo.SiteKind;
            else if (obj is Camp) kind = WorldObjectInfo.CampKind;
            else if (obj is Caravan) kind = WorldObjectInfo.CaravanKind;
            var info = new WorldObjectInfo
            {
                Id = obj.ID,
                Kind = kind,
                Label = obj.LabelCap,
                DefName = obj.def.defName,
                Tile = obj.Tile.tileId,
                Faction = faction?.Name ?? string.Empty,
                Hostile = faction != null && faction != Faction.OfPlayer && faction.HostileTo(Faction.OfPlayer),
                HasMap = obj is MapParent mapParent && mapParent.HasMap,
                DistanceTiles = origin.Valid ? Find.WorldGrid.ApproxDistanceInTiles(origin, obj.Tile) : 0f,
                Quests = quests.TryGetValue(obj.ID, out List<WorldObjectQuestLink> links) ? links : new List<WorldObjectQuestLink>()
            };
            if (obj is Site site)
            {
                info.ExpiresInTicks = site.WorldObjectTimeoutTicksLeft;
                if (site.sitePartsKnown)
                    info.Detail = string.Join(", ", site.parts.Select(p => p.def.label).Distinct().ToArray());
            }
            return info;
        }

        /// <summary>世界物件編號 → 把它列為目標的任務（待接受與進行中）；任務目標清單來自各任務段落的 QuestLookTargets。</summary>
        private static Dictionary<int, List<WorldObjectQuestLink>> QuestLinksByWorldObject()
        {
            var links = new Dictionary<int, List<WorldObjectQuestLink>>();
            foreach (Quest quest in Find.QuestManager.QuestsListForReading)
            {
                if (quest.hidden || quest.hiddenInUI) continue;
                if (quest.State != QuestState.NotYetAccepted && quest.State != QuestState.Ongoing) continue;
                try
                {
                    foreach (WorldObject obj in quest.QuestLookTargets.Where(t => t.HasWorldObject).Select(t => t.WorldObject).Where(o => o != null).Distinct())
                    {
                        if (!links.TryGetValue(obj.ID, out List<WorldObjectQuestLink> list)) links[obj.ID] = list = new List<WorldObjectQuestLink>();
                        list.Add(new WorldObjectQuestLink { QuestId = quest.id, QuestName = PlainText(quest.name), Active = quest.State == QuestState.Ongoing });
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("RimAgent: could not read the world targets of quest " + quest.id + ": " + ex.Message);
                }
            }
            return links;
        }

        public IReadOnlyList<TravelEstimateInfo> GetTravelEstimates(int originCaravanId, IReadOnlyList<int> tiles)
        {
            PlanetTile origin = ResolveOrigin(originCaravanId, out Caravan fromCaravan, out string _);
            var result = new List<TravelEstimateInfo>();
            foreach (int id in tiles)
            {
                PlanetTile to = ToPlanetTile(id, origin);
                var estimate = new TravelEstimateInfo { Tile = id };
                if (origin.Valid && TileInWorld(to) && to.Layer == origin.Layer)
                {
                    estimate.DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(origin, to);
                    estimate.Reachable = fromCaravan != null ? fromCaravan.CanReach(to) : Find.WorldReachability.CanReach(origin, to);
                    // 沒有商隊時傳 null：遊戲以預設速度（每格 3300 tick）估算。
                    if (estimate.Reachable && origin != to)
                        estimate.Ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin, to, fromCaravan);
                }
                result.Add(estimate);
            }
            return result;
        }

        public WorldTileInfo GetWorldTile(int tile, int originCaravanId)
        {
            PlanetTile origin = ResolveOrigin(originCaravanId, out Caravan _, out string _);
            PlanetTile pt = ToPlanetTile(tile, origin);
            if (!TileInWorld(pt))
                return new WorldTileInfo { Tile = tile, Error = $"tile {tile} does not exist on this layer of the world (valid ids are 0 to {pt.Layer.TilesCount - 1})" };
            RimWorld.Planet.Tile data = Find.WorldGrid[pt];
            var reason = new System.Text.StringBuilder();
            bool canSettle = TileFinder.IsValidTileForNewSettlement(pt, reason);
            if (canSettle && SettleUtility.PlayerSettlementsCountLimitReached)
            {
                canSettle = false;
                reason.Append(Prefs.MaxNumberOfPlayerSettlements > 1
                    ? "CommandSettleFailReachedMaximumNumberOfBases".Translate().ToString()
                    : "CommandSettleFailAlreadyHaveBase".Translate().ToString());
            }
            Dictionary<int, List<WorldObjectQuestLink>> quests = QuestLinksByWorldObject();
            Landmark landmark = data.Landmark;
            return new WorldTileInfo
            {
                Tile = tile,
                Biome = data.PrimaryBiome.LabelCap,
                Hilliness = data.hilliness.GetLabelCap(),
                TemperatureC = data.temperature,
                MinTemperatureC = data.MinTemperature,
                MaxTemperatureC = data.MaxTemperature,
                RainfallMm = data.rainfall,
                ElevationM = data.elevation,
                Impassable = Find.World.Impassable(pt),
                WaterCovered = data.WaterCovered,
                Coastal = data.IsCoastal,
                Feature = data.feature?.name ?? string.Empty,
                Landmark = landmark == null ? string.Empty : (landmark.name.NullOrEmpty() ? landmark.def.label : landmark.name),
                Mutators = data.Mutators.Select(m => m.label).ToList(),
                CanSettle = canSettle,
                SettleBlockedReason = canSettle ? string.Empty : PlainText(reason.ToString()),
                Objects = Find.WorldObjects.ObjectsAt(pt).Select(o => ToWorldObjectInfo(o, origin, quests)).ToList()
            };
        }

        /// <summary>商隊對一格能下的一道命令：標籤與執行動作。</summary>
        private sealed class CaravanOrder
        {
            public string Label = string.Empty;
            public bool Disabled;
            public Action Run;
        }

        /// <summary>
        /// 第 1 項是單純前往（與玩家在世界地圖對空地按右鍵相同：AutoOrderToTileNow 的 BestGotoDestNear 加 StartPath），
        /// 其餘是原版世界右鍵選單 FloatMenuMakerWorld.ChoicesAtFor 對這格上所有世界物件產生的選項，開發者模式的瞬間抵達選項不收。
        /// </summary>
        private static List<CaravanOrder> BuildCaravanOrders(Caravan caravan, PlanetTile to)
        {
            var orders = new List<CaravanOrder>();
            string name = "Travel to tile " + to.tileId;
            if (to == caravan.Tile)
            {
                orders.Add(new CaravanOrder { Label = name + " (the caravan is already here)", Disabled = true });
            }
            else
            {
                PlanetTile destination = CaravanUtility.BestGotoDestNear(to, caravan);
                if (!destination.Valid)
                    orders.Add(new CaravanOrder { Label = name + " (no reachable tile nearby)", Disabled = true });
                else
                    orders.Add(new CaravanOrder
                    {
                        Label = destination == to ? name : name + " (stops at the nearest reachable tile " + destination.tileId + ")",
                        Run = () => caravan.pather.StartPath(destination, null, repathImmediately: true)
                    });
            }
            try
            {
                foreach (FloatMenuOption option in FloatMenuMakerWorld.ChoicesAtFor(to, caravan))
                {
                    if (option.Label.Contains("(Dev:")) continue;
                    FloatMenuOption chosen = option;
                    orders.Add(new CaravanOrder { Label = chosen.Label.StripTags(), Disabled = chosen.Disabled, Run = () => chosen.Chosen(colonistOrdering: false, null) });
                }
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: building the caravan world menu failed: " + ex);
            }
            return orders;
        }

        public CaravanOptionsInfo GetCaravanOptions(int caravanId, int tile)
        {
            Caravan caravan = FindPlayerCaravan(caravanId);
            if (caravan == null) return new CaravanOptionsInfo { Error = "caravan not found" };
            PlanetTile to = ToPlanetTile(tile, caravan.Tile);
            if (!TileInWorld(to)) return new CaravanOptionsInfo { Error = $"tile {tile} does not exist on this layer of the world", CaravanName = caravan.LabelCap };
            return new CaravanOptionsInfo
            {
                CaravanName = caravan.LabelCap,
                Options = BuildCaravanOrders(caravan, to).Select((o, i) => new FloatMenuOptionInfo { Number = i + 1, Label = o.Label, Disabled = o.Disabled }).ToList()
            };
        }

        public CommandResult SendCaravan(int caravanId, int tile, int optionNumber, string label)
        {
            Caravan caravan = FindPlayerCaravan(caravanId);
            if (caravan == null) return CommandResult.Fail("caravan not found");
            PlanetTile to = ToPlanetTile(tile, caravan.Tile);
            if (!TileInWorld(to)) return CommandResult.Fail($"tile {tile} does not exist on this layer of the world");
            List<CaravanOrder> orders = BuildCaravanOrders(caravan, to);
            CaravanOrder order = optionNumber >= 1 && optionNumber <= orders.Count ? orders[optionNumber - 1] : null;
            if (order == null || !string.Equals(order.Label, label, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"{caravan.LabelCap}'s orders for tile {tile} changed; call get_world_tile again");
            if (order.Disabled) return CommandResult.Fail($"\"{label}\" is disabled");

            bool startedOnTile = to == caravan.Tile;
            StopAllTargeting();
            var before = new HashSet<Window>(Find.WindowStack.Windows);
            var watch = new MessageWatch();
            try
            {
                order.Run();
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: caravan order \"" + label + "\" threw: " + ex);
                return CommandResult.Fail($"\"{label}\" failed in the game: {ex.Message}");
            }

            // 命令若接著要選目標或開別的視窗，這裡沒辦法回應，收掉並回報；確認框與對話留著給 ChooseOption。
            bool targeting = Find.Targeter.IsTargeting || Find.WorldTargeter.IsTargeting;
            StopAllTargeting();
            CommandResult windowFailure = CloseUnsupportedWindows(before, label);
            if (targeting) return CommandResult.Fail($"\"{label}\" asks for a further target, which is not supported yet; it was cancelled");
            if (windowFailure != null) return windowFailure;

            if (caravan.Destroyed || !caravan.Spawned)
                return CommandResult.Ok(WithGameMessages($"\"{label}\" done: the caravan has left the world map (it entered a map or was dissolved)", watch));
            if (caravan.pather.Moving)
                return CommandResult.Ok(WithGameMessages($"{caravan.LabelCap} is on its way to {DescribeWorldTile(caravan.pather.Destination)}", watch));
            if (Find.WindowStack.Windows.Any(w => !before.Contains(w)))
                return CommandResult.Ok(WithGameMessages($"\"{label}\" is waiting for an answer", watch));
            if (startedOnTile)
                return CommandResult.Ok(WithGameMessages($"\"{label}\" done at {DescribeWorldTile(to)}", watch));
            return CommandResult.Fail(WithGameMessages($"{caravan.LabelCap} did not start moving", watch));
        }

        public CommandResult SetCaravanMovement(int caravanId, string action)
        {
            Caravan caravan = FindPlayerCaravan(caravanId);
            if (caravan == null) return CommandResult.Fail("caravan not found");
            Caravan_PathFollower pather = caravan.pather;
            switch (action)
            {
                case CaravanMovementAction.Pause:
                    if (!pather.Moving) return CommandResult.Fail($"{caravan.LabelCap} has no destination, so there is nothing to pause");
                    if (pather.Paused) return CommandResult.Ok($"{caravan.LabelCap} is already paused.");
                    pather.Paused = true;
                    return CommandResult.Ok($"{caravan.LabelCap} paused on tile {caravan.Tile.tileId}; its destination is kept.");
                case CaravanMovementAction.Resume:
                    if (!pather.Moving) return CommandResult.Fail($"{caravan.LabelCap} has no destination; send it somewhere first");
                    if (!pather.Paused) return CommandResult.Ok($"{caravan.LabelCap} is already travelling.");
                    pather.Paused = false;
                    return CommandResult.Ok($"{caravan.LabelCap} resumed its journey to {DescribeWorldTile(pather.Destination)}.");
                case CaravanMovementAction.Stop:
                    if (!pather.Moving) return CommandResult.Ok($"{caravan.LabelCap} is already stopped.");
                    pather.StopDead();
                    return CommandResult.Ok($"{caravan.LabelCap} stopped on tile {caravan.Tile.tileId}; its destination was cleared.");
                default:
                    return CommandResult.Fail($"unknown action \"{action}\"");
            }
        }

        public CommandResult SettleCaravan(int caravanId, string action)
        {
            Caravan caravan = FindPlayerCaravan(caravanId);
            if (caravan == null) return CommandResult.Fail("caravan not found");
            bool camp = action == CaravanSettleAction.Camp;
            if (!camp && action != CaravanSettleAction.Settle) return CommandResult.Fail($"unknown action \"{action}\"");
            // 與玩家按商隊按鈕同一條路：可否判斷、確認框與背景生成地圖都在原版的按鈕動作裡。
            Command command = camp ? SettleInEmptyTileUtility.SetupCamp(caravan) : SettleInEmptyTileUtility.SettleCommand(caravan);
            string verb = camp ? "set up camp" : "found a colony";
            if (command.Disabled) return CommandResult.Fail($"{caravan.LabelCap} cannot {verb} here: {PlainText(command.disabledReason)}");

            StopAllTargeting();
            var before = new HashSet<Window>(Find.WindowStack.Windows);
            var watch = new MessageWatch();
            try
            {
                ((Command_Action)command).action();
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: caravan " + verb + " threw: " + ex);
                return CommandResult.Fail($"could not {verb} in the game: {ex.Message}");
            }
            CommandResult windowFailure = CloseUnsupportedWindows(before, verb);
            if (windowFailure != null) return windowFailure;
            if (Find.WindowStack.Windows.Any(w => !before.Contains(w)))
                return CommandResult.Ok(WithGameMessages($"The game asks to confirm before {caravan.LabelCap} can {verb} on tile {caravan.Tile.tileId}", watch));
            return CommandResult.Ok(WithGameMessages(
                $"{caravan.LabelCap} is starting to {verb} on tile {caravan.Tile.tileId}. The map is generated in the background; it shows up in get_maps when it is ready", watch));
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
