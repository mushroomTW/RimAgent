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
    /// <summary>比較勘查工具時這一輪指定的地點；null 表示自己找最近的。</summary>
    private static IntVec3? _fixedSite;
    private static readonly List<(IntVec3 Cell, TerrainDef Terrain)> _siteTerrain = [];
    private static readonly List<(Thing Thing, IntVec3 Cell, Rot4 Rotation)> _siteThings = [];
    private static readonly List<Thing> _siteBuilt = [];
    private static readonly List<Designation> _siteMarks = [];
    /// <summary>生成玩家的牆會自動擴大居家區；null 表示還沒佈置。</summary>
    private static HashSet<IntVec3> _homeBefore;

    private static IntVec3 Gap => new(_site.minX + 12, 0, _site.minZ + 6);

    private static CellRect RichSoil => Patch(9, 3, 5, 5);

    private static CellRect FirmGround => Patch(2, 8, 5, 4);

    /// <summary>每 4 格一根柱子，只少一根，6x6 的空地只有這裡放得下。</summary>
    private static CellRect Opening => Patch(5, 1, 7, 7);

    private static CellRect Outcrop => Patch(4, 4, 6, 6);

    private static IntVec3[] Ore => [new(_site.minX + 7, 0, _site.minZ + 5), new(_site.minX + 7, 0, _site.minZ + 6), new(_site.minX + 8, 0, _site.minZ + 6)];

    /// <summary>溪流橫越第 7 列，只有這一格留著原本的地。</summary>
    private static IntVec3 Ford => new(_site.minX + 10, 0, _site.minZ + 7);

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
            solve: null);
        task.Check = (_, after, reply) => ReplyNames(after, reply, FirmGround.Min);
        return task;
    }

    /// <summary>
    /// 只問位置的題目：沒放藍圖、回覆寫出了 <paramref name="cell"/>。不算照抄的區域兩角，最多寫兩個不同座標（答案加上範圍的另一角），
    /// 列出一長串座標碰運氣的算失敗。
    /// </summary>
    private static string ReplyNames(Dictionary<string, int> after, string reply, IntVec3 cell)
    {
        if (after[OthersKey] > 0) return string.Create(CultureInfo.InvariantCulture, $"placed {after[OthersKey]} blueprint(s)");
        string named = string.Create(CultureInfo.InvariantCulture, $"({cell.x},{cell.z})");
        string flat = BenchmarkScoring.FlattenCoordinates(reply);
        string[] corners = [string.Create(CultureInfo.InvariantCulture, $"({_site.minX},{_site.minZ})"), string.Create(CultureInfo.InvariantCulture, $"({_site.maxX},{_site.maxZ})")];
        int distinct = BenchmarkScoring.DistinctCells(flat, named, corners);
        if (distinct > MaxNamedCells) return string.Create(CultureInfo.InvariantCulture, $"reply names {distinct} different cells (max {MaxNamedCells})");
        return flat.Contains(named, StringComparison.Ordinal) ? null : "reply does not name " + named;
    }

    private const int MaxNamedCells = 2;

    /// <summary>岩石裡混著鋼鐵礦；整塊標記開採就會標到岩石。</summary>
    private static BenchTask SurveyOreTask() => SurveyTask(
        "survey_ore",
        area => $"{area}裡有一塊岩石，其中混著幾格鋼鐵礦。只把鋼鐵礦標記開採，岩石的其他部分不要標記。",
        map => SpawnSiteThings(map, Outcrop.Cells, c => ThingMaker.MakeThing(Ore.Contains(c) ? ThingDefOf.MineableSteel : DefDatabase<ThingDef>.GetNamed("Granite"))),
        map =>
        {
            List<IntVec3> marked = [.. _site.Cells.Where(c => map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null)];
            int ore = marked.Count(Ore.Contains);
            return new() { [TargetKey] = ore, [OthersKey] = marked.Count - ore };
        },
        3,
        world => All(Ore.Select(c => world.AddDesignationOnCell(c.x, c.z, DesignationDefOf.Mine.defName))));

    private static BenchTask SurveyFordTask()
    {
        BenchTask task = SurveyTask(
            "survey_ford",
            area => $"{area}有一條由西往東橫越的深水溪，溪上只有一格是陸地，可以走過去。回覆那一格的座標，寫成 (x,z)；不要放任何藍圖。",
            map => SetSiteRow(map, "WaterDeep", Ford),
            map => new() { [TargetKey] = 0, [OthersKey] = PlannedBuildings(map).Count() },
            0,
            solve: null);
        task.Check = (_, after, reply) => ReplyNames(after, reply, Ford);
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
        world => world.AddZone("Stockpile", Spec(Patch(5, 1, 6, 6)), plantDefName: null));

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
        IntVec3 corner = _fixedSite ?? FindSites(map, 1).FirstOrDefault();
        if (corner == default || !IsSite(map, corner)) return false;
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

    /// <summary>離殖民者最近、彼此不重疊的空地，最多 <paramref name="count"/> 塊。</summary>
    private static List<IntVec3> FindSites(Map map, int count)
    {
        Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        if (colonist == null) return [];
        IntVec3 center = colonist.Position;
        IEnumerable<IntVec3> corners = map.AllCells
            .Where(c => c.x >= 12 && c.z >= 12 && c.x + SiteSize + 12 < map.Size.x && c.z + SiteSize + 12 < map.Size.z)
            .OrderBy(c => (c - center).LengthHorizontalSquared);
        var sites = new List<IntVec3>();
        foreach (IntVec3 c in corners)
        {
            if (!IsSite(map, c) || !sites.TrueForAll(s => Math.Abs(s.x - c.x) >= SiteSize || Math.Abs(s.z - c.z) >= SiteSize)) continue;
            sites.Add(c);
            if (sites.Count >= count) break;
        }
        return sites;
    }

    private static bool IsSite(Map map, IntVec3 corner) =>
        new CellRect(corner.x, corner.z, SiteSize, SiteSize).Cells.All(cell => IsOpen(map, cell));

    private static bool IsOpen(Map map, IntVec3 cell) =>
        !cell.Fogged(map) && cell.GetTerrain(map) is { natural: true, IsWater: false } && map.terrainGrid.UnderTerrainAt(cell) == null
        && !cell.Roofed(map) && map.zoneManager.ZoneAt(cell) == null
        && cell.GetThingList(map).TrueForAll(t => t.def.category is ThingCategory.Plant or ThingCategory.Item or ThingCategory.Filth or ThingCategory.Pawn);

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

    private static void SetSiteRow(Map map, string terrain, IntVec3 gap)
    {
        TerrainDef water = DefDatabase<TerrainDef>.GetNamed(terrain);
        foreach (IntVec3 cell in _site.Cells.Where(c => c.z == gap.z && c != gap))
        {
            _siteTerrain.Add((cell, cell.GetTerrain(map)));
            map.terrainGrid.SetTerrain(cell, water);
        }
    }

    private static void BuildSiteWalls(Map map, IEnumerable<IntVec3> cells) =>
        SpawnSiteThings(map, cells, _ =>
        {
            Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
            wall.SetFaction(Faction.OfPlayer);
            return wall;
        });

    private static void SpawnSiteThings(Map map, IEnumerable<IntVec3> cells, Func<IntVec3, Thing> make)
    {
        foreach (IntVec3 cell in cells.ToList())
        {
            // 生成天然岩石會把底下的地改成粗石地，還原時要換回來。
            _siteTerrain.Add((cell, cell.GetTerrain(map)));
            _siteBuilt.Add(GenSpawn.Spawn(make(cell), cell, map));
        }
    }

    private static void RestoreSite(Map map)
    {
        ClearBlueprints(map);
        foreach (Thing wall in _siteBuilt.Where(w => w.Spawned)) wall.Destroy();
        _siteBuilt.Clear();
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
