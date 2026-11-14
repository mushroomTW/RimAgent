using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimAgent.Agent;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 真模型 benchmark：在同一張地圖上依序下一組固定指令，每題前清空對話，跑完依遊戲狀態自動判分，
/// 輸出成功率、工具呼叫與失敗數、耗時，用來比較模型與 RimAgent 版本。不是測試，不斷言。
/// 只有隔離環境的 Config 有 <see cref="MarkerFileName"/> 才啟動；模型照 seed 進來的玩家設定，
/// 標記檔根節點可帶 model="供應商:模型" 覆寫。結果附加到 SaveData/RimAgent/Benchmark.jsonl，
/// 最後一行 Log "[RimAgent.InGameTests] Benchmark DONE" 彙總。
/// </summary>
public sealed class BenchmarkComponent : GameComponent
{
    public const string MarkerFileName = "RimAgentBenchmark.xml";
    private const string LogPrefix = TestHelpers.LogPrefix + "Benchmark ";
    private const int MaxReplyChars = 300;

    private static bool _started;
    private float _readyAt = -1f;

    private const string Basic = "basic";
    private const string Hard = "hard";

    // 高難度題的場景準備會生成物件或挑選對象，判分時要用同一批。
    private static Building _stove;
    private static Building _butcherTable;
    private static Pawn _injured;
    /// <summary>醫療技能並列最高的其餘殖民者；指令說「最高的那一位」，同分時任一位都算對。</summary>
    private static readonly List<Pawn> _doctors = [];
    private static readonly List<Pawn> _enemies = [];

    /// <summary>一題：指令、量測（指令前後各量一次）與判分。</summary>
    private sealed class BenchTask
    {
        public string Id;
        public string Tier = Basic;
        public string Instruction;
        /// <summary>送出指令前佈置場景；回傳 null 表示成功，否則為無法佈置的原因（該題記為失敗、不送指令）。</summary>
        public Func<Map, string> Setup;
        /// <summary>判分後還原會影響後續題目的狀態。</summary>
        public Action<Map> Cleanup;
        public Func<Map, Dictionary<string, int>> Measure;
        /// <summary>依前後量測與模型回覆判分，回傳 null 表示通過，否則為失敗原因。</summary>
        public Func<Dictionary<string, int>, Dictionary<string, int>, string, string> Check;
    }

    private static readonly BenchTask[] Tasks =
    [
        new BenchTask
        {
            Id = "stockpile",
            Instruction = "在殖民者附近建立一個大約 6x6 的儲存區。",
            Measure = map => new() { ["cells"] = ZoneCells<Zone_Stockpile>(map, _ => true) },
            Check = (before, after, _) => Delta(before, after, "cells") >= 20 ? null : $"stockpile cells +{Delta(before, after, "cells")}, need +20"
        },
        new BenchTask
        {
            Id = "rice_field",
            Instruction = "在殖民者附近開一塊至少 30 格的水稻田。",
            Measure = map => new() { ["cells"] = ZoneCells<Zone_Growing>(map, z => z.GetPlantDefToGrow()?.defName == "Plant_Rice") },
            Check = (before, after, _) => Delta(before, after, "cells") >= 25 ? null : $"rice zone cells +{Delta(before, after, "cells")}, need +25"
        },
        new BenchTask
        {
            Id = "research_bench",
            Instruction = "在殖民者附近放一張簡易研究台的藍圖。",
            Measure = map => new() { ["benches"] = CountBuildTargets(map, d => d.defName.Contains("ResearchBench")) },
            Check = (before, after, _) => Delta(before, after, "benches") >= 1 ? null : "no new research bench blueprint"
        },
        new BenchTask
        {
            Id = "wood_room",
            Instruction = "在殖民者附近的空地上，用木頭規劃一間內部 5x5 的小房間藍圖（四面牆加一扇門）。",
            Measure = map => new()
            {
                ["walls"] = CountBuildTargets(map, d => d == ThingDefOf.Wall),
                ["doors"] = CountBuildTargets(map, d => d.IsDoor)
            },
            Check = (before, after, _) =>
                Delta(before, after, "walls") >= 16 && Delta(before, after, "doors") >= 1
                    ? null
                    : $"walls +{Delta(before, after, "walls")} (need +16), doors +{Delta(before, after, "doors")} (need +1)"
        },
        new BenchTask
        {
            Id = "research_priority",
            Instruction = "讓每一位能做研究的殖民者，把研究工作的優先級設為 1。",
            Measure = map => new() { ["missing"] = ColonistsWithoutResearchPriorityOne(map) },
            Check = (_, after, _) => after["missing"] == 0 ? null : $"{after["missing"]} capable colonist(s) not at research priority 1"
        },
        new BenchTask
        {
            Id = "cut_trees",
            Instruction = "標記砍伐殖民者附近的 5 棵樹。",
            Measure = map => new() { ["marked"] = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.CutPlant).Count() },
            Check = (before, after, _) => Delta(before, after, "marked") >= 5 ? null : $"cut designations +{Delta(before, after, "marked")}, need +5"
        },
        new BenchTask
        {
            Id = "food_report",
            Instruction = "告訴我殖民地目前的食物存量大概能撐幾天。只要回答，不要下任何指令。",
            Measure = _ => new(),
            Check = (_, _, reply) => reply != null && Regex.IsMatch(reply, "[0-9０-９]") ? null : "reply has no number"
        },

        // ---- 高難度：多步驟、空間規劃、跨工具推理 ----
        new BenchTask
        {
            Id = "two_room_house",
            Tier = Hard,
            Instruction = "在殖民者附近的空地上，用木頭規劃一棟有兩個房間的建築，兩間房共用一面牆、各有一扇門："
                + "一間是放兩張床的臥室，另一間是放一張簡易研究台的研究室。",
            Setup = ClearBlueprints,
            Measure = map => new()
            {
                ["woodWalls"] = CountBuildTargets(map, d => d == ThingDefOf.Wall, ThingDefOf.WoodLog),
                ["doors"] = CountBuildTargets(map, d => d.IsDoor),
                ["beds"] = CountBuildTargets(map, IsRealBed),
                ["benches"] = CountBuildTargets(map, d => d.defName.Contains("ResearchBench")),
                ["spread"] = WallBounds(map) is CellRect r ? Math.Max(r.Width, r.Height) : 0
            },
            Check = (before, after, _) => Fails(
                Need(after, before, "woodWalls", 20), Need(after, before, "doors", 2),
                Need(after, before, "beds", 2), Need(after, before, "benches", 1),
                after["spread"] <= 20 ? null : $"walls spread over {after["spread"]} cells (max 20, not one building)")
        },
        new BenchTask
        {
            Id = "freezer",
            Tier = Hard,
            Instruction = "空調研究已經完成。在殖民者附近用木頭規劃一間內部至少 3x3 的冷凍庫：四面牆、一扇門、"
                + "牆上裝一台冷氣，並在房間裡面設一個儲存區。",
            Setup = map => ClearBlueprints(map) ?? FinishResearch("Electricity", "AirConditioning"),
            Measure = map => new()
            {
                ["walls"] = CountBuildTargets(map, d => d == ThingDefOf.Wall),
                ["doors"] = CountBuildTargets(map, d => d.IsDoor),
                ["coolersOnWall"] = WallBounds(map) is CellRect r ? CoolersOnWallLine(map, r) : 0,
                ["stockpileInside"] = WallBounds(map) is CellRect room ? StockpileCellsInside(map, room.ContractedBy(1)) : 0
            },
            Check = (before, after, _) => Fails(
                Need(after, before, "walls", 10), Need(after, before, "doors", 1),
                Need(after, before, "coolersOnWall", 1), Need(after, before, "stockpileInside", 4))
        },
        new BenchTask
        {
            Id = "corn_storage",
            Tier = Hard,
            Instruction = "在殖民者附近開一塊至少 30 格的玉米田，並在附近建一個專門放收成玉米的儲存區："
                + "這個儲存區要能放玉米，但不能放鋼鐵和木材。",
            Measure = map => new()
            {
                ["cornCells"] = ZoneCells<Zone_Growing>(map, z => z.GetPlantDefToGrow()?.defName == "Plant_Corn"),
                ["cornStores"] = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Count(IsCornOnlyStore)
            },
            Check = (before, after, _) => Fails(Need(after, before, "cornCells", 25), Need(after, before, "cornStores", 1))
        },
        new BenchTask
        {
            Id = "kitchen_bills",
            Tier = Hard,
            Instruction = "殖民地剛蓋好一座燒柴爐灶和一張屠宰台。在爐灶上排做 10 份簡易餐點，並在屠宰台上排屠宰動物屍體的工作單。",
            Setup = SpawnKitchen,
            Measure = _ => new()
            {
                ["mealBill"] = HasBill(_stove, "CookMealSimple", 10) ? 1 : 0,
                ["butcherBill"] = HasBill(_butcherTable, "ButcherCorpseFlesh", 1) ? 1 : 0
            },
            Check = (_, after, _) => Fails(
                after["mealBill"] == 1 ? null : "no simple meal x10 bill on the stove",
                after["butcherBill"] == 1 ? null : "no butcher corpse bill on the butcher table")
        },
        new BenchTask
        {
            Id = "injured_care",
            Tier = Hard,
            Instruction = "有一位殖民者受傷了。找出是誰，讓醫生替他治療時可以用最好的藥；"
                + "然後把其餘殖民者中醫療技能最高的那一位，醫生工作的優先級設為 1。",
            Setup = InjureColonist,
            Measure = _ => new()
            {
                ["bestCare"] = _injured?.playerSettings?.medCare == MedicalCareCategory.Best ? 1 : 0,
                ["doctorOne"] = _doctors.Any(p => p.workSettings?.GetPriority(WorkTypeDefOf.Doctor) == 1) ? 1 : 0
            },
            Check = (_, after, _) => Fails(
                after["bestCare"] == 1 ? null : $"{_injured?.LabelShort} medical care is not Best",
                after["doctorOne"] == 1
                    ? null
                    : $"none of the best other doctors ({string.Join(", ", _doctors.Select(p => p.LabelShort))}) has doctor priority 1")
        },
        new BenchTask
        {
            Id = "daily_schedule",
            Tier = Hard,
            Instruction = "把所有殖民者的作息改成：0 點到 6 點前睡覺，6 點到 22 點前工作，22 點到午夜娛樂。",
            Setup = ResetSchedules,
            Measure = map => new() { ["wrongHours"] = WrongScheduleHours(map) },
            Check = (_, after, _) => after["wrongHours"] == 0 ? null : $"{after["wrongHours"]} colonist-hours differ from the requested schedule"
        },
        new BenchTask
        {
            Id = "defend",
            Tier = Hard,
            Instruction = "地圖邊緣出現了三頭發狂的野豬。徵召所有能戰鬥的殖民者，並命令每個人去攻擊牠們。",
            Setup = SpawnEnemies,
            Cleanup = RemoveEnemies,
            Measure = map => new()
            {
                ["notDrafted"] = Fighters(map).Count(p => !p.Drafted),
                ["notAttacking"] = Fighters(map).Count(p => !IsAttackingEnemy(p))
            },
            Check = (_, after, _) => Fails(
                after["notDrafted"] == 0 ? null : $"{after["notDrafted"]} fighter(s) not drafted",
                after["notAttacking"] == 0 ? null : $"{after["notAttacking"]} fighter(s) not ordered to attack an enemy")
        }
    ];

    public BenchmarkComponent(Verse.Game game)
    {
    }

    private static bool Enabled => File.Exists(MarkerPath);

    private static string MarkerPath => Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName);

    public override void GameComponentUpdate()
    {
        if (_started || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !Enabled) return;
        // 等 RimAgent 自己的初始化跑完再送指令。
        if (_readyAt < 0f) _readyAt = UnityEngine.Time.realtimeSinceStartup + 5f;
        if (UnityEngine.Time.realtimeSinceStartup < _readyAt) return;
        _started = true;
        RunAsync();
    }

    private static async void RunAsync()
    {
        try
        {
            RimAgentSettings settings = RimAgentMod.Settings;
            RimAgentGameComponent agent = RimAgentGameComponent.Instance;
            if (settings == null || agent == null)
            {
                Log.Warning(LogPrefix + $"FAIL not started: settings={settings != null}, component={agent != null}");
                return;
            }
            // 只改隔離環境記憶體裡的設定：完整權限讓行動直接執行，關掉自主照顧免得插隊。
            string modelOverride = Regex.Match(File.ReadAllText(MarkerPath), "model=\"([^\"]+)\"").Groups[1].Value;
            if (modelOverride.Length > 0) settings.agentModel = modelOverride;
            settings.mode = AgentMode.Autonomous;
            settings.autonomousCare = false;
            settings.recordRuns = true;
            // 暫停讓地圖狀態不受時間影響；工具照常可用。
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent");
            Directory.CreateDirectory(folder);
            string resultPath = Path.Combine(folder, "Benchmark.jsonl");
            string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string modVersion = LoadedModManager.GetMod<RimAgentMod>()?.Content?.ModMetaData?.ModVersion ?? string.Empty;
            string model = string.IsNullOrWhiteSpace(settings.agentModel) ? "(framework default)" : settings.agentModel;

            ColonyAgentSession session = agent.Session;
            var passedByTier = new Dictionary<string, int> { [Basic] = 0, [Hard] = 0 };
            int totalTools = 0;
            int totalFailures = 0;
            foreach (BenchTask task in Tasks)
            {
                session.Clear();
                Map map = Find.CurrentMap;
                string failure = null;
                string reply = null;
                float seconds = 0f;
                List<TranscriptEntry> tools = [];
                try
                {
                    string setupError = task.Setup?.Invoke(map);
                    if (setupError != null)
                    {
                        failure = "setup: " + setupError;
                    }
                    else
                    {
                        Dictionary<string, int> before = task.Measure(map);
                        int transcriptStart = session.Transcript.Snapshot().Count;
                        float startedAt = UnityEngine.Time.realtimeSinceStartup;
                        try
                        {
                            reply = await session.SendAsync(task.Instruction);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(LogPrefix + task.Id + " exception: " + ex.GetType().Name + ": " + ex.Message);
                        }
                        seconds = UnityEngine.Time.realtimeSinceStartup - startedAt;
                        failure = task.Check(before, task.Measure(Find.CurrentMap), reply);
                        tools = [.. session.Transcript.Snapshot().Skip(transcriptStart).Where(e => e.Kind == TranscriptKind.Tool)];
                    }
                }
                catch (Exception ex)
                {
                    // 佈置、量測或判分出錯只算這一題失敗，其餘題目與總結照常產出。
                    failure = "exception: " + ex.GetType().Name + ": " + ex.Message;
                    Log.Warning(LogPrefix + task.Id + " " + failure);
                }
                finally
                {
                    task.Cleanup?.Invoke(map);
                }

                int failures = tools.Count(t => t.Failed && !t.Cancelled);
                totalTools += tools.Count;
                totalFailures += failures;
                if (failure == null) passedByTier[task.Tier]++;

                Append(resultPath, new Dictionary<string, object>
                {
                    ["run"] = runId,
                    ["modVersion"] = modVersion,
                    ["model"] = model,
                    ["task"] = task.Id,
                    ["tier"] = task.Tier,
                    ["ok"] = failure == null,
                    ["detail"] = failure ?? string.Empty,
                    ["tools"] = tools.Count,
                    ["toolFailures"] = failures,
                    ["toolNames"] = string.Join(",", tools.Select(t => t.ToolName)),
                    ["seconds"] = Math.Round(seconds, 1),
                    ["reply"] = Short(reply)
                });
                Log.Message(LogPrefix + $"{task.Tier}/{task.Id}: {(failure == null ? "PASS" : "FAIL " + failure)} tools={tools.Count} failed={failures} {seconds:0}s");
            }

            var summary = new Dictionary<string, object>
            {
                ["run"] = runId,
                ["modVersion"] = modVersion,
                ["model"] = model,
                ["note"] = "summary",
                ["passed"] = passedByTier.Values.Sum(),
                ["total"] = Tasks.Length,
                ["basicPassed"] = passedByTier[Basic],
                ["basicTotal"] = Tasks.Count(t => t.Tier == Basic),
                ["hardPassed"] = passedByTier[Hard],
                ["hardTotal"] = Tasks.Count(t => t.Tier == Hard),
                ["toolCalls"] = totalTools,
                ["toolFailures"] = totalFailures
            };
            Append(resultPath, summary);
            Log.Warning(LogPrefix + "DONE: " + JsonSerializer.Serialize(summary));
        }
        catch (Exception ex)
        {
            Log.Warning(LogPrefix + "FAIL exception: " + ex);
        }
    }

    private static int Delta(Dictionary<string, int> before, Dictionary<string, int> after, string key)
    {
        return after[key] - before[key];
    }

    private static int ZoneCells<T>(Map map, Func<T, bool> predicate) where T : Zone
    {
        return map.zoneManager.AllZones.OfType<T>().Where(predicate).Sum(z => z.CellCount);
    }

    /// <summary>
    /// 藍圖、施工框架與已完成建築都算：模型可能放完藍圖後等殖民者蓋好。
    /// </summary>
    /// <param name="stuff">不為 null 時只算這種材質。</param>
    private static int CountBuildTargets(Map map, Func<ThingDef, bool> predicate, ThingDef stuff = null)
    {
        return BuildTargets(map, predicate).Count(t => stuff == null || StuffOf(t) == stuff);
    }

    /// <summary>位在牆外框那一圈上的冷氣：放在房間內部或外面都不算裝在牆上。</summary>
    private static int CoolersOnWallLine(Map map, CellRect walls)
    {
        CellRect interior = walls.ContractedBy(1);
        return BuildTargets(map, d => d.defName == "Cooler")
            .Count(t => walls.Contains(t.Position) && !interior.Contains(t.Position));
    }

    private static IEnumerable<Thing> BuildTargets(Map map, Func<ThingDef, bool> predicate)
    {
        return map.listerThings.AllThings.Where(t =>
        {
            ThingDef target = t is Blueprint_Build || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def;
            return target != null && predicate(target) && t.Faction == Faction.OfPlayer;
        });
    }

    private static ThingDef StuffOf(Thing thing)
    {
        return thing is Blueprint_Build blueprint ? blueprint.stuffToUse : thing.Stuff;
    }

    /// <summary>所有牆（含藍圖）的外框；沒有牆時回傳 null。建造題會先清掉舊藍圖，所以外框就是本題的建築。</summary>
    private static CellRect? WallBounds(Map map)
    {
        List<IntVec3> cells = [.. BuildTargets(map, d => d == ThingDefOf.Wall).Select(t => t.Position)];
        if (cells.Count == 0) return null;
        return CellRect.FromLimits(cells.Min(c => c.x), cells.Min(c => c.z), cells.Max(c => c.x), cells.Max(c => c.z));
    }

    private static int StockpileCellsInside(Map map, CellRect rect)
    {
        return rect.Cells.Count(c => c.InBounds(map) && map.zoneManager.ZoneAt(c) is Zone_Stockpile);
    }

    /// <summary>真正的床，不含睡覺地鋪。</summary>
    private static bool IsRealBed(ThingDef def)
    {
        return def.IsBed && def.building != null && def.building.bed_humanlike && !def.defName.Contains("Spot");
    }

    private static bool IsCornOnlyStore(Zone_Stockpile zone)
    {
        ThingDef corn = DefDatabase<ThingDef>.GetNamedSilentFail("RawCorn");
        ThingFilter filter = zone.settings.filter;
        return corn != null && filter.Allows(corn) && !filter.Allows(ThingDefOf.Steel) && !filter.Allows(ThingDefOf.WoodLog);
    }

    private static bool HasBill(Building table, string recipeDefName, int minCount)
    {
        if (table is not IBillGiver giver || table.Destroyed) return false;
        return giver.BillStack.Bills.OfType<Bill_Production>().Any(b =>
            b.recipe.defName == recipeDefName
            && (b.repeatMode == BillRepeatModeDefOf.Forever
                || (b.repeatMode == BillRepeatModeDefOf.RepeatCount && b.repeatCount >= minCount)
                || (b.repeatMode == BillRepeatModeDefOf.TargetCount && b.targetCount >= minCount)));
    }

    private static int WrongScheduleHours(Map map)
    {
        int wrong = 0;
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.timetable != null))
        {
            for (int hour = 0; hour < 24; hour++)
            {
                TimeAssignmentDef expected = hour < 6 ? TimeAssignmentDefOf.Sleep
                    : hour < 22 ? TimeAssignmentDefOf.Work
                    : TimeAssignmentDefOf.Joy;
                if (pawn.timetable.GetAssignment(hour) != expected) wrong++;
            }
        }
        return wrong;
    }

    /// <summary>能戰鬥（未倒地、沒有不能使用暴力的特性）的殖民者。</summary>
    private static IEnumerable<Pawn> Fighters(Map map)
    {
        return map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent));
    }

    private static bool IsAttackingEnemy(Pawn pawn)
    {
        IEnumerable<Verse.AI.Job> jobs = new[] { pawn.CurJob }.Concat(pawn.jobs?.jobQueue?.Select(q => q.job) ?? Enumerable.Empty<Verse.AI.Job>());
        return jobs.Any(j => j?.targetA.Thing is Pawn target && _enemies.Contains(target));
    }

    // ---- 場景準備與還原 ----

    private static string ClearBlueprints(Map map)
    {
        foreach (Thing blueprint in map.listerThings.AllThings.Where(t => t is Blueprint && t.Faction == Faction.OfPlayer).ToList())
        {
            blueprint.Destroy(DestroyMode.Cancel);
        }
        return null;
    }

    private static string FinishResearch(params string[] defNames)
    {
        foreach (string name in defNames)
        {
            ResearchProjectDef project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name);
            if (project == null) return "missing research " + name;
            if (!project.IsFinished) Find.ResearchManager.FinishProject(project, false, null, false);
        }
        return null;
    }

    private static string SpawnKitchen(Map map)
    {
        _stove = SpawnBuilding(map, "FueledStove");
        _butcherTable = SpawnBuilding(map, "TableButcher");
        return _stove == null || _butcherTable == null ? "could not place a stove and a butcher table near the colonists" : null;
    }

    /// <summary>在殖民者附近找一塊空地，直接生成一座完工的建築。</summary>
    private static Building SpawnBuilding(Map map, string defName)
    {
        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
        if (def == null) return null;
        IntVec3 start = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
        foreach (IntVec3 cell in GenRadial.RadialCellsAround(start, 25f, false))
        {
            CellRect rect = GenAdj.OccupiedRect(cell, Rot4.North, def.size).ExpandedBy(1);
            if (!rect.InBounds(map)) continue;
            if (!rect.Cells.All(c => c.Standable(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null
                && c.GetFirstPawn(map) == null && map.zoneManager.ZoneAt(c) == null)) continue;
            var building = (Building)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            building.SetFaction(Faction.OfPlayer);
            return (Building)GenSpawn.Spawn(building, cell, map, Rot4.North);
        }
        return null;
    }

    /// <summary>
    /// 醫療最差的人受傷並取消用藥；其餘人中醫療最高者（同分則全部）的醫生優先級先調成 3，答案才不會碰巧已經對。
    /// </summary>
    private static string InjureColonist(Map map)
    {
        List<Pawn> colonists = [.. map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))];
        if (colonists.Count < 2) return "needs at least two colonists who can doctor";
        colonists.Sort((a, b) => MedicineLevel(a).CompareTo(MedicineLevel(b)));
        _injured = colonists[0];
        int best = MedicineLevel(colonists[colonists.Count - 1]);
        _doctors.Clear();
        _doctors.AddRange(colonists.Skip(1).Where(p => MedicineLevel(p) == best));

        BodyPartRecord leg = _injured.RaceProps.body.GetPartsWithDef(BodyPartDefOf.Leg).FirstOrDefault();
        Hediff cut = HediffMaker.MakeHediff(HediffDefOf.Cut, _injured, leg);
        cut.Severity = 8f;
        _injured.health.AddHediff(cut, leg);
        _injured.playerSettings.medCare = MedicalCareCategory.NoMeds;

        Find.PlaySettings.useWorkPriorities = true;
        foreach (Pawn doctor in _doctors) doctor.workSettings.SetPriority(WorkTypeDefOf.Doctor, 3);
        return null;
    }

    private static int MedicineLevel(Pawn pawn)
    {
        return pawn.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0;
    }

    private static string ResetSchedules(Map map)
    {
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.timetable != null))
        {
            for (int hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
        }
        return null;
    }

    /// <summary>
    /// 在可見的地圖邊緣生成三頭永久發狂的野豬；時間暫停，牠們不會移動。
    /// 不用敵對派系：quicktest 世界不一定有敵對的人類派系，野獸每次都一樣。
    /// </summary>
    private static string SpawnEnemies(Map map)
    {
        _enemies.Clear();
        PawnKindDef boar = DefDatabase<PawnKindDef>.GetNamedSilentFail("WildBoar");
        if (boar == null) return "missing pawn kind WildBoar";
        if (!CellFinder.TryFindRandomEdgeCellWith(c => c.Standable(map) && !c.Fogged(map), map, 0f, out IntVec3 edge))
        {
            return "no visible edge cell";
        }
        for (int i = 0; i < 3; i++)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(boar);
            GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(edge, map, 4), map);
            pawn.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
            _enemies.Add(pawn);
        }
        return null;
    }

    private static void RemoveEnemies(Map map)
    {
        foreach (Pawn pawn in _enemies.Where(p => !p.Destroyed).ToList()) pawn.Destroy();
        _enemies.Clear();
        // 取消徵召會改動 FreeColonistsSpawned，先複製再逐一處理。
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.drafter != null).ToList()) pawn.drafter.Drafted = false;
    }

    // ---- 判分小工具 ----

    /// <summary>增量未達門檻時回傳失敗原因。</summary>
    private static string Need(Dictionary<string, int> after, Dictionary<string, int> before, string key, int min)
    {
        int delta = Delta(before, after, key);
        return delta >= min ? null : $"{key} +{delta} (need +{min})";
    }

    /// <summary>合併多個檢查；全部為 null 表示通過。</summary>
    private static string Fails(params string[] reasons)
    {
        string joined = string.Join("; ", reasons.Where(r => r != null));
        return joined.Length == 0 ? null : joined;
    }

    private static int ColonistsWithoutResearchPriorityOne(Map map)
    {
        return map.mapPawns.FreeColonistsSpawned.Count(p =>
            p.workSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Research)
            && p.workSettings.GetPriority(WorkTypeDefOf.Research) != 1);
    }

    private static void Append(string path, Dictionary<string, object> line)
    {
        File.AppendAllText(path, JsonSerializer.Serialize(line) + "\n", new UTF8Encoding(false));
    }

    private static string Short(string text)
    {
        string flat = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " / ");
        return flat.Length > MaxReplyChars ? flat.Substring(0, MaxReplyChars) + "…" : flat;
    }
}
