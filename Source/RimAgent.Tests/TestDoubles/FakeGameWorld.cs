using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

/// <summary>
/// 記憶體內的假遊戲世界。命令會真的改狀態（後續查詢看得到），並把每次呼叫記到 Calls 供斷言。
/// </summary>
internal sealed partial class FakeGameWorld : IGameWorld
{
    public List<ColonistInfo> Colonists { get; } = [];
    public List<ThingInfo> Things { get; } = [];
    public List<ThingInfo> Selection { get; } = [];
    public List<ThreatInfo> Threats { get; } = [];
    /// <summary>世界地圖上的玩家商隊。</summary>
    public List<CaravanInfo> Caravans { get; } = [];
    public WorldSurveyInfo Survey { get; set; } = new WorldSurveyInfo { OriginTile = 100, OriginLabel = "New Arrivals" };
    /// <summary>格子編號 → 行程估算；沒登記的格子當作走得到、一天、十格。</summary>
    public Dictionary<int, TravelEstimateInfo> TravelEstimates { get; } = [];
    /// <summary>格子編號 → 地形；沒登記的格子給一般平地。</summary>
    public Dictionary<int, WorldTileInfo> WorldTiles { get; } = [];
    /// <summary>任何商隊對任何格子得到的命令清單（測試要的情境各自擺）。</summary>
    public List<FloatMenuOptionInfo> CaravanOrders { get; } = [];
    public string CaravanOrdersError { get; set; } = string.Empty;
    public List<TraderInfo> Traders { get; } = [];
    public ColonyOverview Overview { get; set; } = new ColonyOverview { ColonyName = "New Arrivals", ColonistCount = 2, Year = 5500, Season = "Spring", DayOfSeason = 3, Hour = 14, Weather = "Clear", OutdoorTemperatureC = 21.4f, Wealth = 12000f };
    public ResourceSnapshot Resources { get; set; } = new ResourceSnapshot();
    public List<AlertInfo> Alerts { get; } = [];
    public List<LetterInfo> RecentLetters { get; } = [];
    public List<PendingChoiceInfo> PendingChoices { get; } = [];
    /// <summary>物件 id → 指令按鈕；沒登記但存在的物件視為沒有按鈕。</summary>
    public Dictionary<int, List<GizmoInfo>> Gizmos { get; } = [];
    public List<QuestInfo> Quests { get; } = [];
    /// <summary>目標（MapTarget.ToString()，例如「[305]」「cell (4,5)」）→ 遊戲拒絕該目標的原因。</summary>
    public Dictionary<string, string> RejectedTargets { get; } = [];
    /// <summary>任何殖民者對任何目標按右鍵得到的選單（測試要的情境各自擺）。</summary>
    public List<FloatMenuOptionInfo> FloatMenuOptions { get; } = [];
    /// <summary>非空時右鍵選單做不出來（例如殖民者倒地）。</summary>
    public string FloatMenuError { get; set; } = string.Empty;
    public string FloatMenuResult { get; set; } = string.Empty;
    /// <summary>下一次 ChooseOption 或按下 action 按鈕成功後接著出現的對話框（模擬多段對話與確認框）。</summary>
    public PendingChoiceInfo FollowUpDialog { get; set; }
    public PowerInfo Power { get; set; } = new PowerInfo();
    public ResearchInfo Research { get; set; } = new ResearchInfo();
    public MapBounds Bounds { get; set; } = new MapBounds { Width = 100, Height = 100 };
    public List<string> Calls { get; } = [];

    /// <summary>藍圖放不下的格子與原因。</summary>
    public Dictionary<(int X, int Z), string> BlockedCells { get; } = [];
    /// <summary>某建築不能用某材料（defName, stuffDefName）→ 失敗原因。</summary>
    public Dictionary<(string Def, string Stuff), string> BlockedStuff { get; } = [];
    public HashSet<(int X, int Z)> UnwalkableCells { get; } = [];
    /// <summary>只撐得住輕型建築的格子（例如沼澤）；其餘格子都能放重型建築。</summary>
    public HashSet<(int X, int Z)> LightOnlyCells { get; } = [];
    public List<(string Def, int X, int Z, int Rotation, string Stuff)> Blueprints { get; } = [];

    public IReadOnlyList<DefInfo> WorkTypes { get; set; } =
    [
        new DefInfo("Firefighter", "firefight"), new DefInfo("Patient", "patient"), new DefInfo("Doctor", "doctor"),
        new DefInfo("Cooking", "cook"), new DefInfo("Hunting", "hunt"), new DefInfo("Construction", "construct"),
        new DefInfo("Growing", "grow"), new DefInfo("Mining", "mine"), new DefInfo("PlantCutting", "plant cut"),
        new DefInfo("Hauling", "haul"), new DefInfo("Cleaning", "clean"), new DefInfo("Research", "research")
    ];

    public IReadOnlyList<DefInfo> Buildables { get; set; } =
    [
        new DefInfo("Wall", "wall"), new DefInfo("Door", "door"), new DefInfo("Bed", "bed"),
        new DefInfo("SolarGenerator", "solar generator"), new DefInfo("WindTurbine", "wind turbine"), new DefInfo("Sandbags", "sandbags")
    ];

    public IReadOnlyList<DefInfo> Stuffs { get; set; } =
    [
        new DefInfo("WoodLog", "wood"), new DefInfo("Steel", "steel"), new DefInfo("BlocksGranite", "granite blocks")
    ];

    public IReadOnlyList<DefInfo> TimeAssignments { get; set; } =
    [
        new DefInfo("Anything", "anything"), new DefInfo("Work", "work"), new DefInfo("Joy", "recreation"),
        new DefInfo("Sleep", "sleep"), new DefInfo("Meditate", "meditate")
    ];

    public IReadOnlyList<DefInfo> TradeGoods { get; set; } =
    [
        new DefInfo("Steel", "steel"), new DefInfo("Silver", "silver"),
        new DefInfo("ComponentSpacer", "spacer component"), new DefInfo("MedicineUltratech", "ultratech medicine"),
        new DefInfo("HerbalMedicine", "herbal medicine"), new DefInfo("Corn", "corn")
    ];

    /// <summary>假結算的單件價格（正式端用遊戲公式）。買入賣出同價；缺省為 1。</summary>
    public Dictionary<string, float> Prices { get; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DefInfo> MedicalCareLevels { get; set; } =
    [
        new DefInfo("NoCare", "no care"), new DefInfo("NoMeds", "doctor care only, no medicine"),
        new DefInfo("HerbalOrWorse", "herbal medicine or worse"), new DefInfo("NormalOrWorse", "industrial medicine or worse"),
        new DefInfo("Best", "best")
    ];

    public IReadOnlyList<DefInfo> SurgeryRecipes { get; set; } =
    [
        new DefInfo("RemoveBodyPart", "remove body part"),
        new DefInfo("InstallArtificialBodyPart", "install artificial body part"),
        new DefInfo("AdministerAnesthetic", "administer anesthetic")
    ];

    /// <summary>研究還沒完成的手術 defName（正式端用遊戲研究狀態判斷）。</summary>
    public HashSet<string> LockedSurgeries { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public List<PrisonerInfo> Prisoners { get; } = [];

    public IReadOnlyList<DefInfo> PrisonerInteractionModes { get; set; } =
    [
        new DefInfo("MaintainOnly", "maintain only"), new DefInfo("AttemptRecruit", "recruit"),
        new DefInfo("ReduceResistance", "reduce resistance"), new DefInfo("Release", "release"),
        new DefInfo("Execution", "execution")
    ];

    public List<AnimalInfo> Animals { get; } = [];

    public IReadOnlyList<DefInfo> AnimalTraining { get; set; } =
    [
        new DefInfo("Obedience", "obedience"), new DefInfo("Release", "release"),
        new DefInfo("Rescue", "rescue"), new DefInfo("Haul", "haul")
    ];

    public IReadOnlyList<DefInfo> AnimalAreas { get; set; } =
    [
        new DefInfo("Barn", "Barn"), new DefInfo("Pasture", "Pasture")
    ];

    /// <summary>已標記馴養的動物 id（正式端讀 designation）。</summary>
    public HashSet<int> TameMarked { get; } = [];
    /// <summary>開啟中的訓練，key 為 pawnId:defName（正式端讀 training tracker）。</summary>
    public HashSet<string> TrainingOn { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public List<ZoneInfo> Zones { get; } = [];

    public List<MapInfo> Maps { get; } =
    [
        new() { Id = 1, Label = "New Arrivals", Tile = 12345, ColonistCount = 2, IsCurrent = true }
    ];

    /// <summary>目前操作的地圖 id（正式端為 Find.CurrentMap）。</summary>
    public int CurrentMapId { get; set; } = 1;

    /// <summary>目前時間流速 defName（正式端讀 TickManager）。</summary>
    public string CurrentTimeSpeed { get; set; } = "Normal";

    public IReadOnlyList<DefInfo> TimeSpeeds { get; set; } = TimeSpeedDirectory.Default;

    public TimeSpeedInfo GetTimeSpeed()
    {
        return new TimeSpeedInfo { Current = CurrentTimeSpeed, Available = TimeSpeeds };
    }

    public CommandResult SetTimeSpeed(string speedDefName)
    {
        Calls.Add($"SetTimeSpeed({speedDefName})");
        DefInfo match = TimeSpeeds.FirstOrDefault(d => string.Equals(d.DefName, speedDefName, StringComparison.OrdinalIgnoreCase));
        if (match == null) return CommandResult.Fail($"unknown time speed {speedDefName}");
        CurrentTimeSpeed = match.DefName;
        return CommandResult.Ok($"Time speed set to {match.DefName}.");
    }

    /// <summary>已完成的研究 defName（正式端用遊戲研究狀態判斷）。</summary>
    public HashSet<string> FinishedResearch { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DefInfo> FinishedResearchProjects => [.. FinishedResearch.Select(d => new DefInfo(d, d))];
    public IReadOnlyList<DefInfo> StorageFilterables { get; set; } =
    [
        new DefInfo("Steel", "steel"), new DefInfo("WoodLog", "wood"), new DefInfo("Corn", "corn"),
        new DefInfo("Rice", "rice"), new DefInfo("MedicineUltratech", "ultratech medicine"),
        new DefInfo("ComponentSpacer", "spacer component")
    ];

    public IReadOnlyList<DefInfo> GrowablePlants { get; set; } =
    [
        new DefInfo("Plant_Rice", "水稻"), new DefInfo("Plant_Potato", "土豆"), new DefInfo("Plant_Corn", "玉米")
    ];

    /// <summary>各儲藏區明確禁收的 defName（正式端讀 ThingFilter）。</summary>
    public Dictionary<int, HashSet<string>> ZoneDisallowed { get; } = [];

    public List<WorkTableInfo> WorkTables { get; } = [];

    /// <summary>各工作台的帳單（正式端讀 BillStack）。</summary>
    public Dictionary<int, List<BillInfo>> TableBills { get; } = [];

    /// <summary>沒在 <see cref="TableRecipes"/> 指定的工作台都能做的預設配方。</summary>
    public IReadOnlyList<DefInfo> BillRecipes { get; set; } =
    [
        new DefInfo("MakeComponent", "component"), new DefInfo("MakeShortBow", "short bow"),
        new DefInfo("ButcherCreature", "butcher creature")
    ];

    /// <summary>個別工作台自己的配方（正式端讀 def.AllRecipes）。</summary>
    public Dictionary<int, IReadOnlyList<DefInfo>> TableRecipes { get; } = [];

    public IReadOnlyList<DefInfo> GetTableRecipes(int tableId)
    {
        if (WorkTables.TrueForAll(t => t.Id != tableId)) return [];
        return TableRecipes.TryGetValue(tableId, out IReadOnlyList<DefInfo> recipes) ? recipes : BillRecipes;
    }

    public static ColonistInfo MakeColonist(int id, string first, string nick, string last, int x = 10, int z = 10)
    {
        var workTypes = new[] { "Firefighter", "Patient", "Doctor", "Cooking", "Hunting", "Construction", "Growing", "Mining", "PlantCutting", "Hauling", "Cleaning", "Research" };
        return new ColonistInfo
        {
            Id = id,
            Name = string.IsNullOrEmpty(nick) ? first : nick,
            Nickname = nick ?? string.Empty,
            FullName = string.IsNullOrEmpty(nick) ? $"{first} {last}" : $"{first} '{nick}' {last}",
            Age = 30,
            X = x,
            Z = z,
            HealthPercent = 1f,
            MoodPercent = 0.6f,
            MinorBreakThreshold = 0.35f,
            CurrentActivity = "Idle",
            Skills =
            [
                new SkillInfo { DefName = "Shooting", Label = "shooting", Level = 8, Passion = 2 },
                new SkillInfo { DefName = "Medicine", Label = "medical", Level = 5, Passion = 1 },
                new SkillInfo { DefName = "Plants", Label = "plants", Level = 0, Disabled = true }
            ],
            WorkPriorities = [.. workTypes.Select(w => new WorkPriorityInfo { WorkTypeDefName = w, Label = w.ToLowerInvariant(), Priority = 3 })],
            Timetable = [.. Enumerable.Repeat("Anything", 24)],
            MedicalCare = "NoMeds"
        };
    }

    public static ThingInfo MakeThing(int id, string defName, string label, ThingKind kind, int x, int z, bool playerOwned = false, bool harvestable = false)
    {
        return new ThingInfo { Id = id, DefName = defName, Label = label, Kind = kind, X = x, Z = z, PlayerOwned = playerOwned, Harvestable = harvestable, Designations = new List<string>() };
    }
}
