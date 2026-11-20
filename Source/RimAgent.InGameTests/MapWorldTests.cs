using System;
using System.Collections.Generic;
using System.Linq;
using DevTools.Testing;
using RimAgent.Game;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

/// <summary>
/// 正式 <see cref="RimWorldGameWorld"/> 在真實地圖上的行為（它被排除在 headless 覆蓋率之外，只能在遊戲內驗）：
/// 查詢對照原版資料，命令做完以原版 API 讀回、再復原現場。
/// </summary>
[TestFixture(TestType.Playing)]
internal sealed partial class MapWorldTests : IMapGeneration, IWorldGeneration
{
    // DevTools 預設是空白地圖；本套件需要真實殖民地、礦脈及可接任務的殖民者。
    public MapGenerationSettings MapGenerationSettings => new()
    {
        size = 200,
        mapGeneratorDef = MapGeneratorDefOf.Base_Player,
        biome = BiomeDefOf.TemperateForest
    };

    public WorldGenerationSettings WorldGenerationSettings => new()
    {
        allowedDevelopmentalStages = DevelopmentalStage.Adult,
        startingPawnsRequired = [new PawnKindCount { kindDef = PawnKindDefOf.Colonist, count = 3, requiredAtStart = true }]
    };

    private RimWorldGameWorld _world;
    private const string GameLogged = "game logged: ";
    private const string CommandFailed = "command failed: ";

    [SetUp]
    public void SetUp()
    {
        _world = new RimWorldGameWorld();
    }

    [Test]
    public void ReadyWhilePlaying()
    {
        if (TestHelpers.SkipWithoutMap(nameof(ReadyWhilePlaying))) return;
        string reason = _world.GetNotReadyReason();
        if (reason != null)
        {
            Expect.IsNull(reason, "GetNotReadyReason returned: " + reason);
            return;
        }
        Expect.IsNotNull(RimAgentGameComponent.Instance, "RimAgentGameComponent.Instance != null");
    }

    [Test]
    public void ColonistsMatchVanillaFreeColonists()
    {
        if (TestHelpers.SkipWithoutMap(nameof(ColonistsMatchVanillaFreeColonists))) return;
        var failures = new List<string>();
        // 基準不走轉接層用的 mapPawns.FreeColonistsSpawned，改從地圖上所有 pawn 以原始欄位篩：
        // 玩家派系、類人、不是別人的客人或囚犯。
        Dictionary<int, Pawn> vanilla = Find.CurrentMap.mapPawns.AllPawnsSpawned
            .Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike && p.HostFaction == null)
            .ToDictionary(p => p.thingIDNumber);
        IReadOnlyList<ColonistInfo> colonists = _world.GetColonists();
        foreach (ColonistInfo info in colonists)
        {
            if (!vanilla.TryGetValue(info.Id, out Pawn pawn))
            {
                failures.Add($"id {info.Id} ({info.Name}) is not a free colonist on the map");
                continue;
            }
            if (info.X != pawn.Position.x || info.Z != pawn.Position.z)
                failures.Add($"{pawn.LabelShort}: at ({info.X},{info.Z}), vanilla {pawn.Position}");
            if (string.IsNullOrEmpty(info.Name)) failures.Add($"{pawn.LabelShort}: empty name");
        }
        foreach (Pawn pawn in vanilla.Values.Where(p => colonists.All(c => c.Id != p.thingIDNumber)))
            failures.Add($"{pawn.LabelShort} ({pawn.thingIDNumber}) is missing from GetColonists");
        TestHelpers.AssertNone(failures, "colonist list differs from vanilla");
    }

    /// <summary>
    /// 每個查詢都在真實地圖上跑一遍：不丟例外，也不在遊戲 log 留下錯誤
    /// （轉接層對模組資料的例外會降級成警告；錯誤代表沒接住的問題）。
    /// </summary>
    [Test]
    public void EveryQueryRunsCleanOnRealMap()
    {
        if (TestHelpers.SkipWithoutMap(nameof(EveryQueryRunsCleanOnRealMap))) return;
        var failures = new List<string>();
        _world.TakeNewGameErrors();

        Map map = Find.CurrentMap;
        var center = map.Center;
        var queries = new List<KeyValuePair<string, Func<object>>>
        {
            Query("GetOverview", () => _world.GetOverview()),
            Query("GetHostileCount", () => _world.GetHostileCount()),
            Query("GetColonistsInMedicalEmergency", () => _world.GetColonistsInMedicalEmergency()),
            Query("GetFireCount", () => _world.GetFireCount()),
            Query("GetColonists", () => _world.GetColonists()),
            Query("GetPrisoners", () => _world.GetPrisoners()),
            Query("GetAnimals", () => _world.GetAnimals()),
            Query("GetZones", () => _world.GetZones()),
            Query("GetWorkTables", () => _world.GetWorkTables()),
            Query("GetHealth", () => _world.GetHealth()),
            Query("GetResources", () => _world.GetResources()),
            Query("GetAlerts", () => _world.GetAlerts()),
            Query("GetRecentLetters", () => _world.GetRecentLetters()),
            Query("GetPendingChoices", () => _world.GetPendingChoices()),
            Query("GetQuests", () => _world.GetQuests()),
            Query("GetPower", () => _world.GetPower()),
            Query("GetThreats", () => _world.GetThreats()),
            Query("GetResearch", () => _world.GetResearch()),
            Query("GetTimeSpeed", () => _world.GetTimeSpeed()),
            Query("GetMaps", () => _world.GetMaps()),
            Query("GetCaravans", () => _world.GetCaravans()),
            Query("GetWorldSurvey", () => _world.GetWorldSurvey(-1)),
            Query("GetTravelEstimates", () => _world.GetTravelEstimates(-1, new List<int> { Find.CurrentMap.Tile.tileId })),
            Query("GetWorldTile", () => _world.GetWorldTile(Find.CurrentMap.Tile.tileId, -1)),
            Query("GetTraders", () => _world.GetTraders()),
            Query("FindThings", () => _world.FindThings(new ThingQuery())),
            Query("GetSelection", () => _world.GetSelection()),
            Query("GetMapBounds", () => _world.GetMapBounds()),
            Query("GetCell", () => _world.GetCell(center.x, center.z)),
            Query("GetCells", () => _world.GetCells(new CellRectSpec(center.x - 2, center.z - 2, center.x + 2, center.z + 2))),
            Query("GetMapSurvey", () => _world.GetMapSurvey()),
            Query("AnimalAreas", () => _world.AnimalAreas),
            Query("ResearchProjects", () => _world.ResearchProjects),
            Query("FinishedResearchProjects", () => _world.FinishedResearchProjects)
        };
        foreach (KeyValuePair<string, Func<object>> query in queries) Run(query.Key, query.Value, failures);

        IReadOnlyList<WorkTableInfo> tables = SafeList(() => _world.GetWorkTables());
        foreach (WorkTableInfo table in tables)
        {
            Run($"GetBills({table.Label})", () => _world.GetBills(table.Id), failures);
            Run($"GetTableRecipes({table.Label})", () => _world.GetTableRecipes(table.Id), failures);
        }
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            Run($"GetGizmos({pawn.LabelShort})", () => _world.GetGizmos(pawn.thingIDNumber), failures);
        foreach (Caravan caravan in Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled))
        {
            Run($"GetWorldSurvey({caravan.LabelCap})", () => _world.GetWorldSurvey(caravan.ID), failures);
            Run($"GetCaravanOptions({caravan.LabelCap})", () => _world.GetCaravanOptions(caravan.ID, caravan.Tile.tileId), failures);
        }

        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "queries that failed on the real map");
    }

    private static KeyValuePair<string, Func<object>> Query(string name, Func<object> call) =>
        new(name, call);

    private static void Run(string name, Func<object> call, List<string> failures)
    {
        try
        {
            if (call() == null) failures.Add(name + ": returned null");
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IReadOnlyList<T> SafeList<T>(Func<IReadOnlyList<T>> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            // 例外已由 EveryQueryRunsCleanOnRealMap 的查詢清單記錄。
            return [];
        }
    }

    /// <summary>從第一位殖民者往外找一塊 size×size、每格都通過 <paramref name="ok"/> 的矩形。</summary>
    private static CellRect? FindFreeRect(Map map, int size, Predicate<IntVec3> ok)
    {
        Pawn anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        IntVec3 origin = anchor?.Position ?? map.Center;
        foreach (IntVec3 corner in GenRadial.RadialCellsAround(origin, 30f, true))
        {
            var rect = new CellRect(corner.x, corner.z, size, size);
            if (rect.InBounds(map) && rect.Cells.All(c => ok(c))) return rect;
        }
        return null;
    }
}
