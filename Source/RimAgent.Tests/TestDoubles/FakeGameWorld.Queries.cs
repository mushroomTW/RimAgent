using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public ColonyOverview GetOverview() => Overview;

    public int GetHostileCount() => Overview.HostileCount;

    public List<int> MedicalEmergencies { get; set; } = [];

    public IReadOnlyList<int> GetColonistsInMedicalEmergency() => [.. MedicalEmergencies];

    public int FireCount { get; set; }

    public int GetFireCount() => FireCount;

    public IReadOnlyList<ColonistInfo> GetColonists()
    {
        ColonistQueryCount++;
        // 真實世界每次查詢都回新快照；開啟後讓復原流程的「重新讀取」行為也能被測試。
        return SnapshotColonists ? [.. Colonists.Select(CloneColonist)] : Colonists;
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
                if (projects.Exists(d => string.Equals(d.DefName, p.DefName, StringComparison.OrdinalIgnoreCase))) continue;
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
        return [.. Maps.Select(m => new MapInfo
        {
            Id = m.Id,
            Label = m.Label,
            Tile = m.Tile,
            ColonistCount = m.ColonistCount,
            IsCurrent = m.Id == CurrentMapId
        })];
    }

    public CommandResult SetCurrentMap(int mapId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetCurrentMap({mapId})"));
        MapInfo map = Maps.FirstOrDefault(m => m.Id == mapId);
        if (map == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no map has id {mapId}"));
        if (mapId == CurrentMapId) return CommandResult.Ok($"{map.Label} is already the current map.");
        CurrentMapId = mapId;
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Switched to {map.Label} [{mapId}]. Later tools now operate on this map."));
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
            Support = LightOnlyCells.Contains((x, z)) ? GroundSupport.Light : GroundSupport.Heavy,
            Things = [.. Things.Where(t => t.X == x && t.Z == z)]
        };
    }

    public GroundSupport? GetSupport(int x, int z)
    {
        CellInfo cell = GetCell(x, z);
        return cell.Fogged ? null : cell.Support;
    }

    public int CellsRead { get; private set; }

    public IReadOnlyList<CellInfo> GetCells(CellRectSpec rect)
    {
        CellsRead += rect.CellCount;
        var result = new List<CellInfo>();
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
        {
            for (int x = rect.MinX; x <= rect.MaxX; x++) result.Add(GetCell(x, z));
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
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"WaitTicks({ticks})"));
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
            result = result.Where(t => t.Label.Contains(query.LabelContains, StringComparison.OrdinalIgnoreCase) ||
                                       t.DefName.Contains(query.LabelContains, StringComparison.OrdinalIgnoreCase));
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
}
