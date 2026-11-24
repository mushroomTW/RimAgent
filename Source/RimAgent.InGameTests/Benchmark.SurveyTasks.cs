using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimAgent.Game;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>勘查專用題：答案的位置只有看過指定區域才知道，只在比較勘查工具時跑。</summary>
public sealed partial class BenchmarkComponent
{
    private const int SiteSize = 16;

    private static CellRect _site;
    private static readonly List<(IntVec3 Cell, TerrainDef Terrain)> _siteTerrain = [];
    private static readonly List<(Thing Thing, IntVec3 Cell, Rot4 Rotation)> _siteThings = [];
    private static readonly List<Thing> _siteWalls = [];
    private static readonly List<Designation> _siteMarks = [];
    /// <summary>生成玩家的牆會自動擴大居家區；null 表示還沒佈置。</summary>
    private static HashSet<IntVec3> _homeBefore;

    private static IntVec3 Gap => new(_site.minX + 12, 0, _site.minZ + 6);

    private static CellRect RichSoil => Patch(9, 3, 5, 5);

    private static CellRect FirmGround => Patch(2, 8, 5, 4);

    /// <summary>每 4 格一根柱子，只少一根，6x6 的空地只有這裡放得下。</summary>
    private static CellRect Opening => Patch(5, 1, 7, 7);

    private static BenchTask SurveyRichSoilTask() => SurveyTask(
        "survey_rich_soil",
        area => $"{area}裡有一塊 5x5 的肥沃土壤。把水稻田剛好劃在那塊肥沃土壤上，不要超出。",
        map => SetSiteTerrain(map, "Soil", "SoilRich", RichSoil),
        map =>
        {
            List<IntVec3> cells = [.. map.zoneManager.AllZones.OfType<Zone_Growing>().Where(z => z.GetPlantDefToGrow()?.defName == "Plant_Rice").SelectMany(z => z.Cells).Where(_site.Contains)];
            int rich = cells.Count(c => RichSoil.Contains(c));
            return new() { [TargetKey] = rich, [OthersKey] = cells.Count - rich };
        },
        25,
        world => world.AddZone("Growing", Spec(RichSoil), "Plant_Rice"));

    private static BenchTask SurveyWallGapTask() => SurveyTask(
        "survey_wall_gap",
        area => $"{area}裡有一圈木牆，缺了一格。在缺口放一格木牆藍圖把它補起來，其他地方不要放藍圖。",
        map => BuildSiteWalls(map, Patch(3, 2, 10, 11).EdgeCells.Where(c => c != Gap)),
        map => new()
        {
            [TargetKey] = PlannedBuildings(map).Count(t => t.Position == Gap && t.def.entityDefToBuild == ThingDefOf.Wall),
            [OthersKey] = PlannedBuildings(map).Count(t => t.Position != Gap)
        },
        1,
        world => world.TryPlaceBlueprint("Wall", Gap.x, Gap.z, 0, "WoodLog"));

    /// <summary>只問位置、不放東西：放置失敗的回覆會列出撐得住的地，放得到就不必勘查。</summary>
    private static BenchTask SurveyFirmGroundTask()
    {
        BenchTask task = SurveyTask(
            "survey_firm_ground",
            area => $"{area}大多是軟地，只有一小塊長方形的地撐得住中型以上的建築。回覆那塊地西南角的座標，寫成 (x,z)；不要放任何藍圖。",
            map => SetSiteTerrain(map, "MarshyTerrain", "Soil", FirmGround),
            map => new() { [TargetKey] = 0, [OthersKey] = PlannedBuildings(map).Count() },
            0,
            null);
        task.Check = (_, after, reply) =>
        {
            if (after[OthersKey] > 0) return string.Create(CultureInfo.InvariantCulture, $"placed {after[OthersKey]} blueprint(s)");
            string corner = string.Create(CultureInfo.InvariantCulture, $"({FirmGround.minX},{FirmGround.minZ})");
            return (reply ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace('，', ',').Replace('（', '(').Replace('）', ')').Contains(corner, StringComparison.Ordinal)
                ? null
                : "reply does not name " + corner;
        };
        return task;
    }

    private static BenchTask SurveyClearGroundTask() => SurveyTask(
        "survey_clear_ground",
        area => $"{area}裡立著許多柱子。找出一塊裡面完全沒有柱子的 6x6 空地，在那裡劃一個剛好 6x6 的儲存區。",
        map => BuildSiteWalls(map, _site.Cells.Where(c => (c.x - _site.minX) % 4 == 0 && (c.z - _site.minZ) % 4 == 0 && !Opening.Contains(c))),
        map =>
        {
            List<IntVec3> cells = [.. map.zoneManager.AllZones.OfType<Zone_Stockpile>().SelectMany(z => z.Cells).Where(_site.Contains)];
            bool square = cells.Count == 36 && cells.Max(c => c.x) - cells.Min(c => c.x) == 5 && cells.Max(c => c.z) - cells.Min(c => c.z) == 5;
            int inside = cells.Count(c => Opening.Contains(c));
            return new() { [TargetKey] = square ? inside : 0, [OthersKey] = cells.Count - inside };
        },
        36,
        world => world.AddZone("Stockpile", Spec(Patch(5, 1, 6, 6)), null));

    private static BenchTask SurveyTask(
        string id,
        Func<string, string> instruction,
        Action<Map> build,
        Func<Map, Dictionary<string, int>> measure,
        int need,
        Func<IGameWorld, CommandResult> solve)
    {
        var task = new BenchTask
        {
            Id = id,
            Tier = Survey,
            Instruction = instruction("指定區域"),
            Cleanup = RestoreSite,
            Measure = measure,
            Check = (_, after, _) => after[TargetKey] == need && after[OthersKey] == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"in place {after[TargetKey]} (need {need}), misplaced {after[OthersKey]}"),
            Solve = solve
        };
        task.Setup = map =>
        {
            ClearBlueprints(map);
            if (!TryPrepareSite(map)) return "no open site near the colonists";
            build(map);
            task.Instruction = instruction(string.Create(CultureInfo.InvariantCulture, $"({_site.minX},{_site.minZ}) 到 ({_site.maxX},{_site.maxZ}) 的區域"));
            return null;
        };
        return task;
    }

    private static CellRect Patch(int dx, int dz, int width, int height) => new(_site.minX + dx, _site.minZ + dz, width, height);

    private static CellRectSpec Spec(CellRect r) => new(r.minX, r.minZ, r.maxX, r.maxZ);

    /// <summary>找離殖民者最近的天然空地，先收起上面的植物與物品，結束後放回。</summary>
    private static bool TryPrepareSite(Map map)
    {
        Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        if (colonist == null) return false;
        IntVec3 center = colonist.Position;
        IntVec3 corner = map.AllCells
            .Where(c => c.x >= 12 && c.z >= 12 && c.x + SiteSize + 12 < map.Size.x && c.z + SiteSize + 12 < map.Size.z)
            .OrderBy(c => (c - center).LengthHorizontalSquared)
            .FirstOrDefault(c => new CellRect(c.x, c.z, SiteSize, SiteSize).Cells.All(cell => IsOpen(map, cell)));
        if (corner == default) return false;
        _homeBefore = [.. map.areaManager.Home.ActiveCells];
        _site = new CellRect(corner.x, corner.z, SiteSize, SiteSize);
        foreach (IntVec3 cell in _site)
        {
            foreach (Thing thing in cell.GetThingList(map).Where(t => t.def.category is ThingCategory.Plant or ThingCategory.Item).ToList())
            {
                _siteMarks.AddRange(map.designationManager.AllDesignationsOn(thing));
                _siteThings.Add((thing, cell, thing.Rotation));
                thing.DeSpawn();
            }
        }
        return true;
    }

    private static bool IsOpen(Map map, IntVec3 cell) =>
        !cell.Fogged(map) && cell.GetTerrain(map) is { natural: true, IsWater: false } && map.terrainGrid.UnderTerrainAt(cell) == null
        && !cell.Roofed(map) && map.zoneManager.ZoneAt(cell) == null
        && cell.GetThingList(map).All(t => t.def.category is ThingCategory.Plant or ThingCategory.Item or ThingCategory.Filth);

    private static void SetSiteTerrain(Map map, string outside, string inside, CellRect patch)
    {
        TerrainDef outer = DefDatabase<TerrainDef>.GetNamed(outside);
        TerrainDef inner = DefDatabase<TerrainDef>.GetNamed(inside);
        foreach (IntVec3 cell in _site)
        {
            _siteTerrain.Add((cell, cell.GetTerrain(map)));
            map.terrainGrid.SetTerrain(cell, patch.Contains(cell) ? inner : outer);
        }
    }

    private static void BuildSiteWalls(Map map, IEnumerable<IntVec3> cells)
    {
        foreach (IntVec3 cell in cells.ToList())
        {
            Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
            wall.SetFaction(Faction.OfPlayer);
            _siteWalls.Add(GenSpawn.Spawn(wall, cell, map));
        }
    }

    private static void RestoreSite(Map map)
    {
        ClearBlueprints(map);
        foreach (Thing wall in _siteWalls.Where(w => w.Spawned)) wall.Destroy();
        _siteWalls.Clear();
        foreach ((IntVec3 cell, TerrainDef terrain) in _siteTerrain) map.terrainGrid.SetTerrain(cell, terrain);
        _siteTerrain.Clear();
        foreach ((Thing thing, IntVec3 cell, Rot4 rotation) in _siteThings) GenSpawn.Spawn(thing, cell, map, rotation);
        _siteThings.Clear();
        foreach (Designation mark in _siteMarks.Where(m => map.designationManager.DesignationOn(m.target.Thing, m.def) == null)) map.designationManager.AddDesignation(mark);
        _siteMarks.Clear();
        if (_homeBefore == null) return;
        foreach (IntVec3 cell in map.areaManager.Home.ActiveCells.Where(c => !_homeBefore.Contains(c)).ToList()) map.areaManager.Home[cell] = false;
        _homeBefore = null;
    }
}
