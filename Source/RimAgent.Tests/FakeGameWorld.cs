using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

/// <summary>
/// 記憶體內的假遊戲世界。命令會真的改狀態（後續查詢看得到），並把每次呼叫記到 Calls 供斷言。
/// </summary>
internal sealed class FakeGameWorld : IGameWorld
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
    public HashSet<(int X, int Z)> UnwalkableCells { get; } = [];
    public List<(string Def, int X, int Z, int Rotation, string Stuff)> Blueprints { get; } = [];

    public IReadOnlyList<DefInfo> WorkTypes { get; set; } = new[]
    {
        new DefInfo("Firefighter", "firefight"), new DefInfo("Patient", "patient"), new DefInfo("Doctor", "doctor"),
        new DefInfo("Cooking", "cook"), new DefInfo("Hunting", "hunt"), new DefInfo("Construction", "construct"),
        new DefInfo("Growing", "grow"), new DefInfo("Mining", "mine"), new DefInfo("PlantCutting", "plant cut"),
        new DefInfo("Hauling", "haul"), new DefInfo("Cleaning", "clean"), new DefInfo("Research", "research")
    };

    public IReadOnlyList<DefInfo> Buildables { get; set; } = new[]
    {
        new DefInfo("Wall", "wall"), new DefInfo("Door", "door"), new DefInfo("Bed", "bed"),
        new DefInfo("SolarGenerator", "solar generator"), new DefInfo("WindTurbine", "wind turbine"), new DefInfo("Sandbags", "sandbags")
    };

    public IReadOnlyList<DefInfo> Stuffs { get; set; } = new[]
    {
        new DefInfo("WoodLog", "wood"), new DefInfo("Steel", "steel"), new DefInfo("BlocksGranite", "granite blocks")
    };

    public IReadOnlyList<DefInfo> TimeAssignments { get; set; } = new[]
    {
        new DefInfo("Anything", "anything"), new DefInfo("Work", "work"), new DefInfo("Joy", "recreation"),
        new DefInfo("Sleep", "sleep"), new DefInfo("Meditate", "meditate")
    };

    public IReadOnlyList<DefInfo> TradeGoods { get; set; } = new[]
    {
        new DefInfo("Steel", "steel"), new DefInfo("Silver", "silver"),
        new DefInfo("ComponentSpacer", "spacer component"), new DefInfo("MedicineUltratech", "ultratech medicine"),
        new DefInfo("HerbalMedicine", "herbal medicine"), new DefInfo("Corn", "corn")
    };

    /// <summary>假結算的單件價格（正式端用遊戲公式）。買入賣出同價；缺省為 1。</summary>
    public Dictionary<string, float> Prices { get; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DefInfo> MedicalCareLevels { get; set; } = new[]
    {
        new DefInfo("NoCare", "no care"), new DefInfo("NoMeds", "doctor care only, no medicine"),
        new DefInfo("HerbalOrWorse", "herbal medicine or worse"), new DefInfo("NormalOrWorse", "industrial medicine or worse"),
        new DefInfo("Best", "best")
    };

    public IReadOnlyList<DefInfo> SurgeryRecipes { get; set; } = new[]
    {
        new DefInfo("RemoveBodyPart", "remove body part"),
        new DefInfo("InstallArtificialBodyPart", "install artificial body part"),
        new DefInfo("AdministerAnesthetic", "administer anesthetic")
    };

    /// <summary>研究還沒完成的手術 defName（正式端用遊戲研究狀態判斷）。</summary>
    public HashSet<string> LockedSurgeries { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public List<PrisonerInfo> Prisoners { get; } = [];

    public IReadOnlyList<DefInfo> PrisonerInteractionModes { get; set; } = new[]
    {
        new DefInfo("MaintainOnly", "maintain only"), new DefInfo("AttemptRecruit", "recruit"),
        new DefInfo("ReduceResistance", "reduce resistance"), new DefInfo("Release", "release"),
        new DefInfo("Execution", "execution")
    };

    public List<AnimalInfo> Animals { get; } = [];

    public IReadOnlyList<DefInfo> AnimalTraining { get; set; } = new[]
    {
        new DefInfo("Obedience", "obedience"), new DefInfo("Release", "release"),
        new DefInfo("Rescue", "rescue"), new DefInfo("Haul", "haul")
    };

    public IReadOnlyList<DefInfo> AnimalAreas { get; set; } = new[]
    {
        new DefInfo("Barn", "Barn"), new DefInfo("Pasture", "Pasture")
    };

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

    public ScreenshotInfo ScreenshotResult { get; set; } = new ScreenshotInfo
    {
        Success = true,
        FilePath = "C:/RimWorld/Screenshots/screenshot.png",
        Width = 1920,
        Height = 1080
    };

    public SaveContext SaveContext { get; set; } = new SaveContext { ColonyId = "colony1", Tick = 300000 };
    /// <summary>存檔資料夾：SaveGame 直接加進來（真實遊戲下一幀才寫檔，測試不必等）。</summary>
    public List<SaveSlotInfo> SaveFiles { get; } = [];
    public string LoadedSave { get; private set; }

    public SaveContext GetSaveContext() => SaveContext;

    public Task<IReadOnlyList<SaveSlotInfo>> GetSaveFilesAsync() => Task.FromResult<IReadOnlyList<SaveSlotInfo>>(SaveFiles.ToList());

    public CommandResult SaveGame(string saveName)
    {
        Calls.Add($"SaveGame({saveName})");
        SaveFiles.Add(new SaveSlotInfo { Name = saveName, Checkpoint = true, ColonyId = SaveContext.ColonyId, Tick = SaveContext.Tick, WrittenUtcTicks = SaveFiles.Count + 1 });
        return CommandResult.Ok();
    }

    public CommandResult DeleteCheckpoint(string saveName)
    {
        Calls.Add($"DeleteCheckpoint({saveName})");
        SaveFiles.RemoveAll(s => s.Checkpoint && s.Name == saveName);
        return CommandResult.Ok();
    }

    public CommandResult LoadGame(string saveName)
    {
        Calls.Add($"LoadGame({saveName})");
        LoadedSave = saveName;
        return CommandResult.Ok();
    }

    public ScreenshotInfo TakeScreenshot(string filename = null)
    {
        Calls.Add($"TakeScreenshot({filename ?? "null"})");
        if (ScreenshotResult != null && !ScreenshotResult.Success)
        {
            return ScreenshotResult;
        }

        string name = string.IsNullOrWhiteSpace(filename) ? "screenshot" : filename;
        return new ScreenshotInfo
        {
            Success = ScreenshotResult?.Success ?? true,
            FilePath = $"C:/RimWorld/Screenshots/{name}.png",
            Width = ScreenshotResult?.Width ?? 1920,
            Height = ScreenshotResult?.Height ?? 1080,
            Error = ScreenshotResult?.Error ?? string.Empty
        };
    }

    /// <summary>已完成的研究 defName（正式端用遊戲研究狀態判斷）。</summary>
    public HashSet<string> FinishedResearch { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DefInfo> FinishedResearchProjects => FinishedResearch.Select(d => new DefInfo(d, d)).ToList();
    public IReadOnlyList<DefInfo> StorageFilterables { get; set; } = new[]
    {
        new DefInfo("Steel", "steel"), new DefInfo("WoodLog", "wood"), new DefInfo("Corn", "corn"),
        new DefInfo("Rice", "rice"), new DefInfo("MedicineUltratech", "ultratech medicine"),
        new DefInfo("ComponentSpacer", "spacer component")
    };

    public IReadOnlyList<DefInfo> GrowablePlants { get; set; } = new[]
    {
        new DefInfo("Plant_Rice", "水稻"), new DefInfo("Plant_Potato", "土豆"), new DefInfo("Plant_Corn", "玉米")
    };

    /// <summary>各儲藏區明確禁收的 defName（正式端讀 ThingFilter）。</summary>
    public Dictionary<int, HashSet<string>> ZoneDisallowed { get; } = [];

    public List<WorkTableInfo> WorkTables { get; } = [];

    /// <summary>各工作台的帳單（正式端讀 BillStack）。</summary>
    public Dictionary<int, List<BillInfo>> TableBills { get; } = [];

    /// <summary>沒在 <see cref="TableRecipes"/> 指定的工作台都能做的預設配方。</summary>
    public IReadOnlyList<DefInfo> BillRecipes { get; set; } = new[]
    {
        new DefInfo("MakeComponent", "component"), new DefInfo("MakeShortBow", "short bow"),
        new DefInfo("ButcherCreature", "butcher creature")
    };

    /// <summary>個別工作台自己的配方（正式端讀 def.AllRecipes）。</summary>
    public Dictionary<int, IReadOnlyList<DefInfo>> TableRecipes { get; } = [];

    public IReadOnlyList<DefInfo> GetTableRecipes(int tableId)
    {
        if (WorkTables.All(t => t.Id != tableId)) return Array.Empty<DefInfo>();
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
            Skills = new[]
            {
                new SkillInfo { DefName = "Shooting", Label = "shooting", Level = 8, Passion = 2 },
                new SkillInfo { DefName = "Medicine", Label = "medical", Level = 5, Passion = 1 },
                new SkillInfo { DefName = "Plants", Label = "plants", Level = 0, Disabled = true }
            },
            WorkPriorities = workTypes.Select(w => new WorkPriorityInfo { WorkTypeDefName = w, Label = w.ToLowerInvariant(), Priority = 3 }).ToList(),
            Timetable = Enumerable.Repeat("Anything", 24).ToList(),
            MedicalCare = "NoMeds"
        };
    }

    public static ThingInfo MakeThing(int id, string defName, string label, ThingKind kind, int x, int z, bool playerOwned = false, bool harvestable = false)
    {
        return new ThingInfo { Id = id, DefName = defName, Label = label, Kind = kind, X = x, Z = z, PlayerOwned = playerOwned, Harvestable = harvestable, Designations = new List<string>() };
    }

    public ColonyOverview GetOverview() => Overview;
    public int GetHostileCount() => Overview.HostileCount;
    public List<int> MedicalEmergencies { get; set; } = [];
    public IReadOnlyList<int> GetColonistsInMedicalEmergency() => MedicalEmergencies.ToList();
    public int FireCount { get; set; }
    public int GetFireCount() => FireCount;
    public IReadOnlyList<ColonistInfo> GetColonists()
    {
        ColonistQueryCount++;
        // 真實世界每次查詢都回新快照；開啟後讓復原流程的「重新讀取」行為也能被測試。
        return SnapshotColonists ? Colonists.Select(CloneColonist).ToList() : Colonists;
    }

    /// <summary>測試用：讓 GetColonists 回傳複本，模擬真實 IGameWorld 的新鮮快照。</summary>
    public bool SnapshotColonists { get; set; }

    private static ColonistInfo CloneColonist(ColonistInfo c)
    {
        return new ColonistInfo
        {
            Id = c.Id,
            Name = c.Name,
            Nickname = c.Nickname,
            FullName = c.FullName,
            Age = c.Age,
            X = c.X,
            Z = c.Z,
            HealthPercent = c.HealthPercent,
            Downed = c.Downed,
            MoodPercent = c.MoodPercent,
            MinorBreakThreshold = c.MinorBreakThreshold,
            InMentalState = c.InMentalState,
            MentalStateLabel = c.MentalStateLabel,
            CurrentActivity = c.CurrentActivity,
            Drafted = c.Drafted,
            MedicalCare = c.MedicalCare,
            Equipment = c.Equipment,
            Needs = c.Needs,
            Thoughts = c.Thoughts,
            Skills = c.Skills,
            WorkPriorities = c.WorkPriorities?.Select(w => new WorkPriorityInfo
            {
                WorkTypeDefName = w.WorkTypeDefName,
                Label = w.Label,
                Priority = w.Priority,
                Disabled = w.Disabled
            }).ToList() ?? [],
            Timetable = c.Timetable?.ToList() ?? [],
            UnreadableParts = c.UnreadableParts
        };
    }

    /// <summary>本局 GetColonists 被呼叫次數：驗證同一句內重複查詢只碰一次世界。</summary>
    public int ColonistQueryCount { get; private set; }
    public IReadOnlyList<PrisonerInfo> GetPrisoners() => Prisoners;
    public IReadOnlyList<AnimalInfo> GetAnimals() => Animals;
    public IReadOnlyList<ZoneInfo> GetZones() => Zones;
    public IReadOnlyList<WorkTableInfo> GetWorkTables() => WorkTables;
    public IReadOnlyList<BillInfo> GetBills(int tableId)
    {
        return TableBills.TryGetValue(tableId, out List<BillInfo> bills) ? bills : (IReadOnlyList<BillInfo>)[];
    }
    public List<ColonistHealthInfo> Health { get; } = [];
    public IReadOnlyList<ColonistHealthInfo> GetHealth() => Health;
    public ResourceSnapshot GetResources() => Resources;
    public IReadOnlyList<AlertInfo> GetAlerts() => Alerts;
    public IReadOnlyList<LetterInfo> GetRecentLetters() => RecentLetters;
    public IReadOnlyList<PendingChoiceInfo> GetPendingChoices() => PendingChoices;
    public PowerInfo GetPower() => Power;
    public IReadOnlyList<ThreatInfo> GetThreats() => Threats;
    public IReadOnlyList<TraderInfo> GetTraders() => Traders;
    public ResearchInfo GetResearch() => Research;

    public IReadOnlyList<DefInfo> ResearchProjects
    {
        get
        {
            var projects = new List<DefInfo>();
            if (Research.Current != null) projects.Add(new DefInfo(Research.Current.DefName, Research.Current.Label));
            foreach (ResearchProjectInfo p in Research.Available)
            {
                if (projects.Any(d => string.Equals(d.DefName, p.DefName, StringComparison.OrdinalIgnoreCase))) continue;
                projects.Add(new DefInfo(p.DefName, p.Label));
            }
            return projects;
        }
    }

    public CommandResult SetResearch(string researchDefName)
    {
        Calls.Add($"SetResearch({researchDefName})");
        string want = (researchDefName ?? string.Empty).Trim();
        if (want.Length == 0 || string.Equals(want, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(want, "stop", StringComparison.OrdinalIgnoreCase))
        {
            if (Research.Current == null) return CommandResult.Ok("No research is running.");
            string stopped = Research.Current.Label;
            Research = new ResearchInfo { Available = Research.Available };
            return CommandResult.Ok($"Stopped research {stopped}.");
        }
        if (Research.Current != null && string.Equals(Research.Current.DefName, want, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Ok($"Research {Research.Current.Label} is already running.");
        ResearchProjectInfo project = Research.Available.FirstOrDefault(p => string.Equals(p.DefName, want, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            if (FinishedResearch.Contains(want)) return CommandResult.Fail($"{want} is already finished");
            return CommandResult.Fail($"unknown research {want}");
        }
        Research = new ResearchInfo { Current = project, Available = Research.Available };
        return CommandResult.Ok($"Research switched to {project.Label}.");
    }

    public IReadOnlyList<MapInfo> GetMaps()
    {
        return Maps.Select(m => new MapInfo
        {
            Id = m.Id,
            Label = m.Label,
            Tile = m.Tile,
            ColonistCount = m.ColonistCount,
            IsCurrent = m.Id == CurrentMapId
        }).ToList();
    }

    public CommandResult SetCurrentMap(int mapId)
    {
        Calls.Add($"SetCurrentMap({mapId})");
        MapInfo map = Maps.FirstOrDefault(m => m.Id == mapId);
        if (map == null) return CommandResult.Fail($"no map has id {mapId}");
        if (mapId == CurrentMapId) return CommandResult.Ok($"{map.Label} is already the current map.");
        CurrentMapId = mapId;
        return CommandResult.Ok($"Switched to {map.Label} [{mapId}]. Later tools now operate on this map.");
    }
    public IReadOnlyList<ThingInfo> GetSelection() => Selection;
    public MapBounds GetMapBounds() => Bounds;

    /// <summary>指定格子的地面狀態；沒登記的格子為一般土壤、可走、室外，格上物件取自 Things。</summary>
    public Dictionary<(int X, int Z), CellInfo> Cells { get; } = [];

    /// <summary>沒登記的格子共用的室外房間。</summary>
    public RoomInfo Outdoors { get; set; } = new RoomInfo { Id = 0, Role = "none", CellCount = 10000, Outdoors = true, TemperatureC = 21.4f, Extents = new CellRectSpec(0, 0, 99, 99) };

    public CellInfo GetCell(int x, int z)
    {
        if (Cells.TryGetValue((x, z), out CellInfo cell)) return cell;
        return new CellInfo
        {
            X = x, Z = z, TerrainDefName = "Soil", Terrain = "soil", Fertility = 1f,
            Walkable = !UnwalkableCells.Contains((x, z)), Room = Outdoors,
            Things = Things.Where(t => t.X == x && t.Z == z).ToList()
        };
    }

    public IReadOnlyList<CellInfo> GetCells(CellRectSpec rect)
    {
        var result = new List<CellInfo>();
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
        {
            for (int x = rect.MinX; x <= rect.MaxX; x++) result.Add(GetCell(x, z));
        }
        return result;
    }

    /// <summary>字元網格的格子；沒登記的格子為室外裸地（預設的 MapGridCell）。</summary>
    public Dictionary<(int X, int Z), MapGridCell> GridCells { get; } = [];

    /// <summary>每次 GetMapGrid 收到的矩形（驗證工具層有裁切、有擋超大矩形）。</summary>
    public List<CellRectSpec> GridRequests { get; } = [];

    public IReadOnlyList<MapGridCell> GetMapGrid(CellRectSpec rect)
    {
        GridRequests.Add(rect);
        var result = new List<MapGridCell>();
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
        {
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                result.Add(GridCells.TryGetValue((x, z), out MapGridCell cell) ? cell : new MapGridCell());
            }
        }
        return result;
    }

    /// <summary>非 null 時所有工具都會被擋下（模擬讀檔中／回到主選單）。</summary>
    public string NotReadyReason { get; set; }

    public string GetNotReadyReason() => NotReadyReason;

    /// <summary>假世界的遊戲時間；WaitTicksAsync 立即推進它。</summary>
    public int TicksGame { get; set; }
    public bool Paused { get; set; }
    /// <summary>時間推到這個 tick 時觸發一次（測試用來模擬等待中途來襲擊或被暫停）。</summary>
    public int? EventAtTick { get; set; }
    public Action OnEvent { get; set; }

    public Task<CommandResult> WaitTicksAsync(int ticks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"WaitTicks({ticks})");
        if (Paused) return Task.FromResult(CommandResult.Fail("the game is paused"));
        int target = TicksGame + ticks;
        if (EventAtTick.HasValue && EventAtTick.Value > TicksGame && EventAtTick.Value <= target)
        {
            TicksGame = EventAtTick.Value;
            EventAtTick = null;
            OnEvent?.Invoke();
            if (Paused) return Task.FromResult(CommandResult.Fail("the game is paused"));
        }
        TicksGame = target;
        return Task.FromResult(CommandResult.Ok(string.Empty));
    }

    /// <summary>下一次 TakeNewGameErrors 會取走的錯誤（測試塞進來模擬遊戲噴錯）。</summary>
    public List<string> PendingGameErrors { get; } = [];

    public IReadOnlyList<string> TakeNewGameErrors()
    {
        var taken = PendingGameErrors.ToList();
        PendingGameErrors.Clear();
        return taken;
    }

    public ThingSearchResult FindThings(ThingQuery query)
    {
        IEnumerable<ThingInfo> result = Things;
        if (query.Kind != ThingKind.Any) result = result.Where(t => t.Kind == query.Kind);
        if (!string.IsNullOrEmpty(query.DefName)) result = result.Where(t => string.Equals(t.DefName, query.DefName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(query.LabelContains))
        {
            result = result.Where(t => t.Label.IndexOf(query.LabelContains, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       t.DefName.IndexOf(query.LabelContains, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        if (query.Area.HasValue) result = result.Where(t => query.Area.Value.Contains(t.X, t.Z));
        // 與真實世界層同一規則：放行的在前，同組依距離殖民者平均位置（沒有殖民者則地圖中心）由近到遠。
        List<ThingInfo> matches = [.. result];
        double cx = Bounds.Width / 2.0, cz = Bounds.Height / 2.0;
        if (Colonists.Count > 0)
        {
            cx = Colonists.Average(c => (double)c.X);
            cz = Colonists.Average(c => (double)c.Z);
        }
        List<ThingInfo> shown = [.. matches
            .OrderBy(t => t.Forbidden)
            .ThenBy(t => (t.X - cx) * (t.X - cx) + (t.Z - cz) * (t.Z - cz))
            .Take(query.Limit)];
        return new ThingSearchResult(shown, matches.Count);
    }

    public bool UseWorkPriorities { get; set; }

    public void SetUseWorkPriorities(bool value)
    {
        Calls.Add($"SetUseWorkPriorities({value})");
        UseWorkPriorities = value;
    }

    public CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority)
    {
        Calls.Add($"SetWorkPriority({pawnId},{workTypeDefName},{priority})");
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        WorkPriorityInfo work = pawn?.WorkPriorities.FirstOrDefault(w => w.WorkTypeDefName == workTypeDefName);
        if (work == null) return CommandResult.Fail("pawn or work type not found");
        work.Priority = priority;
        return CommandResult.Ok();
    }

    public CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName)
    {
        Calls.Add($"SetTimeAssignment({pawnId},{hour},{assignmentDefName})");
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("pawn not found");
        var table = pawn.Timetable.ToList();
        table[hour] = assignmentDefName;
        pawn.Timetable = table;
        return CommandResult.Ok();
    }

    public CommandResult AddDesignationOnCell(int x, int z, string designationDefName)
    {
        Calls.Add($"AddDesignationOnCell({x},{z},{designationDefName})");
        ThingInfo thing = Things.FirstOrDefault(t => t.X == x && t.Z == z && t.Kind == ThingKind.Mineable);
        if (thing == null) return CommandResult.Fail("nothing mineable there");
        Mark(thing, designationDefName);
        return CommandResult.Ok();
    }

    public CommandResult AddDesignationOnThing(int thingId, string designationDefName)
    {
        Calls.Add($"AddDesignationOnThing({thingId},{designationDefName})");
        ThingInfo thing = Things.Concat(Selection).FirstOrDefault(t => t.Id == thingId);
        if (thing == null) return CommandResult.Fail("thing not found");
        Mark(thing, designationDefName);
        return CommandResult.Ok();
    }

    public CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName)
    {
        Calls.Add($"RemoveDesignationOnCell({x},{z},{designationDefName})");
        ThingInfo thing = Things.FirstOrDefault(t => t.X == x && t.Z == z && t.Designations.Contains(designationDefName));
        if (thing == null) return CommandResult.Fail("no such designation there");
        Unmark(thing, designationDefName);
        return CommandResult.Ok();
    }

    public CommandResult RemoveDesignationOnThing(int thingId, string designationDefName)
    {
        Calls.Add($"RemoveDesignationOnThing({thingId},{designationDefName})");
        ThingInfo thing = Things.Concat(Selection).FirstOrDefault(t => t.Id == thingId);
        if (thing == null || !thing.Designations.Contains(designationDefName)) return CommandResult.Fail("no such designation");
        Unmark(thing, designationDefName);
        return CommandResult.Ok();
    }

    /// <summary>各建築的規格（以 defName 為鍵）；沒登記的視為不存在，GetBuildingInfo 回傳 null。</summary>
    public Dictionary<string, BuildingInfo> BuildingInfos { get; } = new Dictionary<string, BuildingInfo>(StringComparer.OrdinalIgnoreCase);

    public BuildingInfo GetBuildingInfo(string buildableDefName)
    {
        return BuildingInfos.TryGetValue(buildableDefName ?? string.Empty, out BuildingInfo info) ? info : null;
    }

    public CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName)
    {
        Calls.Add($"TryPlaceBlueprint({buildableDefName},{x},{z},{rotation},{stuffDefName})");
        if (BlockedCells.TryGetValue((x, z), out string reason)) return CommandResult.Fail(reason);
        Blueprints.Add((buildableDefName, x, z, rotation, stuffDefName));
        return CommandResult.Ok();
    }

    public List<string> RemovableBlueprints { get; } = new List<string>();
    public CellRectSpec? LastRemovedArea { get; private set; }

    public IReadOnlyList<string> RemoveBlueprints(CellRectSpec rect)
    {
        Calls.Add($"RemoveBlueprints({rect})");
        LastRemovedArea = rect;
        return RemovableBlueprints;
    }

    public List<ShipPartInfo> ShipParts { get; } = new List<ShipPartInfo>();

    public IReadOnlyList<ShipPartInfo> GetShipParts() => ShipParts;

    public List<ShipPartRequirement> RequiredShipParts { get; } = new List<ShipPartRequirement>();

    public IReadOnlyList<ShipPartRequirement> GetRequiredShipParts() => RequiredShipParts;

    public CommandResult SetDrafted(int pawnId, bool drafted)
    {
        Calls.Add($"SetDrafted({pawnId},{drafted})");
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("pawn not found");
        pawn.Drafted = drafted;
        return CommandResult.Ok();
    }

    public CommandResult OrderMoveTo(int pawnId, int x, int z)
    {
        Calls.Add($"OrderMoveTo({pawnId},{x},{z})");
        if (UnwalkableCells.Contains((x, z))) return CommandResult.Fail("cell is not walkable");
        return Colonists.Any(c => c.Id == pawnId) ? CommandResult.Ok() : CommandResult.Fail("pawn not found");
    }

    public CommandResult ChooseOption(string choiceId, int option)
    {
        Calls.Add($"ChooseOption({choiceId},{option})");
        PendingChoiceInfo choice = PendingChoices.FirstOrDefault(c => c.Id == choiceId);
        ChoiceOptionInfo picked = choice?.Options.FirstOrDefault(o => o.Number == option);
        if (picked == null) return CommandResult.Fail("choice not found");
        PendingChoices.Remove(choice);
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
        }
        return CommandResult.Ok(picked.Text);
    }

    public IReadOnlyList<QuestInfo> GetQuests() => Quests;

    public CommandResult AcceptQuest(int questId, int rewardNumber, int accepterPawnId)
    {
        Calls.Add($"AcceptQuest({questId},{rewardNumber},{accepterPawnId})");
        QuestInfo quest = Quests.FirstOrDefault(q => q.Id == questId);
        if (quest == null) return CommandResult.Fail("quest not found");
        quest.State = QuestInfo.ActiveState;
        quest.ExpiresInTicks = null;
        quest.AccepterName = Colonists.FirstOrDefault(c => c.Id == accepterPawnId)?.Name ?? string.Empty;
        return CommandResult.Ok(quest.Name);
    }

    public CommandResult SetQuestDismissed(int questId, bool dismissed)
    {
        Calls.Add($"SetQuestDismissed({questId},{dismissed})");
        QuestInfo quest = Quests.FirstOrDefault(q => q.Id == questId);
        if (quest == null) return CommandResult.Fail("quest not found");
        quest.Dismissed = dismissed;
        return CommandResult.Ok();
    }

    public IReadOnlyList<GizmoInfo> GetGizmos(int thingId)
    {
        if (Gizmos.TryGetValue(thingId, out List<GizmoInfo> gizmos)) return gizmos;
        return Things.Any(t => t.Id == thingId) ? new List<GizmoInfo>() : null;
    }

    public CommandResult UseGizmo(int thingId, int gizmoNumber, string label, MapTarget target)
    {
        Calls.Add($"UseGizmo({thingId},{gizmoNumber},{label}" + (target == null ? ")" : $",{target})"));
        GizmoInfo gizmo = GetGizmos(thingId)?.FirstOrDefault(g => g.Number == gizmoNumber);
        if (gizmo != null && !string.Equals(gizmo.Label, label, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail("button list changed");
        if (gizmo == null) return CommandResult.Fail("no such button");
        if (gizmo.Kind == GizmoInfo.TargetKind || target != null)
        {
            if (target == null) return CommandResult.Fail("asks for a target");
            if (RejectedTargets.TryGetValue(target.ToString(), out string reason)) return CommandResult.Fail(reason);
            return CommandResult.Ok("targeted " + target);
        }
        if (gizmo.Kind == GizmoInfo.ToggleKind)
        {
            gizmo.On = !gizmo.On;
            return CommandResult.Ok(gizmo.On ? "now on" : "now off");
        }
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
        }
        return CommandResult.Ok();
    }

    public FloatMenuInfo GetFloatMenuOptions(int colonistId, MapTarget target)
    {
        ColonistInfo colonist = Colonists.FirstOrDefault(c => c.Id == colonistId);
        if (colonist == null) return new FloatMenuInfo { Error = "colonist not found" };
        if (!string.IsNullOrEmpty(FloatMenuError)) return new FloatMenuInfo { Error = FloatMenuError, PawnName = colonist.Name };
        return new FloatMenuInfo { PawnName = colonist.Name, Drafted = colonist.Drafted, TargetLabel = target.ToString(), Options = FloatMenuOptions };
    }

    public CommandResult UseFloatMenuOption(int colonistId, MapTarget target, int optionNumber, string label)
    {
        Calls.Add($"UseFloatMenuOption({colonistId},{target},{optionNumber},{label})");
        FloatMenuOptionInfo option = FloatMenuOptions.FirstOrDefault(o => o.Number == optionNumber);
        if (option == null || !string.Equals(option.Label, label, StringComparison.OrdinalIgnoreCase)) return CommandResult.Fail("menu changed");
        if (option.Disabled) return CommandResult.Fail("disabled");
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
        }
        return CommandResult.Ok(FloatMenuResult);
    }

    public CommandResult OrderJob(int pawnId, string job, int targetId)
    {
        Calls.Add($"OrderJob({pawnId},{job},{targetId})");
        if (Colonists.All(c => c.Id != pawnId)) return CommandResult.Fail("pawn not found");
        bool targetExists = Threats.Any(t => t.Id == targetId) || Things.Any(t => t.Id == targetId) || Colonists.Any(c => c.Id == targetId);
        return targetExists ? CommandResult.Ok($"{job} [{targetId}]") : CommandResult.Fail("target not found on the map");
    }

    public CommandResult SetForbidden(CellRectSpec? area, string labelContains, bool forbidden)
    {
        Calls.Add($"SetForbidden({area?.ToString() ?? "map"},{labelContains},{forbidden})");
        List<ThingInfo> changed = [.. Things
            .Where(t => (t.Kind == ThingKind.Item) && t.Forbidden != forbidden)
            .Where(t => !area.HasValue || area.Value.Contains(t.X, t.Z))
            .Where(t => string.IsNullOrEmpty(labelContains) || t.Label.IndexOf(labelContains, StringComparison.OrdinalIgnoreCase) >= 0)];
        if (changed.Count == 0) return CommandResult.Fail("no visible item or corpse there");
        foreach (ThingInfo t in changed) t.Forbidden = forbidden;
        return CommandResult.Ok($"{changed.Count} item(s): {string.Join(", ", changed.Select(t => t.Label))}");
    }

    public CommandResult OrderAttack(int pawnId, int targetId)
    {
        Calls.Add($"OrderAttack({pawnId},{targetId})");
        bool targetExists = Threats.Any(t => t.Id == targetId) || Things.Any(t => t.Id == targetId);
        return targetExists ? CommandResult.Ok() : CommandResult.Fail("target not found");
    }

    public CommandResult SetMedicalCare(int pawnId, string careDefName)
    {
        Calls.Add($"SetMedicalCare({pawnId},{careDefName})");
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("pawn not found");
        if (!MedicalCareLevels.Any(d => string.Equals(d.DefName, careDefName, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"unknown medical care level {careDefName}");
        pawn.MedicalCare = careDefName;
        return CommandResult.Ok();
    }

    /// <summary>殖民地能裝車的物品（正式端讀倉儲與家園區）。</summary>
    public List<CaravanCargoInfo> CaravanCargo { get; } =
    [
        new() { DefName = "Silver", Label = "silver", Count = 800, MassEach = 0.008f },
        new() { DefName = "Pemmican", Label = "pemmican", Count = 200, MassEach = 0.05f },
        new() { DefName = "MedicineIndustrial", Label = "medicine", Count = 12, MassEach = 0.5f },
        new() { DefName = "Bedroll", Label = "bedroll", Count = 3, MassEach = 7f }
    ];

    /// <summary>規劃查詢收到的請求（查詢不記進 <see cref="Calls"/>，那裡只放會動世界的命令）。</summary>
    public List<string> PlanCalls { get; } = [];

    /// <summary>還在集結的商隊。</summary>
    public List<FormingCaravanInfo> Forming { get; } = [];

    /// <summary>設定後 <see cref="PlanCaravan"/> 與 <see cref="FormCaravan"/> 直接用它（測試想要特定數字或拒絕原因時）。</summary>
    public CaravanPlanInfo PlanOverride { get; set; }

    private int _nextFormingId = 70;

    /// <summary>簡化的組隊規劃：每位殖民者承重 40 公斤、動物 120 公斤，其餘數字固定；拒絕原因照原版的幾個檢查。</summary>
    private CaravanPlanInfo BuildPlan(CaravanRequest request)
    {
        if (PlanOverride != null) return PlanOverride;
        var members = new List<CaravanMemberInfo>();
        foreach (int id in (request.PawnIds ?? Array.Empty<int>()).Distinct())
        {
            ColonistInfo colonist = Colonists.FirstOrDefault(c => c.Id == id);
            AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == id);
            PrisonerInfo prisoner = Prisoners.FirstOrDefault(p => p.Id == id);
            if (colonist != null)
            {
                if (colonist.Drafted) return new CaravanPlanInfo { Error = $"{colonist.Name} is drafted; undraft them first" };
                members.Add(new CaravanMemberInfo { Id = id, Name = colonist.Name, Kind = CaravanMemberInfo.ColonistKind, Downed = colonist.Downed });
            }
            else if (animal != null && !animal.Wild)
                members.Add(new CaravanMemberInfo { Id = id, Name = animal.Name, Kind = CaravanMemberInfo.AnimalKind, Downed = animal.Downed });
            else if (prisoner != null)
                members.Add(new CaravanMemberInfo { Id = id, Name = prisoner.Name, Kind = CaravanMemberInfo.PrisonerKind, Downed = prisoner.Downed });
            else
                return new CaravanPlanInfo { Error = $"no pawn with id {id} is on this map" };
        }

        var cargo = new List<CaravanPlanCargo>();
        foreach (TradeLine line in request.Cargo ?? Array.Empty<TradeLine>())
        {
            CaravanCargoInfo have = CaravanCargo.FirstOrDefault(c => string.Equals(c.DefName, line.DefName, StringComparison.OrdinalIgnoreCase));
            if (have == null || have.Count < line.Count)
                return new CaravanPlanInfo { Error = $"the colony can only supply {have?.Count ?? 0} {line.DefName} for the caravan (asked for {line.Count})" };
            cargo.Add(new CaravanPlanCargo { DefName = have.DefName, Label = have.Label, Count = line.Count, MassEach = have.MassEach });
        }

        float usage = cargo.Sum(c => c.MassEach * c.Count);
        float capacity = members.Count(m => m.Kind != CaravanMemberInfo.AnimalKind) * 40f + members.Count(m => m.Kind == CaravanMemberInfo.AnimalKind) * 120f;
        bool pemmican = cargo.Any(c => c.DefName == "Pemmican");
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (request.DestinationTile < 0) blockers.Add("You must choose a route first.");
        if (!members.Any(m => m.Kind == CaravanMemberInfo.ColonistKind && !m.Downed)) blockers.Add("A caravan needs at least one colonist who is not downed.");
        if (usage > capacity) blockers.Add("Too big caravan mass usage.");
        float foodDays = pemmican ? cargo.First(c => c.DefName == "Pemmican").Count / 4f : 0f;
        if (foodDays < 5f) warnings.Add(foodDays < 0.1f ? "The caravan has no food." : $"The caravan only has {foodDays:0.#} days' worth of food.");
        return new CaravanPlanInfo
        {
            Members = members,
            Cargo = cargo,
            MassUsage = usage,
            MassCapacity = capacity,
            TilesPerDay = 15f,
            TicksPerMove = 4000,
            FoodDays = foodDays,
            DaysUntilRot = pemmican ? 90f : 600f,
            ForagedFoodLabel = "berries",
            ForagedPerDay = members.Count > 1 ? 1.5f : 0f,
            Visibility = 0.4f,
            DestinationTile = request.DestinationTile,
            DestinationLabel = request.DestinationTile < 0 ? string.Empty : "tile " + request.DestinationTile + " (Temperate forest)",
            DistanceTiles = request.DestinationTile < 0 ? 0f : 12f,
            EtaTicks = request.DestinationTile < 0 ? 0 : 120000,
            DestinationReachable = request.DestinationTile >= 0,
            Blockers = blockers,
            Warnings = warnings
        };
    }

    public IReadOnlyList<CaravanCargoInfo> GetCaravanCargo() => CaravanCargo;

    public CaravanPlanInfo PlanCaravan(CaravanRequest request)
    {
        PlanCalls.Add(Describe(request));
        return request == null ? new CaravanPlanInfo { Error = "no caravan requested" } : BuildPlan(request);
    }

    public CommandResult FormCaravan(CaravanRequest request)
    {
        Calls.Add($"FormCaravan({Describe(request)})");
        if (request == null) return CommandResult.Fail("no caravan requested");
        CaravanPlanInfo plan = BuildPlan(request);
        if (!string.IsNullOrEmpty(plan.Error)) return CommandResult.Fail(plan.Error);
        if (plan.Blockers.Count > 0) return CommandResult.Fail(string.Join("; ", plan.Blockers.ToArray()));
        Forming.Add(new FormingCaravanInfo
        {
            Id = _nextFormingId++,
            Status = "gathering items",
            Members = plan.Members,
            Cargo = plan.Cargo.Select(c => c.Label + " x" + c.Count).ToList(),
            ExitX = 0,
            ExitZ = 30
        });
        return CommandResult.Ok($"Started forming a caravan with {string.Join(", ", plan.Members.Select(m => m.Name).ToArray())} for tile {request.DestinationTile}");
    }

    private static string Describe(CaravanRequest request)
    {
        if (request == null) return "null";
        return $"{string.Join("+", (request.PawnIds ?? Array.Empty<int>()).ToArray())}|" +
               $"{string.Join("+", (request.Cargo ?? Array.Empty<TradeLine>()).Select(l => l.DefName + ":" + l.Count).ToArray())}|{request.DestinationTile}";
    }

    public IReadOnlyList<FormingCaravanInfo> GetFormingCaravans() => Forming;

    public CommandResult CancelFormingCaravan(int formingId)
    {
        Calls.Add($"CancelFormingCaravan({formingId})");
        FormingCaravanInfo forming = Forming.FirstOrDefault(f => f.Id == formingId);
        if (forming == null) return CommandResult.Fail($"no caravan is being formed with id {formingId}");
        Forming.Remove(forming);
        return CommandResult.Ok("Stopped forming the caravan");
    }

    public IReadOnlyList<CaravanInfo> GetCaravans() => Caravans;

    public WorldSurveyInfo GetWorldSurvey(int originCaravanId) => Survey;

    public IReadOnlyList<TravelEstimateInfo> GetTravelEstimates(int originCaravanId, IReadOnlyList<int> tiles)
    {
        return tiles.Select(t => TravelEstimates.TryGetValue(t, out TravelEstimateInfo e)
            ? e
            : new TravelEstimateInfo { Tile = t, Reachable = true, Ticks = 60000, DistanceTiles = 10f }).ToList();
    }

    public WorldTileInfo GetWorldTile(int tile, int originCaravanId)
    {
        if (tile > 99999) return new WorldTileInfo { Tile = tile, Error = $"tile {tile} does not exist on this layer of the world" };
        return WorldTiles.TryGetValue(tile, out WorldTileInfo info)
            ? info
            : new WorldTileInfo { Tile = tile, Biome = "Temperate forest", Hilliness = "Flat", TemperatureC = 14f, MinTemperatureC = -8f, MaxTemperatureC = 31f, RainfallMm = 900f, ElevationM = 300f, CanSettle = true };
    }

    public CaravanOptionsInfo GetCaravanOptions(int caravanId, int tile)
    {
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return new CaravanOptionsInfo { Error = "caravan not found" };
        if (!string.IsNullOrEmpty(CaravanOrdersError)) return new CaravanOptionsInfo { Error = CaravanOrdersError, CaravanName = caravan.Name };
        return new CaravanOptionsInfo { CaravanName = caravan.Name, Options = CaravanOrders };
    }

    public CommandResult SendCaravan(int caravanId, int tile, int optionNumber, string label)
    {
        Calls.Add($"SendCaravan({caravanId},{tile},{optionNumber},{label})");
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return CommandResult.Fail("caravan not found");
        FloatMenuOptionInfo option = CaravanOrders.FirstOrDefault(o => o.Number == optionNumber);
        if (option == null || !string.Equals(option.Label, label, StringComparison.OrdinalIgnoreCase)) return CommandResult.Fail("orders changed");
        if (option.Disabled) return CommandResult.Fail("disabled");
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
            return CommandResult.Ok($"\"{label}\" is waiting for an answer");
        }
        caravan.Moving = true;
        caravan.Paused = false;
        caravan.DestinationTile = tile;
        caravan.DestinationLabel = "tile " + tile;
        caravan.EtaTicks = 150000;
        return CommandResult.Ok($"{caravan.Name} is on its way to tile {tile}");
    }

    public CommandResult SetCaravanMovement(int caravanId, string action)
    {
        Calls.Add($"SetCaravanMovement({caravanId},{action})");
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return CommandResult.Fail("caravan not found");
        if (!caravan.Moving) return CommandResult.Fail($"{caravan.Name} has no destination");
        if (action == CaravanMovementAction.Pause) caravan.Paused = true;
        else if (action == CaravanMovementAction.Resume) caravan.Paused = false;
        else
        {
            caravan.Moving = false;
            caravan.Paused = false;
            caravan.DestinationTile = -1;
        }
        return CommandResult.Ok($"{caravan.Name} {action}");
    }

    public CommandResult SettleCaravan(int caravanId, string action)
    {
        Calls.Add($"SettleCaravan({caravanId},{action})");
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return CommandResult.Fail("caravan not found");
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
            return CommandResult.Ok("The game asks to confirm");
        }
        return CommandResult.Ok($"{caravan.Name} is starting to {action}");
    }

    public CommandResult ScheduleSurgery(SurgeryRequest request)
    {
        Calls.Add($"ScheduleSurgery({request?.PawnId},{request?.RecipeDefName},{request?.BodyPartLabel})");
        if (request == null) return CommandResult.Fail("no surgery requested");
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == request.PawnId);
        if (pawn == null) return CommandResult.Fail("colonist not found");
        DefInfo recipe = SurgeryRecipes.FirstOrDefault(d => string.Equals(d.DefName, request.RecipeDefName, StringComparison.OrdinalIgnoreCase));
        if (recipe == null) return CommandResult.Fail($"unknown surgery {request.RecipeDefName}");
        if (LockedSurgeries.Contains(recipe.DefName)) return CommandResult.Fail($"{recipe.Label} needs more research first");
        ColonistHealthInfo health = Health.FirstOrDefault(h => h.PawnId == request.PawnId);
        if (health == null)
        {
            health = new ColonistHealthInfo { PawnId = request.PawnId };
            Health.Add(health);
        }
        string part = request.BodyPartLabel ?? string.Empty;
        var pending = health.PendingSurgeries.ToList();
        if (pending.Any(b => string.Equals(b.Label, recipe.Label, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Part ?? string.Empty, part, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"{recipe.Label} is already scheduled on {pawn.Name}");
        pending.Add(new SurgeryBillInfo { Label = recipe.Label, Part = part });
        health.PendingSurgeries = pending;
        return CommandResult.Ok($"Scheduled {recipe.Label} on {pawn.Name}" + (part.Length > 0 ? $" ({part})" : string.Empty) + ".");
    }

    public CommandResult SetPrisonerInteraction(int pawnId, string modeDefName)
    {
        Calls.Add($"SetPrisonerInteraction({pawnId},{modeDefName})");
        PrisonerInfo pawn = Prisoners.FirstOrDefault(p => p.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("prisoner not found");
        DefInfo mode = PrisonerInteractionModes.FirstOrDefault(d => string.Equals(d.DefName, modeDefName, StringComparison.OrdinalIgnoreCase));
        if (mode == null) return CommandResult.Fail($"unknown prisoner interaction mode {modeDefName}");
        pawn.InteractionMode = mode.DefName;
        pawn.InteractionModeLabel = mode.Label;
        return CommandResult.Ok();
    }

    public CommandResult ReleasePrisoner(int pawnId)
    {
        Calls.Add($"ReleasePrisoner({pawnId})");
        PrisonerInfo pawn = Prisoners.FirstOrDefault(p => p.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("prisoner not found");
        Prisoners.Remove(pawn);
        return CommandResult.Ok($"{pawn.Name} was released and left the colony.");
    }

    public CommandResult TameAnimal(int pawnId)
    {
        Calls.Add($"TameAnimal({pawnId})");
        AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
        if (animal == null) return CommandResult.Fail("animal not found");
        if (!animal.Wild) return CommandResult.Fail($"{animal.Name} is already tame");
        if (!TameMarked.Add(pawnId)) return CommandResult.Fail($"{animal.Name} is already marked for taming");
        return CommandResult.Ok($"Marked {animal.Name} for taming. A handler will attempt it when available.");
    }

    public CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable)
    {
        Calls.Add($"SetAnimalTraining({pawnId},{trainableDefName},{enable})");
        AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
        if (animal == null) return CommandResult.Fail("animal not found");
        if (animal.Wild) return CommandResult.Fail($"{animal.Name} is wild; tame it first");
        DefInfo training = AnimalTraining.FirstOrDefault(d => string.Equals(d.DefName, trainableDefName, StringComparison.OrdinalIgnoreCase));
        if (training == null) return CommandResult.Fail($"unknown animal training {trainableDefName}");
        string key = $"{pawnId}:{training.DefName}";
        if (enable) TrainingOn.Add(key);
        else TrainingOn.Remove(key);
        return CommandResult.Ok(enable
            ? $"{animal.Name} will now be trained in {training.Label}."
            : $"{animal.Name} will no longer be trained in {training.Label}.");
    }

    public CommandResult SetAnimalArea(int pawnId, string areaLabel)
    {
        Calls.Add($"SetAnimalArea({pawnId},{areaLabel})");
        AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
        if (animal == null) return CommandResult.Fail("animal not found");
        if (animal.Wild) return CommandResult.Fail($"{animal.Name} is wild; tame it first");
        string label = areaLabel ?? string.Empty;
        if (label.Length > 0 && !AnimalAreas.Any(d => string.Equals(d.Label, label, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"unknown animal area {label}");
        animal.Area = label;
        return CommandResult.Ok($"{animal.Name}'s allowed area is now {(label.Length > 0 ? label : "unrestricted")}.");
    }

    public CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName)
    {
        Calls.Add(string.IsNullOrEmpty(plantDefName) ? $"AddZone({kind},{rect})" : $"AddZone({kind},{rect},{plantDefName})");
        bool stockpile = string.Equals(kind, "Stockpile", StringComparison.OrdinalIgnoreCase);
        bool growing = string.Equals(kind, "Growing", StringComparison.OrdinalIgnoreCase);
        if (!stockpile && !growing) return CommandResult.Fail($"unknown zone kind {kind}. Use stockpile or growing.");
        int id = Zones.Count == 0 ? 1 : Zones.Max(z => z.Id) + 1;
        string label = stockpile ? $"Stockpile {id}" : $"Growing zone {id}";
        Zones.Add(new ZoneInfo { Id = id, Label = label, Kind = stockpile ? "Stockpile" : "Growing", CellCount = rect.CellCount });
        return CommandResult.Ok($"Added {label.ToLowerInvariant()} [{id}] covering {rect.CellCount} cells.");
    }

    public CommandResult DeleteZone(int zoneId)
    {
        Calls.Add($"DeleteZone({zoneId})");
        ZoneInfo zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
        Zones.Remove(zone);
        ZoneDisallowed.Remove(zoneId);
        return CommandResult.Ok($"Deleted zone [{zoneId}] {zone.Label}. Stored items stay on the ground.");
    }

    public CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames)
    {
        Calls.Add($"SetZoneFilter({zoneId})");
        ZoneInfo zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
        if (!string.Equals(zone.Kind, "Stockpile", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{zone.Label} is a growing zone and has no storage filter");
        var allows = (allowDefNames ?? Array.Empty<string>()).ToList();
        var disallows = (disallowDefNames ?? Array.Empty<string>()).ToList();
        if (allows.Count == 0 && disallows.Count == 0) return CommandResult.Fail("nothing to allow or disallow");
        foreach (string defName in allows.Concat(disallows))
        {
            if (!StorageFilterables.Any(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"unknown stored item {defName}");
        }
        if (!ZoneDisallowed.TryGetValue(zoneId, out HashSet<string> banned))
        {
            banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZoneDisallowed[zoneId] = banned;
        }
        foreach (string defName in allows) banned.Remove(defName);
        foreach (string defName in disallows) banned.Add(defName);
        var parts = new List<string>();
        if (allows.Count > 0) parts.Add("allows " + string.Join(", ", allows.Select(ItemLabel).ToArray()));
        if (disallows.Count > 0) parts.Add("disallows " + string.Join(", ", disallows.Select(ItemLabel).ToArray()));
        return CommandResult.Ok($"Zone [{zoneId}] {zone.Label} now {string.Join("; ", parts.ToArray())}.");
    }

    private string ItemLabel(string defName)
    {
        DefInfo def = StorageFilterables.FirstOrDefault(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase));
        return def != null ? def.Label : defName;
    }

    public CommandResult AddBill(int tableId, string recipeDefName, int count)
    {
        Calls.Add($"AddBill({tableId},{recipeDefName},{count})");
        WorkTableInfo table = WorkTables.FirstOrDefault(t => t.Id == tableId);
        if (table == null) return CommandResult.Fail($"no work table has id {tableId}");
        DefInfo recipe = GetTableRecipes(tableId).FirstOrDefault(d => string.Equals(d.DefName, recipeDefName, StringComparison.OrdinalIgnoreCase));
        if (recipe == null) return CommandResult.Fail($"unknown bill recipe {recipeDefName}");
        if (count < 1) return CommandResult.Fail($"count must be at least 1, not {count}");
        if (!TableBills.TryGetValue(tableId, out List<BillInfo> bills))
        {
            bills = [];
            TableBills[tableId] = bills;
        }
        bills.Add(new BillInfo { Index = bills.Count + 1, Label = $"Make {recipe.Label}", Repeat = $"{count}x" });
        table.BillCount = bills.Count;
        return CommandResult.Ok($"Added bill [{bills.Count}] Make {recipe.Label} x{count} on {table.Label}.");
    }

    public CommandResult DeleteBill(int tableId, int index)
    {
        Calls.Add($"DeleteBill({tableId},{index})");
        WorkTableInfo table = WorkTables.FirstOrDefault(t => t.Id == tableId);
        if (table == null) return CommandResult.Fail($"no work table has id {tableId}");
        if (!TableBills.TryGetValue(tableId, out List<BillInfo> bills) || index < 1 || index > bills.Count)
            return CommandResult.Fail($"bill [{index}] does not exist on {table?.Label ?? "that table"}. List the bills again to get valid numbers.");
        BillInfo removed = bills[index - 1];
        bills.RemoveAt(index - 1);
        for (int i = 0; i < bills.Count; i++) bills[i].Index = i + 1;
        table.BillCount = bills.Count;
        return CommandResult.Ok($"Deleted bill {removed.Label} from {table.Label}. Materials already gathered stay where they are.");
    }

    /// <summary>
    /// 與正式端同語義的假結算：單價取貨物市價（正式端用遊戲公式），先驗證全部再交割，
    /// 餘額與庫存不足整筆不做。殖民地庫存與銀兩取自 <see cref="Resources"/>。
    /// </summary>
    public CommandResult ExecuteTrade(TradeRequest request)
    {
        Calls.Add($"ExecuteTrade({request?.TraderId},{request?.NegotiatorPawnId})");
        if (request == null) return CommandResult.Fail("no trade requested");
        TraderInfo trader = Traders.FirstOrDefault(t => string.Equals(t.Id, request.TraderId, StringComparison.OrdinalIgnoreCase));
        if (trader == null) return CommandResult.Fail($"unknown trader \"{request.TraderId}\". List the traders again to get a valid id.");
        ColonistInfo negotiator = Colonists.FirstOrDefault(c => c.Id == request.NegotiatorPawnId);
        if (negotiator == null) return CommandResult.Fail("negotiator not found");
        if (negotiator.Downed) return CommandResult.Fail($"{negotiator.Name} is downed and cannot negotiate.");
        var buys = request.Buys ?? Array.Empty<TradeLine>();
        var sells = request.Sells ?? Array.Empty<TradeLine>();
        if (buys.Count == 0 && sells.Count == 0) return CommandResult.Fail("nothing to buy or sell.");

        foreach (TradeLine line in buys.Concat(sells))
        {
            if (string.Equals(line.DefName, "Silver", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("silver moves automatically as payment; do not list it.");
            if (line.Count <= 0) return CommandResult.Fail($"invalid count {line.Count} for {line.DefName}.");
        }
        foreach (TradeLine line in buys)
        {
            int available = trader.Goods.Where(g => string.Equals(g.DefName, line.DefName, StringComparison.OrdinalIgnoreCase)).Sum(g => g.Count);
            if (available < line.Count)
                return CommandResult.Fail($"{trader.Name} only has {available} {line.DefName} for sale, not {line.Count}.");
        }
        var stock = Resources.Items.ToList();
        foreach (TradeLine line in sells)
        {
            int available = stock.Where(r => string.Equals(r.DefName, line.DefName, StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
            if (available < line.Count)
                return CommandResult.Fail($"the colony only has {available} sellable {line.DefName}, not {line.Count}.");
        }

        float PriceOf(string defName, float fallback)
        {
            return Prices.TryGetValue(defName, out float p) ? p : fallback;
        }
        int buyTotal = buys.Sum(l => (int)(PriceOf(l.DefName, trader.Goods.FirstOrDefault(g => string.Equals(g.DefName, l.DefName, StringComparison.OrdinalIgnoreCase))?.MarketValue ?? 1f) * l.Count));
        int sellTotal = sells.Sum(l => (int)(PriceOf(l.DefName, 1f) * l.Count));
        int colonySilver = stock.Where(r => string.Equals(r.DefName, "Silver", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
        if (colonySilver - buyTotal + sellTotal < 0)
            return CommandResult.Fail($"the colony cannot afford this: needs {buyTotal - sellTotal} silver but has {colonySilver}.");
        if (trader.Silver - sellTotal + buyTotal < 0)
            return CommandResult.Fail($"{trader.Name} cannot afford this: needs {sellTotal - buyTotal} silver but has {trader.Silver}.");

        foreach (TradeLine line in buys) TakeFrom(trader.Goods, line.DefName, line.Count);
        foreach (TradeLine line in buys) AddTo(stock, line.DefName, LabelOf(line.DefName), line.Count);
        foreach (TradeLine line in sells) TakeFrom(stock, line.DefName, line.Count);
        foreach (TradeLine line in sells)
        {
            var goods = trader.Goods.ToList();
            AddTo(goods, line.DefName, LabelOf(line.DefName), line.Count, PriceOf(line.DefName, 1f));
            trader.Goods = goods;
        }
        TakeFrom(stock, "Silver", buyTotal);
        AddTo(stock, "Silver", "silver", sellTotal);
        trader.Silver += buyTotal - sellTotal;
        Resources = new ResourceSnapshot { TotalHumanEdibleNutrition = Resources.TotalHumanEdibleNutrition, Items = stock };

        string bought = buys.Count == 0 ? string.Empty : "bought " + string.Join(", ", buys.Select(l => $"{LabelOf(l.DefName)} ({l.DefName}) x{l.Count}"));
        string sold = sells.Count == 0 ? string.Empty : "sold " + string.Join(", ", sells.Select(l => $"{LabelOf(l.DefName)} ({l.DefName}) x{l.Count}"));
        string what = string.Join("; ", new[] { bought, sold }.Where(s => s.Length > 0));
        return CommandResult.Ok($"Traded with {trader.Name} (negotiated by {negotiator.Name}): {what}. Colony silver {colonySilver} -> {colonySilver - buyTotal + sellTotal}.");
    }

    private string LabelOf(string defName)
    {
        DefInfo known = TradeGoods.FirstOrDefault(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase));
        if (known != null) return known.Label;
        ResourceInfo resource = Resources.Items.FirstOrDefault(r => string.Equals(r.DefName, defName, StringComparison.OrdinalIgnoreCase));
        return resource?.Label ?? defName;
    }

    private static void TakeFrom<T>(IEnumerable<T> items, string defName, int count) where T : class
    {
        int rest = count;
        foreach (T item in items.ToList())
        {
            if (rest <= 0) break;
            if (item is TradeGoodsInfo g)
            {
                if (!string.Equals(g.DefName, defName, StringComparison.OrdinalIgnoreCase)) continue;
                int take = Math.Min(rest, g.Count);
                g.Count -= take;
                rest -= take;
            }
            else if (item is ResourceInfo r)
            {
                if (!string.Equals(r.DefName, defName, StringComparison.OrdinalIgnoreCase)) continue;
                int take = Math.Min(rest, r.Count);
                r.Count -= take;
                rest -= take;
            }
        }
    }

    private static void AddTo<T>(List<T> items, string defName, string label, int count, float marketValue = 1f) where T : class, new()
    {
        if (count <= 0) return;
        if (typeof(T) == typeof(ResourceInfo))
        {
            var list = items as List<ResourceInfo>;
            ResourceInfo existing = list.FirstOrDefault(r => string.Equals(r.DefName, defName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Count += count;
            else list.Add(new ResourceInfo { DefName = defName, Label = label, Count = count });
        }
        else if (typeof(T) == typeof(TradeGoodsInfo))
        {
            var list = items as List<TradeGoodsInfo>;
            TradeGoodsInfo existing = list.FirstOrDefault(g => string.Equals(g.DefName, defName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Count += count;
            else list.Add(new TradeGoodsInfo { DefName = defName, Label = label, Count = count, MarketValue = marketValue });
        }
    }

    private static void Mark(ThingInfo thing, string def)
    {
        var list = thing.Designations.ToList();
        if (!list.Contains(def)) list.Add(def);
        thing.Designations = list;
    }

    private static void Unmark(ThingInfo thing, string def)
    {
        thing.Designations = thing.Designations.Where(d => d != def).ToList();
    }
}
