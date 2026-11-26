using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LudeonTK;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 真模型 benchmark：在同一張地圖上依序下一組固定指令，每題前清空對話，跑完依遊戲狀態自動判分，
/// 輸出成功率、工具呼叫與失敗數、耗時，用來比較模型與 RimAgent 版本。不是測試，不斷言。
/// 只有隔離環境的 Config 有 <see cref="MarkerFileName"/> 才啟動；模型照 seed 進來的玩家設定，
/// 標記檔根節點可帶 model="供應商:模型"、reasoning="強度" 覆寫，survey="true" 只比較勘查工具，selfcheck="true" 不叫模型只檢查題目；思考強度不沿用玩家設定，預設為中，Routing 是自動。結果附加到 SaveData/RimAgent/Benchmark.jsonl，
/// 最後一行 Log "[RimAgent.InGameTests] Benchmark DONE" 彙總。
/// 也可在開發模式的除錯動作「RimAgent」分類手動起跑（全部、基本或高難度題），用目前的模型設定與開跑時選的思考強度，跑完還原。
/// </summary>
public sealed partial class BenchmarkComponent : GameComponent
{
    public const string MarkerFileName = "RimAgentBenchmark.xml";
    private const string LogPrefix = TestHelpers.LogPrefix + "Benchmark ";
    /// <summary>模型可能一直重試或等待；超過就取消這題，後面的題目照常跑。</summary>
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(10);
    private const int MaxReplyChars = 300;

    private bool _started;
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

    private const string CellsKey = "cells";
    private const string BenchesKey = "benches";
    private const string WallsKey = "walls";
    private const string DoorsKey = "doors";

    /// <summary>一題：指令、量測（指令前後各量一次）與判分。</summary>
    private sealed class BenchTask
    {
        public string Id;
        public string Tier = Basic;
        public string Instruction;
        /// <summary>技能題該讀的技能。</summary>
        public string Skill;
        /// <summary>自我檢查用的參考解，不經模型。</summary>
        public Func<IGameWorld, CommandResult> Solve;
        /// <summary>送出指令前佈置場景；回傳 null 表示成功，否則為無法佈置的原因（該題記為無效、不送指令）。</summary>
        public Func<Map, string> Setup;
        /// <summary>判分後還原會影響後續題目的狀態。</summary>
        public Action<Map> Cleanup;
        public Func<Map, Dictionary<string, int>> Measure;
        /// <summary>依前後量測與模型回覆判分，回傳 null 表示通過，否則為失敗原因。</summary>
        public Func<Dictionary<string, int>, Dictionary<string, int>, string, string> Check;
    }

    private static BenchTask[] Tasks => CreateTasks();

    private static BenchTask[] CreateTasks() => [
        StockpileTask(), RiceFieldTask(), ResearchBenchTask(), WoodRoomTask(),
        ResearchPriorityTask(), CutTreesTask(), FoodReportTask(), TwoRoomHouseTask(),
        FreezerTask(), CornStorageTask(), KitchenBillsTask(), InjuredCareTask(),
        DailyScheduleTask(), DefendTask(), SetResearchTask(), TameAnimalTask(),
        RecruitPrisonerTask(), SurgeryTask(), DeleteTask(), TradeTask(),
        MineSteelTask(), DeconstructWallTask(), UnforbidWoodTask(), CancelCutTask(), AnimalAreaTask(),
        DraftMoveTask(), WearHatTask(), DoorHoldOpenTask(), AcceptQuestTask(),
        SurveyRichSoilTask(), SurveyWallGapTask(), SurveyFirmGroundTask(), SurveyClearGroundTask(), SurveyOreTask(), SurveyFordTask(),
        SkillTriageTask(), SkillManhunterTask(), SkillNewColonistTask(), SkillFoodRunwayTask(), SkillMoodTask(), SkillScheduleTask()
    ];

    private static BenchTask StockpileTask() =>
        new()
        {
            Id = "stockpile",
            Instruction = "在殖民者附近建立一個大約 6x6 的儲存區。",
            Measure = map => new() { [CellsKey] = ZoneCells<Zone_Stockpile>(map, _ => true) },
            Check = (before, after, _) => Delta(before, after, CellsKey) >= 20 ? null : string.Create(CultureInfo.InvariantCulture, $"stockpile cells +{Delta(before, after, CellsKey)}, need +20")
        };

    private static BenchTask RiceFieldTask() =>
        new()
        {
            Id = "rice_field",
            Instruction = "在殖民者附近開一塊至少 30 格的水稻田。",
            Measure = map => new() { [CellsKey] = ZoneCells<Zone_Growing>(map, z => z.GetPlantDefToGrow()?.defName == "Plant_Rice") },
            Check = (before, after, _) => Delta(before, after, CellsKey) >= 25 ? null : string.Create(CultureInfo.InvariantCulture, $"rice zone cells +{Delta(before, after, CellsKey)}, need +25")
        };

    private static BenchTask ResearchBenchTask() =>
        new()
        {
            Id = "research_bench",
            Instruction = "在殖民者附近放一張簡易研究台的藍圖。",
            Measure = map => new() { [BenchesKey] = CountBuildTargets(map, d => d.defName.Contains("ResearchBench", StringComparison.Ordinal)) },
            Check = (before, after, _) => Delta(before, after, BenchesKey) >= 1 ? null : "no new research bench blueprint"
        };

    private static BenchTask WoodRoomTask() =>
        new()
        {
            Id = "wood_room",
            Instruction = "在殖民者附近的空地上，用木頭規劃一間內部 5x5 的小房間藍圖（四面牆加一扇門）。",
            Setup = ClearBlueprints,
            Measure = map => new()
            {
                [WallsKey] = CountBuildTargets(map, d => d == ThingDefOf.Wall),
                [DoorsKey] = CountBuildTargets(map, d => d.IsDoor),
                ["rooms5x5"] = PlannedRooms(map).Count(r => r.IsRectangle(5, 5) && Cells(map, d => d.IsDoor).Any(r.Border.Contains))
            },
            Check = (before, after, _) =>
                Delta(before, after, WallsKey) >= 16 && Delta(before, after, DoorsKey) >= 1 && Delta(before, after, "rooms5x5") >= 1
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"walls +{Delta(before, after, WallsKey)} (need +16), doors +{Delta(before, after, DoorsKey)} (need +1), enclosed 5x5 rooms with a door +{Delta(before, after, "rooms5x5")} (need +1)")
        };

    private static BenchTask ResearchPriorityTask() =>
        new()
        {
            Id = "research_priority",
            Instruction = "讓每一位能做研究的殖民者，把研究工作的優先級設為 1。",
            Measure = map => new() { ["missing"] = ColonistsWithoutResearchPriorityOne(map) },
            Check = (_, after, _) => after["missing"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["missing"]} capable colonist(s) not at research priority 1")
        };

    private static BenchTask CutTreesTask() =>
        new()
        {
            Id = "cut_trees",
            Instruction = "標記砍伐殖民者附近的 5 棵樹。",
            Measure = map => new() { ["marked"] = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.CutPlant).Count() },
            Check = (before, after, _) => Delta(before, after, "marked") >= 5 ? null : string.Create(CultureInfo.InvariantCulture, $"cut designations +{Delta(before, after, "marked")}, need +5")
        };

    private static BenchTask FoodReportTask() =>
        new()
        {
            Id = "food_report",
            Instruction = "告訴我殖民地目前的食物存量大概能撐幾天。可以查詢，但不要改變殖民地的任何東西。",
            Measure = _ => new(),
            Check = (_, _, reply) => BenchmarkScoring.Days(reply).Any() ? null : "reply gives no number of days"
        };

    private static BenchTask TwoRoomHouseTask() =>
        new()
        {
            Id = "two_room_house",
            Tier = Hard,
            Instruction = "在殖民者附近的空地上，用木頭規劃一棟有兩個房間的建築，兩間房共用一面牆、各有一扇門："
                + "一間是放兩張床的臥室，另一間是放一張簡易研究台的研究室。",
            Setup = ClearBlueprints,
            Measure = map => new()
            {
                ["woodWalls"] = CountBuildTargets(map, d => d == ThingDefOf.Wall, ThingDefOf.WoodLog),
                [DoorsKey] = CountBuildTargets(map, d => d.IsDoor),
                ["beds"] = CountBuildTargets(map, IsRealBed),
                ["benches"] = CountBuildTargets(map, d => d.defName.Contains("ResearchBench", StringComparison.Ordinal)),
                ["bedroomNextToLab"] = RoomPlan.BedroomLabPairs(
                    PlannedRooms(map), Cells(map, d => d.IsDoor), Cells(map, IsRealBed),
                    Cells(map, d => d.defName.Contains("ResearchBench", StringComparison.Ordinal))),
                ["spread"] = WallBounds(map) is CellRect r ? Math.Max(r.Width, r.Height) : 0
            },
            Check = (before, after, _) => Fails(
                Need(after, before, "woodWalls", 20), Need(after, before, DoorsKey, 2),
                Need(after, before, "beds", 2), Need(after, before, "benches", 1),
                Delta(before, after, "bedroomNextToLab") >= 1 ? null : "no enclosed bedroom (2 beds) and lab (bench) sharing a wall with a door each",
                after["spread"] <= 20 ? null : string.Create(CultureInfo.InvariantCulture, $"walls spread over {after["spread"]} cells (max 20, not one building)"))
        };

    private static BenchTask FreezerTask() =>
        new()
        {
            Id = "freezer",
            Tier = Hard,
            Instruction = "空調研究已經完成。在殖民者附近用木頭規劃一間內部至少 3x3 的冷凍庫：四面牆、一扇門、"
                + "牆上裝一台冷氣，並在房間裡面劃一個儲存區（區域，不是貨架）。",
            Setup = map => ClearBlueprints(map) ?? FinishResearch("Electricity", "AirConditioning"),
            Measure = map => new()
            {
                ["walls"] = CountBuildTargets(map, d => d == ThingDefOf.Wall),
                [DoorsKey] = CountBuildTargets(map, d => d.IsDoor),
                ["coolersOnWall"] = WallBounds(map) is CellRect r ? CoolersOnWallLine(map, r) : 0,
                ["stockpileInside"] = WallBounds(map) is CellRect room ? StockpileCellsInside(map, room.ContractedBy(1)) : 0
            },
            Check = (before, after, _) => Fails(
                Need(after, before, "walls", 10), Need(after, before, DoorsKey, 1),
                Need(after, before, "coolersOnWall", 1), Need(after, before, "stockpileInside", 4))
        };

    private static BenchTask CornStorageTask() =>
        new()
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
        };

    private static BenchTask KitchenBillsTask() =>
        new()
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
        };

    private static BenchTask InjuredCareTask() =>
        new()
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
        };

    private static BenchTask DailyScheduleTask() =>
        new()
        {
            Id = "daily_schedule",
            Tier = Hard,
            Instruction = "把所有殖民者的作息改成：0 點到 6 點前睡覺，6 點到 22 點前工作，22 點到午夜娛樂。",
            Setup = ResetSchedules,
            Measure = map => new() { ["wrongHours"] = WrongScheduleHours(map) },
            Check = (_, after, _) => after["wrongHours"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["wrongHours"]} colonist-hours differ from the requested schedule")
        };

    private static BenchTask DefendTask() =>
        new()
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
                after["notDrafted"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["notDrafted"]} fighter(s) not drafted"),
                after["notAttacking"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["notAttacking"]} fighter(s) not ordered to attack an enemy"))
        };

    public BenchmarkComponent(Verse.Game game)
    {
    }

    private static bool Enabled => File.Exists(MarkerPath);

    private static string MarkerPath => Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName);

    public override void GameComponentUpdate()
    {
        UpdateRunningRow();
        if (_started || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || !Enabled) return;
        // 等 RimAgent 自己的初始化跑完再送指令。
        if (_readyAt < 0f) _readyAt = UnityEngine.Time.realtimeSinceStartup + 5f;
        if (UnityEngine.Time.realtimeSinceStartup < _readyAt) return;
        _started = true;
        string text = File.ReadAllText(MarkerPath);
        XElement marker = string.IsNullOrWhiteSpace(text) ? null : XDocument.Parse(text).Root;
        if ((bool?)marker?.Attribute("selfcheck") == true)
        {
            SelfCheck();
            return;
        }
        int rounds = Math.Clamp((int?)marker?.Attribute("rounds") ?? DefaultRounds, 1, MaxRounds);
        if ((bool?)marker?.Attribute("survey") == true) _ = CompareSurveyAsync(isolated: true, rounds);
        else if ((bool?)marker?.Attribute("skills") == true) _ = CompareSkillsAsync(isolated: true, rounds);
        else _ = RunAsync(tier: null, isolated: true);
    }

    /// <summary>同一時間只跑一輪：題目共用同一個會話與地圖。</summary>
    private static bool _running;

    /// <summary>這一輪（或上一輪）的進度；視窗關掉後仍可再打開查看。</summary>
    private static List<BenchmarkRow> _rows = [];
    private static ColonyAgentSession _session;
    private static int _seenVersion = -1;

    [DebugAction("RimAgent", "Benchmark: show progress", allowedGameStates = AllowedGameStates.Playing)]
    private static void DebugShowProgress()
    {
        if (_rows.Count == 0)
            Messages.Message("RimAgent.Benchmark.NoRun".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        else
            Window_BenchmarkProgress.Show(_rows);
    }

    /// <summary>這一輪（或上一輪）的思考強度，也是下一輪的預選。</summary>
    internal static ReasoningLevel Reasoning { get; private set; } = ReasoningLevel.Medium;

    /// <summary>「自動」：由決策模型逐題挑選。</summary>
    internal const ReasoningLevel Routing = (ReasoningLevel)(-1);

    internal static string ReasoningLabel(ReasoningLevel level) =>
        (level == Routing ? "RimAgent.Reasoning.Routing" : RimAgentMod.ReasoningKey(level)).Translate();

    private static string ReasoningName => Reasoning == Routing ? nameof(Routing) : Reasoning.ToString();

    /// <summary>這次開遊戲以來各級別（null 表示全部）、各思考強度最近一輪的成績。</summary>
    internal static Dictionary<(string Tier, ReasoningLevel Level), BenchmarkScore> Scores { get; } = [];

    internal static void Start(string tier, ReasoningLevel level)
    {
        if (_running) return;
        Reasoning = level;
        _ = tier switch
        {
            Survey => CompareSurveyAsync(isolated: false, DefaultRounds),
            SkillTier => CompareSkillsAsync(isolated: false, DefaultRounds),
            _ => RunAsync(tier, isolated: false)
        };
    }

    internal const string Survey = "survey";
    private const string InspectTool = "inspect_area";
    private const string ScreenshotTool = "take_screenshot";

    /// <summary>比較模式每邊跑幾輪；每題成對比較，輪數越多信賴區間越窄。</summary>
    private const int DefaultRounds = 3;
    private const int MaxRounds = 10;

    /// <summary>這一輪唯一可用的勘查工具；null 表示不限制。</summary>
    private static string _surveyTool;

    /// <summary>
    /// 同樣的題目輪流只給 inspect_area 或只給 take_screenshot，各跑 <paramref name="rounds"/> 輪，每輪對調先後；
    /// 都藏 get_map_overview 與 find_things，它們也能看地形或列出牆的座標，留著就不必勘查。每輪換地點，同一輪兩個工具用同一處。
    /// </summary>
    private static async Task CompareSurveyAsync(bool isolated, int rounds)
    {
        List<IntVec3> sites = Find.CurrentMap == null ? [] : FindSites(Find.CurrentMap, rounds);
        if (sites.Count < rounds)
        {
            string problem = string.Create(CultureInfo.InvariantCulture, $"SURVEY not started: {sites.Count} open {SiteSize}x{SiteSize} sites, need {rounds}");
            Log.Warning(LogPrefix + problem);
            if (!isolated) Messages.Message(problem, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        (Dictionary<string, BenchmarkScore> scores, Dictionary<string, List<IReadOnlyDictionary<string, bool?>>> outcomes) = await RunArmsAsync(
            Survey, isolated, rounds, [InspectTool, ScreenshotTool], "SURVEY", (round, tool) =>
            {
                _surveyTool = tool;
                _fixedSite = sites[round];
                RimAgentSettings.HiddenTools = new HashSet<string>(StringComparer.Ordinal) { "get_map_overview", "find_things", SubagentTools.ToolName, tool == InspectTool ? ScreenshotTool : InspectTool };
            });
        if (scores.Count == 0) return;
        string result = string.Join(" | ", scores.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Key} {p.Value.Passed}/{p.Value.Total}, {p.Value.PassedByLooking} of them after looking, looked {p.Value.SurveyCalls}x, tools {p.Value.Tools} (failed {p.Value.Failures}), {p.Value.Seconds:0}s, in {p.Value.Input}, out {p.Value.Output}, {PerPass(p.Value)}")))
            + " | " + Difference(ScreenshotTool, InspectTool, outcomes);
        Log.Warning(LogPrefix + "SURVEY DONE: " + result);
        if (!isolated) Messages.Message("RimAgent.Benchmark.SurveyDone".Translate(result), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    internal const string SkillTier = "skill";

    /// <summary>這一輪藏起 read_skill 與技能索引。</summary>
    private static bool _skillsHidden;

    /// <summary>
    /// 技能題輪流開、關技能各跑 <paramref name="rounds"/> 輪，每輪對調先後；指令只描述情況，正確做法只寫在技能裡，
    /// 兩邊的通過率差就是技能的效用。
    /// </summary>
    private static async Task CompareSkillsAsync(bool isolated, int rounds)
    {
        (Dictionary<string, BenchmarkScore> scores, Dictionary<string, List<IReadOnlyDictionary<string, bool?>>> outcomes) = await RunArmsAsync(
            SkillTier, isolated, rounds, [ArmName(false), ArmName(true)], "SKILLS", (_, arm) =>
            {
                _skillsHidden = arm == ArmName(true);
                if (_skillsHidden) RimAgentSettings.HiddenTools = new HashSet<string>(StringComparer.Ordinal) { SkillTools.ToolName };
            });
        if (scores.Count == 0) return;
        string result = string.Join(" | ", scores.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Key} {p.Value.Passed}/{p.Value.Total}, read the expected skill in {p.Value.ExpectedSkillReads}, read_skill {p.Value.SkillReads}x, tools {p.Value.Tools} (failed {p.Value.Failures}), {p.Value.Seconds:0}s, in {p.Value.Input}, out {p.Value.Output}, {PerPass(p.Value)}")))
            + " | " + Difference(ArmName(false), ArmName(true), outcomes);
        Log.Warning(LogPrefix + "SKILLS DONE: " + result);
        if (!isolated) Messages.Message("RimAgent.Benchmark.SkillsDone".Translate(result), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>兩邊輪流各跑 <paramref name="rounds"/> 輪、每輪對調先後；<paramref name="enter"/> 設定這一輪的那一邊，跑完一律還原。</summary>
    private static async Task<(Dictionary<string, BenchmarkScore>, Dictionary<string, List<IReadOnlyDictionary<string, bool?>>>)> RunArmsAsync(
        string tier, bool isolated, int rounds, string[] arms, string label, Action<int, string> enter)
    {
        var scores = new Dictionary<string, BenchmarkScore>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, List<IReadOnlyDictionary<string, bool?>>>(StringComparer.Ordinal);
        foreach ((int round, string arm) in Enumerable.Range(0, rounds).SelectMany(i => (i % 2 == 0 ? arms : arms.Reverse()).Select(a => (i, a))))
        {
            enter(round, arm);
            BenchmarkScore s;
            try
            {
                s = await RunAsync(tier, isolated);
            }
            finally
            {
                _surveyTool = null;
                _fixedSite = null;
                _skillsHidden = false;
                RimAgentSettings.HiddenTools = System.Collections.Frozen.FrozenSet<string>.Empty;
            }
            if (s == null)
            {
                Log.Warning(LogPrefix + label + " stopped early: a " + arm + " round did not finish");
                break;
            }
            scores[arm] = scores.TryGetValue(arm, out BenchmarkScore sum) ? sum.Plus(s) : s;
            AddOutcomes(outcomes, arm, s);
        }
        return (scores, outcomes);
    }

    private static string ArmName(bool skillsHidden) => skillsHidden ? "without skills" : "with skills";

    private static string PerPass(BenchmarkScore score) =>
        score.Passed == 0
            ? "no passes"
            : string.Create(CultureInfo.InvariantCulture, $"per pass: in {score.Input / score.Passed}, out {score.Output / score.Passed}, {score.Seconds / score.Passed:0}s");

    /// <summary>
    /// <paramref name="a"/> 減 <paramref name="b"/> 的通過率差（百分點），以同一輪同一題成對、兩邊都有效的結果做 bootstrap，
    /// 95% 信賴區間含 0 就是平手：題數少時差一兩題可能只是運氣。
    /// </summary>
    private static string Difference(string a, string b, Dictionary<string, List<IReadOnlyDictionary<string, bool?>>> outcomes)
    {
        if (!outcomes.TryGetValue(a, out List<IReadOnlyDictionary<string, bool?>> left) || !outcomes.TryGetValue(b, out List<IReadOnlyDictionary<string, bool?>> right))
            return "difference: one side did not finish";
        List<int> diffs = [.. left.Zip(right).SelectMany(r => r.First
            .Where(p => p.Value.HasValue && r.Second.TryGetValue(p.Key, out bool? other) && other.HasValue)
            .Select(p => (p.Value.Value ? 1 : 0) - (r.Second[p.Key].Value ? 1 : 0)))];
        if (diffs.Count == 0) return "difference: no valid pairs";
        (double mean, double low, double high) = BenchmarkScoring.PairedBootstrap(diffs);
        string verdict = "tied";
        if (low > 0) verdict = a + " better";
        else if (high < 0) verdict = b + " better";
        return string.Create(CultureInfo.InvariantCulture, $"{a} minus {b}: {mean * 100:+0;-0;0} pp (95% CI {low * 100:+0;-0;0} to {high * 100:+0;-0;0}, {diffs.Count} pairs), {verdict}");
    }

    private static void AddOutcomes(Dictionary<string, List<IReadOnlyDictionary<string, bool?>>> outcomes, string arm, BenchmarkScore score)
    {
        if (!outcomes.TryGetValue(arm, out List<IReadOnlyDictionary<string, bool?>> rounds)) outcomes[arm] = rounds = [];
        rounds.Add(score.Outcomes);
    }

    /// <summary>這題成功讀過的技能名稱。</summary>
    private static List<string> SkillsRead(IEnumerable<TranscriptEntry> tools) =>
        [.. tools.Where(t => t.ToolName == SkillTools.ToolName && !t.Failed && !t.Cancelled)
            .Select(t => t.ArgumentValues != null && t.ArgumentValues.TryGetValue("skill", out object name) ? Convert.ToString(name, CultureInfo.InvariantCulture) : t.Arguments)];

    private static List<BenchmarkRow> CreateRows(IEnumerable<BenchTask> tasks) =>
        [.. tasks.Select(t => new BenchmarkRow { Id = t.Id, Tier = t.Tier, Instruction = t.Instruction })];

    /// <summary>截圖檢查用：涵蓋五種狀態。</summary>
    internal static List<BenchmarkRow> SampleRows()
    {
        List<BenchmarkRow> rows = CreateRows(Tasks.Where(t => t.Tier != Survey));
        for (int i = 0; i < 4; i++) Finish(rows[i], i == 2 ? "timed out after 10 min" : null, 8, i, 63f * i);
        Finish(rows[5], "model service error", 2, 0, 90f, invalid: true);
        rows[4].State = BenchmarkState.Running;
        rows[4].StartedAt = UnityEngine.Time.realtimeSinceStartup;
        return rows;
    }

    private static void Finish(BenchmarkRow row, string failure, int tools, int failures, float seconds, bool invalid = false)
    {
        row.State = failure == null ? BenchmarkState.Passed : invalid ? BenchmarkState.Invalid : BenchmarkState.Failed;
        row.Detail = failure;
        row.Tools = tools;
        row.Failures = failures;
        row.Seconds = seconds;
    }

    /// <summary>進行中那一題的工具數即時更新；對話沒變就不重數。</summary>
    private static void UpdateRunningRow()
    {
        BenchmarkRow row = _running ? _rows.Find(x => x.State == BenchmarkState.Running) : null;
        AgentTranscript transcript = row == null ? null : _session?.Transcript;
        if (transcript == null || transcript.Version == _seenVersion) return;
        _seenVersion = transcript.Version;
        List<TranscriptEntry> tools = [.. transcript.Snapshot().Where(e => e.Kind == TranscriptKind.Tool)];
        row.Tools = tools.Count;
        row.Failures = tools.Count(t => t.Failed && !t.Cancelled);
    }

    [DebugAction("RimAgent", "Benchmark: all tasks", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DebugRunAll() => ConfirmAndRun(tier: null);

    [DebugAction("RimAgent", "Benchmark: basic tasks", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DebugRunBasic() => ConfirmAndRun(Basic);

    [DebugAction("RimAgent", "Benchmark: hard tasks", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DebugRunHard() => ConfirmAndRun(Hard);

    [DebugAction("RimAgent", "Benchmark: inspect_area vs take_screenshot", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DebugRunSurvey() => ConfirmAndRun(Survey);

    [DebugAction("RimAgent", "Benchmark: with vs without skills", allowedGameStates = AllowedGameStates.PlayingOnMap)]
    private static void DebugRunSkills() => ConfirmAndRun(SkillTier);

    /// <summary>
    /// 開發模式按鈕手動起跑：題目會清空對話、在地圖上放藍圖與區域、生成敵人，所以先確認並選思考強度；
    /// 跑完還原暫時改動的設定與時間流速（隔離環境用標記檔起跑時不需要還原）。
    /// </summary>
    private static void ConfirmAndRun(string tier)
    {
        if (_running)
        {
            Messages.Message("RimAgent.Benchmark.AlreadyRunning".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Find.WindowStack.Add(new Window_BenchmarkStart(tier));
    }

    /// <param name="tier">只跑這一級的題目；null 表示基本與高難度題（勘查題與技能題只在比較時跑）。</param>
    /// <param name="isolated">由隔離環境的標記檔起跑：讀標記檔的模型與思考強度覆寫，跑完不還原設定。</param>
    /// <returns>沒跑完時為 null。</returns>
    private static async Task<BenchmarkScore> RunAsync(string tier, bool isolated)
    {
        if (_running) return null;
        _running = true;
        RimAgentSettings settings = RimAgentMod.Settings;
        string savedModel = settings?.agentModel;
        ReasoningLevel savedReasoning = settings?.reasoning ?? ReasoningLevel.Auto;
        bool savedRouting = settings?.reasoningRouting ?? false;
        AgentMode savedMode = settings?.mode ?? AgentMode.Confirm;
        bool savedCare = settings?.autonomousCare ?? false;
        bool savedRecord = settings?.recordRuns ?? false;
        bool savedTime = settings?.ToPermissions().Allows(ToolCategory.Time) ?? true;
        TimeSpeed savedSpeed = Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Normal;
        ColonyAgentSession statsSession = null;
        ToolUsageStats savedStats = null;
        try
        {
            RimAgentGameComponent agent = RimAgentGameComponent.Instance;
            if (settings == null || agent == null)
            {
                Log.Warning(LogPrefix + $"FAIL not started: settings={settings != null}, component={agent != null}");
                return null;
            }
            await ApplyBenchmarkSettingsAsync(settings, isolated);
            // 標記檔可能換了模型，所以套用設定後才看。
            if (_surveyTool != null && !agent.Session.ImageInputEnabled)
            {
                Log.Warning(LogPrefix + "FAIL survey: the model does not accept images");
                if (!isolated) Messages.Message("RimAgent.Benchmark.SurveyNeedsImages".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }

            string folder = RunLogWriter.DefaultFolder;
            Directory.CreateDirectory(folder);
            string resultPath = Path.Combine(folder, "Benchmark.jsonl");
            string runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string modVersion = LoadedModManager.GetMod<RimAgentMod>()?.Content?.ModMetaData?.ModVersion ?? string.Empty;
            string model = string.IsNullOrWhiteSpace(settings.agentModel) ? "(framework default)" : settings.agentModel;

            ColonyAgentSession session = agent.Session;
            statsSession = session;
            savedStats = session.ToolUsage;
            string expectedModel = ColonyAgentSession.ResolveModel(settings.agentModel);
            if (expectedModel == null || RimLLMProvider.GetMaxOutputTokens(expectedModel) == null)
                Log.Warning(LogPrefix + "output limit unknown for " + (expectedModel ?? "(no model)") + ": refresh the provider's model list in RimLLM Framework settings, or reasoning may use up the default limit");
            int fallbackTasks = 0;
            int passedWithoutFallback = 0;
            var passedByTier = new Dictionary<string, int> { [Basic] = 0, [Hard] = 0, [Survey] = 0, [SkillTier] = 0 };
            var invalidByTier = new Dictionary<string, int> { [Basic] = 0, [Hard] = 0, [Survey] = 0, [SkillTier] = 0 };
            int totalTools = 0;
            int totalFailures = 0;
            var total = default(ModelUsage);
            BenchTask[] tasks = [.. Tasks.Where(t => tier == null ? t.Tier is Basic or Hard : t.Tier == tier)];
            _session = session;
            _rows = CreateRows(tasks);
            if (!isolated) Window_BenchmarkProgress.Show(_rows);
            // 開跑時就取：中途讀了別的檔，成績仍屬於這張地圖。
            string seed = Find.World?.info?.seedString ?? string.Empty;
            string biome = Find.CurrentMap?.Biome?.defName ?? string.Empty;
            int startTick = Current.Game?.tickManager?.TicksGame ?? 0;
            int configurationErrors = 0;
            int surveyCalls = 0;
            int? unknownCalls = 0;
            int passedByLooking = 0;
            int skillReads = 0;
            int expectedSkillReads = 0;
            var taskOutcomes = new Dictionary<string, bool?>(StringComparer.Ordinal);
            string taskSet = TaskSetHash(tasks);
            string tierName = tier ?? "all";
            string previousTaskSet = PreviousTaskSet(resultPath, tierName);
            for (int i = 0; i < tasks.Length; i++)
            {
                BenchTask task = tasks[i];
                if (configurationErrors >= MaxConfigurationErrors)
                {
                    invalidByTier[task.Tier]++;
                    const string skipped = "skipped: the model provider keeps failing";
                    RecordTaskResult(resultPath, task, skipped, [], 0f, null, default, invalid: true, attempts: 0);
                    Finish(_rows[i], skipped, 0, 0, 0f, invalid: true);
                    continue;
                }
                _rows[i].State = BenchmarkState.Running;
                _rows[i].StartedAt = UnityEngine.Time.realtimeSinceStartup;
                DateTime startedUtc = DateTime.UtcNow;
                TaskOutcome outcome = await RunTaskAsync(session, task);
                ModelUsage usage = ReadUsage(folder, startedUtc, expectedModel);
                int attempts = 1;
                ModelUsage last = usage;
                // 金鑰、餘額、模型不存在這類錯誤等 30 秒也不會好。換了備援模型的成績不屬於受測模型，跟服務出錯一樣重跑。
                if (outcome.ServiceError is { ErrorKind: not AgentErrorKind.Configuration } || usage.FallbackSteps > 0)
                {
                    TaskOutcome first = outcome;
                    attempts++;
                    Log.Warning(LogPrefix + task.Id + " retrying after " + (outcome.ServiceError != null ? "model service error: " + outcome.ServiceError.Text : "falling back to another model"));
                    await Task.Delay(ServiceRetryDelay);
                    startedUtc = DateTime.UtcNow;
                    outcome = await RunTaskAsync(session, task);
                    last = ReadUsage(folder, startedUtc, expectedModel);
                    usage = usage.Plus(last);
                    outcome = outcome with { Tools = [.. first.Tools, .. outcome.Tools], Seconds = first.Seconds + outcome.Seconds };
                }
                (string failure, List<TranscriptEntry> tools, float seconds, string reply, TranscriptEntry serviceError, bool setupFailed) = outcome;
                bool fellBack = last.FallbackSteps > 0;
                if (fellBack) failure = "fell back to another model" + (failure == null ? string.Empty : "; " + failure);
                bool invalid = serviceError != null || setupFailed || fellBack;
                configurationErrors = serviceError?.ErrorKind == AgentErrorKind.Configuration ? configurationErrors + 1 : 0;
                total = total.Plus(usage);
                int failures = RecordTaskResult(resultPath, task, failure, tools, seconds, reply, usage, invalid, attempts);
                Finish(_rows[i], failure, tools.Count, failures, UnityEngine.Time.realtimeSinceStartup - _rows[i].StartedAt, invalid);
                SaveScreenshots(Path.Combine(folder, RunLogWriter.FolderName, "benchmark-shots", runId), task.Id, tools);
                totalTools += tools.Count;
                totalFailures += failures;
                if (!invalid) unknownCalls += usage.UnknownCalls;
                if (usage.FallbackSteps > 0) fallbackTasks++;
                else if (usage.FallbackSteps == 0 && failure == null) passedWithoutFallback++;
                surveyCalls += tools.Count(t => t.ToolName == _surveyTool);
                if (failure == null && tools.Any(t => t.ToolName == _surveyTool && !t.Failed && !t.Cancelled)) passedByLooking++;
                List<string> read = SkillsRead(tools);
                skillReads += read.Count;
                if (task.Skill != null && read.Contains(task.Skill, StringComparer.OrdinalIgnoreCase)) expectedSkillReads++;
                taskOutcomes[task.Id] = invalid ? null : failure == null;
                if (failure == null) passedByTier[task.Tier]++;
                else if (invalid) invalidByTier[task.Tier]++;
            }

            var summary = new Dictionary<string, object>
            {
                ["run"] = runId,
                ["modVersion"] = modVersion,
                ["model"] = model,
                ["reasoning"] = ReasoningName,
                ["surveyTool"] = _surveyTool,
                ["note"] = "summary",
                ["tier"] = tierName,
                // 題目換了就不能和之前的成績比；判分邏輯的改動不在雜湊裡，要看 modVersion。
                ["taskSet"] = taskSet,
                ["sameTaskSetAsPrevious"] = previousTaskSet == null ? null : previousTaskSet == taskSet,
                ["passed"] = passedByTier.Values.Sum(),
                ["total"] = tasks.Length - invalidByTier.Values.Sum(),
                ["invalid"] = invalidByTier.Values.Sum(),
                ["basicPassed"] = passedByTier[Basic],
                ["basicTotal"] = tasks.Count(t => t.Tier == Basic) - invalidByTier[Basic],
                ["hardPassed"] = passedByTier[Hard],
                ["hardTotal"] = tasks.Count(t => t.Tier == Hard) - invalidByTier[Hard],
                ["toolCalls"] = totalTools,
                ["toolFailures"] = totalFailures,
                ["unknownCalls"] = unknownCalls,
                ["surveyCalls"] = surveyCalls,
                ["passedByLooking"] = passedByLooking,
                ["skillsHidden"] = _skillsHidden,
                // 一般題本來就不需要技能，這裡讀得多代表技能描述把模型引錯了方向。
                ["skillReads"] = skillReads,
                ["expectedSkillReads"] = expectedSkillReads,
                ["steps"] = total.Steps,
                ["in"] = total.Input,
                ["out"] = total.Output,
                ["inPerPass"] = passedByTier.Values.Sum() == 0 ? null : total.Input / passedByTier.Values.Sum(),
                ["outPerPass"] = passedByTier.Values.Sum() == 0 ? null : total.Output / passedByTier.Values.Sum(),
                ["secondsPerPass"] = passedByTier.Values.Sum() == 0 ? null : Math.Round(_rows.Sum(r => r.Seconds) / passedByTier.Values.Sum(), 1),
                ["modelsUsed"] = total.Models ?? [],
                ["fallbackTasks"] = fallbackTasks,
                ["passedWithoutFallback"] = passedWithoutFallback,
                // 上限太小時思考會吃光輸出、換成備援模型，換模型數要對照這個看。
                ["maxOutputTokens"] = session.MaxOutputFor(settings.agentModel),
                // 地圖不同，難度就不同：兩輪要比較得先確認這幾項相同。
                ["seed"] = seed,
                ["biome"] = biome,
                ["tick"] = startTick
            };
            Append(resultPath, summary);
            if (previousTaskSet != null && previousTaskSet != taskSet)
                Log.Warning(LogPrefix + "the " + tierName + " task set changed since the last run (" + previousTaskSet + " -> " + taskSet + "): pass rates are not comparable");
            var score = new BenchmarkScore(passedByTier.Values.Sum(), tasks.Length - invalidByTier.Values.Sum(), totalTools, totalFailures, _rows.Sum(r => r.Seconds), total.Input, total.Output, model, surveyCalls, passedByLooking, skillReads, expectedSkillReads) { Outcomes = taskOutcomes };
            // 比較模式的兩輪不是同一組條件，不能當成這個強度的成績。
            if (_surveyTool == null && tier != SkillTier) Scores[(tier, Reasoning)] = score;
            Log.Warning(LogPrefix + "DONE: " + JsonSerializer.Serialize(summary, RunLogWriter.Json));
            if (!isolated)
            {
                Messages.Message("RimAgent.Benchmark.Done".Translate(passedByTier.Values.Sum(), tasks.Length - invalidByTier.Values.Sum(), resultPath), MessageTypeDefOf.NeutralEvent, historical: false);
            }
            return score;
        }
        catch (Exception ex)
        {
            Log.Warning(LogPrefix + "FAIL exception: " + ex);
            return null;
        }
        finally
        {
            _session = null;
            statsSession?.ToolUsage = savedStats;
            foreach (BenchmarkRow row in _rows.Where(x => x.State == BenchmarkState.Running))
            {
                Finish(row, "aborted", row.Tools, row.Failures, UnityEngine.Time.realtimeSinceStartup - row.StartedAt);
            }
            if (!isolated && settings != null)
            {
                settings.agentModel = savedModel;
                settings.reasoning = savedReasoning;
                settings.reasoningRouting = savedRouting;
                settings.mode = savedMode;
                settings.autonomousCare = savedCare;
                settings.recordRuns = savedRecord;
                settings.allowByCategory[(int)ToolCategory.Time] = savedTime;
                if (Find.TickManager != null) Find.TickManager.CurTimeSpeed = savedSpeed;
            }
            _running = false;
        }
    }

    /// <summary>
    /// 只改記憶體裡的設定：完整權限讓行動直接執行，關掉自主照顧免得插隊；隔離環境另套用標記檔的模型覆寫。
    /// 遊戲暫停讓地圖狀態不受時間影響，工具照常可用。
    /// </summary>
    private static async Task ApplyBenchmarkSettingsAsync(RimAgentSettings settings, bool isolated)
    {
        if (isolated)
        {
            string text = await File.ReadAllTextAsync(MarkerPath);
            XElement marker = string.IsNullOrWhiteSpace(text) ? null : XDocument.Parse(text).Root;
            string modelOverride = (string)marker?.Attribute("model");
            if (!string.IsNullOrEmpty(modelOverride)) settings.agentModel = modelOverride;
            string level = (string)marker?.Attribute("reasoning");
            if (!string.IsNullOrEmpty(level)) Reasoning = level.Equals(nameof(Routing), StringComparison.OrdinalIgnoreCase) ? Routing : Enum.Parse<ReasoningLevel>(level, ignoreCase: true);
        }
        settings.reasoningRouting = Reasoning == Routing;
        // 自動的備援若是「預設」會退回這個強度，不能用玩家殘留的設定。
        settings.reasoning = settings.reasoningRouting ? ReasoningLevel.Medium : Reasoning;
        settings.mode = AgentMode.Autonomous;
        settings.autonomousCare = false;
        settings.recordRuns = true;
        // 模型能解除暫停的話，地圖會跟著時間變，還會以一倍速等上好幾分鐘。
        settings.allowByCategory[(int)ToolCategory.Time] = false;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
    }

    private readonly record struct ModelUsage(long Steps, long Input, long Output, string Reasoning, string[] Models, int? FallbackSteps, string[] StepModels, int? UnknownCalls)
    {
        public ModelUsage Plus(ModelUsage other) => new(Steps + other.Steps, Input + other.Input, Output + other.Output, other.Reasoning ?? Reasoning,
            [.. (Models ?? []).Union(other.Models ?? [], StringComparer.Ordinal)], FallbackSteps + other.FallbackSteps, [.. StepModels ?? [], .. other.StepModels ?? []], UnknownCalls + other.UnknownCalls);
    }

    /// <summary>取這題的運行紀錄摘要；摘要還沒寫出（例如寫檔失敗）時為 0。</summary>
    private static ModelUsage ReadUsage(string folder, DateTime startedUtc, string expectedModel)
    {
        try
        {
            using JsonDocument line = JsonDocument.Parse(File.ReadLines(Path.Combine(folder, "runs", "index.jsonl")).Last());
            JsonElement e = line.RootElement;
            if (e.GetProperty("ts").GetDateTime() < startedUtc) return default;
            (List<string> stepModels, int? unknownCalls) = ReadSteps(Path.Combine(folder, "runs", e.GetProperty("file").GetString()));
            int? fallbackSteps = expectedModel == null || stepModels.Count == 0
                ? null
                : stepModels.Count(m => !string.Equals(m, expectedModel, StringComparison.OrdinalIgnoreCase));
            return new ModelUsage(e.GetProperty("steps").GetInt64(), e.GetProperty("in").GetInt64(), e.GetProperty("out").GetInt64(),
                    e.TryGetProperty("reasoning", out JsonElement reasoning) ? reasoning.GetString() : null,
                    e.TryGetProperty("models", out JsonElement models) ? [.. models.EnumerateArray().Select(m => m.GetString())] : null,
                    fallbackSteps, [.. stepModels], unknownCalls);
        }
        catch (Exception ex)
        {
            Log.Warning(LogPrefix + "usage not read: " + ex.Message);
            return default;
        }
    }

    /// <summary>主代理每一步的模型，以及呼叫了沒有的工具幾次（SDK 直接回錯，不會有工具項目）；子代理可以另設模型，不算。讀不到時為空，不影響索引裡的用量。</summary>
    private static (List<string> Models, int? UnknownCalls) ReadSteps(string path)
    {
        List<string> models = [];
        int unknown = 0;
        // 最後一步的呼叫可能是被取消或中斷，有下一步才確定都處理過了。
        int requested = 0;
        HashSet<int> executed = [];
        int executedBefore = 0;
        try
        {
            foreach (string text in File.ReadLines(path))
            {
                using JsonDocument step = JsonDocument.Parse(text);
                JsonElement s = step.RootElement;
                if (s.TryGetProperty("agent", out _)) continue;
                string kind = s.GetProperty("t").GetString();
                if (kind == "tool") executed.Add(s.GetProperty("call").GetInt32());
                if (kind != "model") continue;
                unknown += Math.Max(0, requested - (executed.Count - executedBefore));
                executedBefore = executed.Count;
                requested = s.TryGetProperty("calls", out JsonElement calls) ? calls.GetArrayLength() : 0;
                if (s.TryGetProperty("model", out JsonElement m) && m.GetString() is { } name) models.Add(name);
            }
            return (models, unknown);
        }
        catch (Exception ex)
        {
            Log.Warning(LogPrefix + "step models not read: " + ex.Message);
            return ([], null);
        }
    }

    private static IEnumerable<Thing> PlannedBuildings(Map map) =>
        map.listerThings.AllThings.Where(t => t is Blueprint or Frame && t.Faction == Faction.OfPlayer);

    /// <summary>把單題結果寫進結果檔與日誌；回傳這題失敗的工具呼叫數（取消不算，呼叫了沒有的工具另記在 unknownCalls）。</summary>
    private static int RecordTaskResult(string resultPath, BenchTask task, string failure, List<TranscriptEntry> tools, float seconds, string reply, ModelUsage usage, bool invalid, int attempts)
    {
        int failures = tools.Count(t => t.Failed && !t.Cancelled);
        Append(resultPath, new Dictionary<string, object>
        {
            ["task"] = task.Id,
            ["tier"] = task.Tier,
            ["reasoning"] = ReasoningName,
            ["surveyTool"] = _surveyTool,
            ["skillsHidden"] = _skillsHidden,
            ["expectedSkill"] = task.Skill,
            ["skillsRead"] = SkillsRead(tools),
            ["reasoningUsed"] = usage.Reasoning,
            ["modelsUsed"] = usage.Models ?? [],
            ["stepModels"] = usage.StepModels ?? [],
            ["fallbackSteps"] = usage.FallbackSteps,
            ["ok"] = failure == null,
            ["invalid"] = invalid,
            ["attempts"] = attempts,
            ["detail"] = failure ?? string.Empty,
            ["tools"] = tools.Count,
            ["toolFailures"] = failures,
            ["unknownCalls"] = usage.UnknownCalls,
            ["steps"] = usage.Steps,
            ["in"] = usage.Input,
            ["out"] = usage.Output,
            ["toolNames"] = string.Join(',', tools.Select(t => t.ToolName)),
            ["seconds"] = Math.Round(seconds, 1, MidpointRounding.ToEven),
            ["reply"] = Short(reply)
        });
        Log.Message(LogPrefix + string.Create(CultureInfo.InvariantCulture, $"{task.Tier}/{task.Id}: {(failure == null ? "PASS" : (invalid ? "INVALID " : "FAIL ") + failure)} tools={tools.Count} failed={failures} {seconds:0}s"));
        return failures;
    }

    /// <summary>隔離環境的截圖會跟測試存檔一起清掉；留一份在運行紀錄裡，備份紀錄時才會一起帶走，才能核對是圖錯還是模型看錯。</summary>
    private static void SaveScreenshots(string folder, string taskId, IEnumerable<TranscriptEntry> tools)
    {
        if (_surveyTool != ScreenshotTool) return;
        int index = 0;
        foreach (TranscriptEntry tool in tools.Where(t => t.ToolName == ScreenshotTool && !t.Failed))
        {
            index++;
            try
            {
                string path = Regex.Match(tool.Result, @"Screenshot saved: (.+\.png) \(", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value;
                if (path.Length == 0) continue;
                if (!Directory.Exists(folder)) PruneScreenshots(Path.GetDirectoryName(folder));
                Directory.CreateDirectory(folder);
                File.Copy(path, Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{taskId}-{index}.png")), overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(LogPrefix + "screenshot not kept: " + ex.Message);
            }
        }
    }

    private const int MaxScreenshotRuns = 20;

    /// <summary>資料夾以開跑時間命名，字典序就是時間序。</summary>
    private static void PruneScreenshots(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string old in Directory.GetDirectories(root).OrderDescending(StringComparer.Ordinal).Skip(MaxScreenshotRuns - 1)) Directory.Delete(old, recursive: true);
    }

    private const int MaxConfigurationErrors = 2;
    private static readonly TimeSpan ServiceRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>題目代號、級別、該讀的技能與佈置前的指令；佈置時才補上的座標與名字不算。</summary>
    private static string TaskSetHash(IEnumerable<BenchTask> tasks)
    {
        string text = string.Join('\n', tasks.Select(t => t.Id + "|" + t.Tier + "|" + t.Skill + "|" + t.Instruction));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    /// <summary>同一級別上一輪總結的題組雜湊；沒有或讀不到時為 null。</summary>
    private static string PreviousTaskSet(string resultPath, string tier)
    {
        try
        {
            if (!File.Exists(resultPath)) return null;
            foreach (string line in File.ReadLines(resultPath).Reverse())
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement e = doc.RootElement;
                if (e.TryGetProperty("note", out JsonElement note) && note.GetString() == "summary"
                    && e.TryGetProperty("tier", out JsonElement t) && t.GetString() == tier)
                    return e.TryGetProperty("taskSet", out JsonElement hash) ? hash.GetString() : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warning(LogPrefix + "previous task set not read: " + ex.Message);
        }
        return null;
    }

    /// <param name="SetupFailed">這張地圖佈置不出場景，沒送指令，不算進通過率。</param>
    /// <param name="ServiceError">模型服務出錯（逾時、額度、設定）以致沒做完；不是模型不會做，不算進通過率。</param>
    private sealed record TaskOutcome(string Failure, List<TranscriptEntry> Tools, float Seconds, string Reply, TranscriptEntry ServiceError, bool SetupFailed = false);

    /// <summary>只看讓指令結束的錯誤（沒有回覆）；途中的錯誤（例如壓縮失敗）之後仍跑完的不算。</summary>
    private static TranscriptEntry FindServiceError(string reply, IEnumerable<TranscriptEntry> entries) =>
        reply == null && entries.LastOrDefault(e => e.Kind == TranscriptKind.Error) is
            { ErrorKind: AgentErrorKind.Configuration or AgentErrorKind.Transient or AgentErrorKind.RateLimited } error ? error : null;

    private static async Task<TaskOutcome> RunTaskAsync(ColonyAgentSession session, BenchTask task)
    {
        Map map = Current.Game?.CurrentMap;
        if (map == null || RimAgentGameComponent.Instance?.Session != session)
        {
            return new TaskOutcome("the game was closed or reloaded", [], 0f, null, null);
        }
        string failure = null;
        bool setupFailed = false;
        string reply = null;
        TranscriptEntry serviceError = null;
        float seconds = 0f;
        List<TranscriptEntry> tools = [];
        BenchmarkSnapshot snapshot = null;
        try
        {
            session.Clear();
            // 統計會跨題累積，後面的題目會多出預載的工具包與經驗提示，各題就不能比。
            session.ToolUsage = new ToolUsageStats(null);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            snapshot = new BenchmarkSnapshot(map);
            string setupError = task.Setup?.Invoke(map);
            if (setupError != null)
            {
                setupFailed = true;
                failure = "setup: " + setupError;
            }
            else
            {
                Dictionary<string, int> before = task.Measure(map);
                int transcriptStart = session.Transcript.Snapshot().Count;
                float startedAt = UnityEngine.Time.realtimeSinceStartup;
                using var timeout = new CancellationTokenSource(TaskTimeout);
                try
                {
                    reply = await session.SendAsync(task.Instruction, cancellationToken: timeout.Token);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogPrefix + task.Id + " exception: " + ex.GetType().Name + ": " + ex.Message);
                }
                seconds = UnityEngine.Time.realtimeSinceStartup - startedAt;
                failure = task.Check(before, task.Measure(Find.CurrentMap), reply);
                if (timeout.IsCancellationRequested)
                    failure = Fails(string.Create(CultureInfo.InvariantCulture, $"timed out after {TaskTimeout.TotalMinutes:0} min"), failure);
                List<TranscriptEntry> added = [.. session.Transcript.Snapshot().Skip(transcriptStart)];
                tools = [.. added.Where(e => e.Kind == TranscriptKind.Tool)];
                if (failure != null && !timeout.IsCancellationRequested) serviceError = FindServiceError(reply, added);
                if (serviceError != null) failure = "model service error: " + serviceError.Text + "; " + failure;
                else if (failure != null && !timeout.IsCancellationRequested && reply == null && added.LastOrDefault(e => e.Kind == TranscriptKind.Error) is { } error)
                    failure = "ended by error: " + error.Text + "; " + failure;
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
            TryCleanUp(task.Id, () => task.Cleanup?.Invoke(map));
            TryCleanUp(task.Id, () => snapshot?.Restore());
        }
        return new TaskOutcome(failure, tools, seconds, reply, serviceError, setupFailed);
    }

    private static void TryCleanUp(string taskId, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warning(LogPrefix + taskId + " cleanup: " + ex);
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

    private static List<(int X, int Z)> Cells(Map map, Func<ThingDef, bool> predicate)
    {
        return [.. BuildTargets(map, predicate).Select(t => (t.Position.x, t.Position.z))];
    }

    private static List<RoomPlan.Room> PlannedRooms(Map map)
    {
        List<(int X, int Z)> barriers = Cells(map, d => d.IsDoor || d.Fillage == FillCategory.Full);
        if (WallBounds(map) is CellRect bounds)
        {
            // 山壁也能當房間的牆。
            barriers.AddRange(bounds.ExpandedBy(1).Cells
                .Where(c => c.InBounds(map) && c.Filled(map))
                .Select(c => (c.x, c.z)));
        }
        return RoomPlan.Enclosed(barriers);
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
        return def.IsBed && def.building != null && def.building.bed_humanlike && !def.defName.Contains("Spot", StringComparison.Ordinal);
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
                TimeAssignmentDef expected;
                if (hour < 6) expected = TimeAssignmentDefOf.Sleep;
                else if (hour < 22) expected = TimeAssignmentDefOf.Work;
                else expected = TimeAssignmentDefOf.Joy;
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
        return Jobs(pawn).Any(j => j.targetA.Thing is Pawn target && _enemies.Contains(target));
    }

    // ---- 場景準備與還原 ----

    private static string ClearBlueprints(Map map)
    {
        foreach (Thing blueprint in PlannedBuildings(map).ToList())
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
            if (!project.IsFinished) Find.ResearchManager.FinishProject(project, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
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
        foreach (IntVec3 cell in GenRadial.RadialCellsAround(start, 25f, useCenter: false))
        {
            CellRect rect = GenAdj.OccupiedRect(cell, Rot4.North, def.size).ExpandedBy(1);
            if (!rect.InBounds(map)) continue;
            if (!rect.Cells.All(c => c.Standable(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null
                && c.GetFirstPawn(map) == null && map.zoneManager.ZoneAt(c) == null))
            {
                continue;
            }
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
        return delta >= min ? null : string.Create(CultureInfo.InvariantCulture, $"{key} +{delta} (need +{min})");
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
        File.AppendAllText(path, JsonSerializer.Serialize(line, RunLogWriter.Json) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Short(string text)
    {
        string flat = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " / ", StringComparison.Ordinal);
        return flat.Length > MaxReplyChars ? flat[..MaxReplyChars] + "…" : flat;
    }
}
