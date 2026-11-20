using System;
using System.Collections.Generic;
using System.Linq;
using DevTools.Testing;
using RimAgent.Game;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

internal sealed partial class MapWorldTests
{
    private static readonly string[] _multiCellFurniture = ["DoubleBed", "Table2x2c", "Table2x4c"];

    /// <summary>BuildingInfoMatchesVanillaDefs 中反覆使用的原版建築規格預期關鍵字，避免每次測試都重新建立陣列。</summary>
    private static readonly string[] _reactorExpectedText =
    [
        "Size: 6x7", "makes it 7x6", "Heavy support", "must not be under a roof", "Ship part"
    ];

    [Test]
    public void MineDesignationAddsAndRemoves()
    {
        if (TestHelpers.SkipWithoutMap(nameof(MineDesignationAddsAndRemoves))) return;
        Map map = Find.CurrentMap;
        IntVec3? candidate = map.AllCells
            .Where(c => c.GetFirstMineable(map) != null && map.designationManager.DesignationAt(c, DesignationDefOf.Mine) == null)
            .Cast<IntVec3?>()
            .FirstOrDefault();
        if (candidate == null)
        {
            TestHelpers.Skip(nameof(MineDesignationAddsAndRemoves), "no undesignated mineable cell");
            return;
        }
        IntVec3 cell = candidate.Value;
        var failures = new List<string>();
        try
        {
            CommandResult added = _world.AddDesignationOnCell(cell.x, cell.z, "Mine");
            if (!added.Success) failures.Add("add failed: " + added.Error);
            if (map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) == null) failures.Add($"no vanilla Mine designation at {cell} after add");

            CommandResult removed = _world.RemoveDesignationOnCell(cell.x, cell.z, "Mine");
            if (!removed.Success) failures.Add("remove failed: " + removed.Error);
            if (map.designationManager.DesignationAt(cell, DesignationDefOf.Mine) != null) failures.Add($"Mine designation still at {cell} after remove");
        }
        finally
        {
            Designation left = map.designationManager.DesignationAt(cell, DesignationDefOf.Mine);
            if (left != null) map.designationManager.RemoveDesignation(left);
        }
        TestHelpers.AssertNone(failures, "mine designation");
    }

    [Test]
    public void StockpileZoneAddsAndDeletes()
    {
        if (TestHelpers.SkipWithoutMap(nameof(StockpileZoneAddsAndDeletes))) return;
        Map map = Find.CurrentMap;
        int zonesBefore = map.zoneManager.AllZones.Count;
        var failures = new List<string>();

        // 越界的矩形整筆不做，也不留下空區域。
        CommandResult outside = _world.AddZone("stockpile", new CellRectSpec(-2, -2, 0, 0), null);
        if (outside.Success) failures.Add("out-of-bounds rect was accepted");
        if (map.zoneManager.AllZones.Count != zonesBefore) failures.Add("out-of-bounds rect left a zone behind");

        var designator = new Designator_ZoneAddStockpile_Resources();
        CellRect? found = FindFreeRect(map, 3, c => map.zoneManager.ZoneAt(c) == null && designator.CanDesignateCell(c).Accepted);
        if (found == null)
        {
            TestHelpers.Skip(nameof(StockpileZoneAddsAndDeletes), "no free 3x3 area near the colonists");
            TestHelpers.AssertNone(failures, "stockpile zone");
            return;
        }
        CellRect rect = found.Value;
        Zone zone = null;
        try
        {
            CommandResult added = _world.AddZone("stockpile", new CellRectSpec(rect.minX, rect.minZ, rect.maxX, rect.maxZ), null);
            if (!added.Success) failures.Add("add failed: " + added.Error);
            zone = map.zoneManager.ZoneAt(rect.CenterCell);
            if (zone is not Zone_Stockpile) failures.Add($"vanilla zone at {rect.CenterCell} is {zone?.GetType().Name ?? "null"}");
            else if (rect.Cells.Any(c => map.zoneManager.ZoneAt(c) != zone) || zone.CellCount != rect.Area)
                failures.Add($"zone covers {zone.CellCount} cells, expected exactly {rect}");

            if (zone != null)
            {
                CheckZoneCanBeDeleted(zone, rect, map, failures);
            }
        }
        finally
        {
            if (zone != null && map.zoneManager.AllZones.Contains(zone)) zone.Delete();
        }
        TestHelpers.AssertNone(failures, "stockpile zone");
    }

    private void CheckZoneCanBeDeleted(Zone zone, CellRect rect, Map map, List<string> failures)
    {
        CommandResult deleted = _world.DeleteZone(zone.ID);
        if (!deleted.Success) failures.Add("delete failed: " + deleted.Error);
        if (map.zoneManager.AllZones.Contains(zone) || rect.Cells.Any(c => map.zoneManager.ZoneAt(c) != null))
            failures.Add("zone still present after delete");
    }

    [Test]
    public void WallBlueprintIsPlacedWithDefaultStuff()
    {
        if (TestHelpers.SkipWithoutMap(nameof(WallBlueprintIsPlacedWithDefaultStuff))) return;
        Map map = Find.CurrentMap;
        ThingDef wall = ThingDefOf.Wall;
        ThingDef stuff = GenStuff.DefaultStuffFor(wall);
        CellRect? found = FindFreeRect(map, 1, c => GenConstruct.CanPlaceBlueprintAt(wall, c, Rot4.North, map, false, null, null, stuff).Accepted);
        if (found == null)
        {
            TestHelpers.Skip(nameof(WallBlueprintIsPlacedWithDefaultStuff), "no cell accepts a wall blueprint");
            return;
        }
        IntVec3 cell = found.Value.CenterCell;
        var before = new HashSet<Thing>(cell.GetThingList(map));
        var failures = new List<string>();
        try
        {
            CommandResult result = _world.TryPlaceBlueprint(wall.defName, cell.x, cell.z, 0, null);
            if (!result.Success) failures.Add(CommandFailed + result.Error);
            Blueprint_Build blueprint = cell.GetThingList(map).OfType<Blueprint_Build>().FirstOrDefault(b => b.def.entityDefToBuild == wall);
            if (blueprint == null) failures.Add($"no vanilla wall blueprint at {cell}");
            else if (blueprint.stuffToUse != stuff) failures.Add($"blueprint stuff {blueprint.stuffToUse?.defName}, expected {stuff?.defName}");
        }
        finally
        {
            // 藍圖、框架或上帝模式下直接蓋好的牆都要拆掉，別留給後面的測試。
            List<Thing> placed = [.. cell.GetThingList(map).Where(t => !before.Contains(t) && (t.def == wall || t.def.entityDefToBuild == wall))];
            foreach (Thing thing in placed)
            {
                if (!thing.Destroyed) thing.Destroy();
            }
        }
        TestHelpers.AssertNone(failures, "place_blueprint");
    }

    /// <summary>
    /// 多格建築的藍圖：place_blueprint 回報的佔格與原版藍圖的 OccupiedRect 一致（四個朝向都驗）；
    /// 再往它身上放別的建築時，失敗原因列出占用者的 id 與範圍。結束後拆掉藍圖。
    /// </summary>
    [Test]
    public void PlaceBlueprintReportsMultiCellFootprint()
    {
        const string name = nameof(PlaceBlueprintReportsMultiCellFootprint);
        if (TestHelpers.SkipWithoutMap(name)) return;
        Map map = Find.CurrentMap;
        _world.TakeNewGameErrors();
        // 不需研發、至少 2x2 的家具；雙人床與桌子都是。
        ThingDef def = _multiCellFurniture
            .Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n))
            .FirstOrDefault(d => d != null && d.BuildableByPlayer && d.IsResearchFinished && d.size.x >= 2 && d.size.z >= 2);
        if (def == null)
        {
            TestHelpers.Skip(name, "no researched multi-cell furniture def");
            return;
        }
        ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
        // 空地 6x6：中心放得下任何朝向的雙人床／桌子，四周還有餘格可比對「旁邊不受影響」。
        CellRect? found = FindFreeRect(map, 6, c => !c.Fogged(map) && c.Standable(map) && c.GetThingList(map).All(t => t is Plant && !t.def.plant.IsTree));
        if (found == null)
        {
            TestHelpers.Skip(name, "no empty visible 6x6 area");
            return;
        }
        CellRect free = found.Value;
        IntVec3 center = free.CenterCell;
        if (!GenConstruct.CanPlaceBlueprintAt(def, center, Rot4.North, map, false, null, null, stuff).Accepted)
        {
            TestHelpers.Skip(name, $"{def.defName} cannot be placed at the free area's center");
            return;
        }

        var build = new RimAgent.Tools.BuildTools(_world);
        var failures = new List<string>();
        CellRect view = free.ExpandedBy(1).ClipInsideMap(map);
        try
        {
            for (int rot = 0; rot < 4; rot++)
                CheckBlueprintRotation(build, def, map, center, free, rot, failures);
        }
        finally
        {
            foreach (Thing t in view.Cells.SelectMany(c => c.GetThingList(map)).OfType<Blueprint>().Distinct().ToList())
            {
                if (!t.Destroyed) t.Destroy();
            }
        }
        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "multi-cell blueprint footprint");
    }

    /// <summary>驗證單一朝向的多格藍圖：佔格描述、尺寸／旋轉文字，以及與牆重疊時的拒絕訊息。</summary>
    private void CheckBlueprintRotation(RimAgent.Tools.BuildTools build, ThingDef def, Map map, IntVec3 center, CellRect free, int rot, List<string> failures)
    {
        string text = build.PlaceBlueprint(def.defName, "single", center.x, center.z, center.x, center.z, rot, string.Empty);
        Blueprint_Build placed = free.Cells.SelectMany(c => c.GetThingList(map)).OfType<Blueprint_Build>().Distinct().FirstOrDefault();
        if (placed == null)
        {
            failures.Add($"rot {rot}: no blueprint was placed: {text}");
            return;
        }
        CellRect vanilla = placed.OccupiedRect();
        string wantRect = $"({vanilla.minX},{vanilla.minZ})-({vanilla.maxX},{vanilla.maxZ})";
        if (!text.Contains("covers " + wantRect)) failures.Add($"rot {rot}: reply should say 'covers {wantRect}': {text}");
        if (!text.Contains($"Size {vanilla.Width}x{vanilla.Height}, rotation {rot}")) failures.Add($"rot {rot}: reply should say 'Size {vanilla.Width}x{vanilla.Height}, rotation {rot}': {text}");

        // 往佔格左下角放一道牆：原版回「空間已被占用」，附上擋住的藍圖 id 與範圍。
        // 不能放同一件：CanReplace 讓同種藍圖直接取代舊的，那是原版的「改朝向」行為。
        CommandResult overlap = _world.TryPlaceBlueprint(ThingDefOf.Wall.defName, vanilla.minX, vanilla.minZ, 0, null);
        if (overlap.Success) failures.Add($"rot {rot}: an overlapping blueprint was accepted");
        else if (!overlap.Message.Contains("#" + placed.thingIDNumber) || !overlap.Message.Contains(wantRect))
            failures.Add($"rot {rot}: refusal should name #{placed.thingIDNumber} {wantRect}: {overlap.Message}");

        placed.Destroy();
    }

    /// <summary>
    /// 建築規格（lookup_def 的 Building 段）對照 ThingDef／TerrainDef 欄位：反應爐 6x7、需要 Heavy 地基、不能在屋頂下、是船艙零件；
    /// 並對照所有可建造項目的佔格公式（BuildTools.OccupiedRect）與原版 GenAdj.OccupiedRect：各種尺寸、四個朝向、偶數與奇數邊長都涵蓋。
    /// 不需要地圖，只讀 Def。
    /// </summary>
    [Test]
    public void BuildingInfoMatchesVanillaDefs()
    {
        var failures = new List<string>();
        ThingDef reactor = DefDatabase<ThingDef>.GetNamedSilentFail("Ship_Reactor");
        if (reactor == null)
        {
            TestHelpers.Skip(nameof(BuildingInfoMatchesVanillaDefs), "Ship_Reactor def not found");
            return;
        }

        BuildingInfo info = _world.GetBuildingInfo("Ship_Reactor");
        if (info == null)
        {
            Expect.IsNotNull(info, "GetBuildingInfo(Ship_Reactor) returned null");
            return;
        }
        CheckReactorInfo(info, reactor, failures);

        string text = new RimAgent.Tools.BuildTools(_world).LookupDef("Ship_Reactor");
        failures.AddRange(_reactorExpectedText
            .Where(want => !text.Contains(want))
            .Select(want => $"tool text lacks '{want}': {text}"));

        CheckMiscBuildingInfo(failures);
        CheckOccupiedRect(failures);
        TestHelpers.AssertNone(failures, "building info vs vanilla defs");
    }

    /// <summary>檢查 Ship_Reactor 的建築規格是否與原版 Def 一致。</summary>
    private static void CheckReactorInfo(BuildingInfo info, ThingDef reactor, List<string> failures)
    {
        if (info.Width != reactor.size.x || info.Height != reactor.size.z || info.Width != 6 || info.Height != 7) failures.Add($"size {info.Width}x{info.Height}, def {reactor.size}");
        if (info.TerrainAffordance != reactor.terrainAffordanceNeeded.defName || info.TerrainAffordance != "Heavy") failures.Add("affordance " + info.TerrainAffordance);
        if (!info.ShipPart || !reactor.building.shipPart) failures.Add("ship part flag");
        if (!info.PlaceWorkers.Contains("PlaceWorker_NotUnderRoof")) failures.Add("place workers: " + string.Join(",", info.PlaceWorkers));
        if (info.ConstructionSkill != reactor.constructionSkillPrerequisite) failures.Add("construction skill " + info.ConstructionSkill);
        // 主選單那輪沒有遊戲，研究進度讀不到，一律視為未完成。
        bool playing = Current.ProgramState == ProgramState.Playing;
        if (info.Research.Count != reactor.researchPrerequisites.Count || info.Research.Zip(reactor.researchPrerequisites, (i, r) => i.Done != (playing && r.IsFinished)).Any(x => x)) failures.Add("research prerequisites");
        if (info.Costs.Sum(c => c.Count) != reactor.costList.Sum(c => c.count) || info.Costs.Count != reactor.costList.Count) failures.Add("costs");
        if (Math.Abs(info.PowerWatts - reactor.GetCompProperties<CompProperties_Power>().PowerConsumption) > 0.01f) failures.Add("power " + info.PowerWatts);
    }

    /// <summary>檢查互動格、材料成本、地板旗標與不存在的建築定義。</summary>
    private void CheckMiscBuildingInfo(List<string> failures)
    {
        // 有互動格的建築、要選材料的建築、地板：欄位與 Def 一致。
        ThingDef withCell = DefDatabase<ThingDef>.AllDefsListForReading.FirstOrDefault(d => d.BuildableByPlayer && d.HasSingleOrMultipleInteractionCells);
        if (withCell != null)
        {
            int expected = (withCell.hasInteractionCell ? 1 : 0) + (withCell.multipleInteractionCellOffsets?.Count ?? 0);
            if (_world.GetBuildingInfo(withCell.defName).InteractionOffsets.Count != expected) failures.Add($"interaction cells of {withCell.defName}");
        }
        ThingDef wall = ThingDefOf.Wall;
        BuildingInfo wallInfo = _world.GetBuildingInfo(wall.defName);
        if (wallInfo.StuffCount != wall.CostStuffCount || wallInfo.StuffCategories.Count != wall.stuffCategories.Count) failures.Add("stuff cost of Wall");
        TerrainDef floor = DefDatabase<TerrainDef>.AllDefsListForReading.FirstOrDefault(t => t.BuildableByPlayer);
        if (floor != null && !_world.GetBuildingInfo(floor.defName).IsTerrain) failures.Add("terrain flag of " + floor.defName);
        if (_world.GetBuildingInfo("NoSuchBuildableDef") != null) failures.Add("unknown def should return null");
    }

    /// <summary>將可建造項目的佔格公式與原版 GenAdj.OccupiedRect 做四個朝向的比對。</summary>
    private void CheckOccupiedRect(List<string> failures)
    {
        var origin = new IntVec3(50, 0, 60);
        foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading.Where(x => x.BuildableByPlayer).GroupBy(x => x.size).Select(g => g.First()))
        {
            BuildingInfo di = _world.GetBuildingInfo(d.defName);
            for (int rot = 0; rot < 4; rot++)
            {
                CellRect vanilla = GenAdj.OccupiedRect(origin, new Rot4(rot), d.size);
                RimAgent.Game.CellRectSpec mine = RimAgent.Tools.BuildTools.OccupiedRect(origin.x, origin.z, rot, di);
                if (mine.MinX != vanilla.minX || mine.MinZ != vanilla.minZ || mine.MaxX != vanilla.maxX || mine.MaxZ != vanilla.maxZ)
                    failures.Add($"{d.defName} {d.size} rot {rot}: ours {mine}, vanilla ({vanilla.minX},{vanilla.minZ})-({vanilla.maxX},{vanilla.maxZ})");
            }
        }
    }

    /// <summary>lookup_def 對原版 Def 的知識內容（見 LookupDefCheck）。</summary>
    [Test]
    public void LookupDefReportsVanillaKnowledge()
    {
        TestHelpers.AssertNone(LookupDefCheck.Run(_world, logReplies: false), "lookup_def vs vanilla defs");
    }
}
