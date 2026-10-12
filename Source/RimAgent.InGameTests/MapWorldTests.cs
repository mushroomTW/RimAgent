using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimAgent.Game;
using RimTestRedux;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimAgent.InGameTests
{
    /// <summary>
    /// 正式 <see cref="RimWorldGameWorld"/> 在真實地圖上的行為（它被排除在 headless 覆蓋率之外，只能在遊戲內驗）：
    /// 查詢對照原版資料，命令做完以原版 API 讀回、再復原現場。
    /// </summary>
    [TestSuite]
    internal static class MapWorldTests
    {
        private static RimWorldGameWorld _world;

        [BeforeEach]
        public static void SetUp()
        {
            _world = new RimWorldGameWorld();
        }

        [Test]
        public static void ReadyWhilePlaying()
        {
            if (TestHelpers.SkipWithoutMap(nameof(ReadyWhilePlaying))) return;
            string reason = _world.GetNotReadyReason();
            if (reason != null) throw new AssertionException("GetNotReadyReason returned: " + reason);
            Assert.That(RimAgentGameComponent.Instance != null).Is.True();
        }

        [Test]
        public static void ColonistsMatchVanillaFreeColonists()
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
        public static void EveryQueryRunsCleanOnRealMap()
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
                Query("GetMapGrid", () => _world.GetMapGrid(new CellRectSpec(center.x - 2, center.z - 2, center.x + 2, center.z + 2))),
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

            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "queries that failed on the real map");
        }

        [Test]
        public static void SetWorkPriorityWritesVanillaSetting()
        {
            if (TestHelpers.SkipWithoutMap(nameof(SetWorkPriorityWritesVanillaSetting))) return;
            Pawn pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.workSettings != null && p.workSettings.EverWork);
            WorkTypeDef workType = pawn == null ? null : DefDatabase<WorkTypeDef>.AllDefsListForReading.FirstOrDefault(w => w.visible && !pawn.WorkTypeIsDisabled(w));
            if (workType == null)
            {
                TestHelpers.Skip(nameof(SetWorkPriorityWritesVanillaSetting), "no colonist with an enabled work type");
                return;
            }

            // GetPriority 在手動優先級關閉時把 1..4 都回報成 3；復原要用存著的原始值。
            DefMap<WorkTypeDef, int> raw = Traverse.Create(pawn.workSettings).Field("priorities").GetValue<DefMap<WorkTypeDef, int>>();
            int original = raw[workType];
            bool originalManual = Current.Game.playSettings.useWorkPriorities;
            int target = original == 1 ? 2 : 1;
            List<Pawn> others = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Where(p => p != pawn && p.workSettings != null).ToList();
            var failures = new List<string>();
            try
            {
                // 先關掉手動優先級，讓命令一定走「開啟」那條路；清掉其他殖民者的工作順序髒標記，看命令有沒有通知到他們。
                Current.Game.playSettings.useWorkPriorities = false;
                foreach (Pawn other in others) Traverse.Create(other.workSettings).Field("workGiversDirty").SetValue(false);

                CommandResult result = _world.SetWorkPriority(pawn.thingIDNumber, workType.defName, target);
                if (!result.Success) failures.Add("command failed: " + result.Error);
                if (!Current.Game.playSettings.useWorkPriorities) failures.Add("manual priorities were not turned on for priority " + target);
                if (pawn.workSettings.GetPriority(workType) != target)
                    failures.Add($"{pawn.LabelShort} {workType.defName}: vanilla priority {pawn.workSettings.GetPriority(workType)}, expected {target}");
                foreach (Pawn other in others.Where(o => !Traverse.Create(o.workSettings).Field("workGiversDirty").GetValue<bool>()))
                    failures.Add($"{other.LabelShort}: work order cache not refreshed after manual priorities turned on");
            }
            finally
            {
                pawn.workSettings.SetPriority(workType, original);
                Current.Game.playSettings.useWorkPriorities = originalManual;
                RimWorldGameWorld.NotifyUseWorkPrioritiesChanged();
            }
            TestHelpers.AssertNone(failures, "set_work_priority");
        }

        [Test]
        public static void SetTimeAssignmentWritesVanillaTimetable()
        {
            if (TestHelpers.SkipWithoutMap(nameof(SetTimeAssignmentWritesVanillaTimetable))) return;
            Pawn pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.timetable != null);
            if (pawn == null)
            {
                TestHelpers.Skip(nameof(SetTimeAssignmentWritesVanillaTimetable), "no colonist with a timetable");
                return;
            }
            const int hour = 0;
            TimeAssignmentDef original = pawn.timetable.GetAssignment(hour);
            TimeAssignmentDef target = DefDatabase<TimeAssignmentDef>.AllDefsListForReading.First(d => d != original);
            var failures = new List<string>();
            try
            {
                CommandResult result = _world.SetTimeAssignment(pawn.thingIDNumber, hour, target.defName);
                if (!result.Success) failures.Add("command failed: " + result.Error);
                if (pawn.timetable.GetAssignment(hour) != target)
                    failures.Add($"hour {hour}: vanilla {pawn.timetable.GetAssignment(hour)?.defName}, expected {target.defName}");
            }
            finally
            {
                pawn.timetable.SetAssignment(hour, original);
            }
            TestHelpers.AssertNone(failures, "set_time_assignment");
        }

        [Test]
        public static void MineDesignationAddsAndRemoves()
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
        public static void StockpileZoneAddsAndDeletes()
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
                if (!(zone is Zone_Stockpile)) failures.Add($"vanilla zone at {rect.CenterCell} is {zone?.GetType().Name ?? "null"}");
                else if (rect.Cells.Any(c => map.zoneManager.ZoneAt(c) != zone) || zone.CellCount != rect.Area)
                    failures.Add($"zone covers {zone.CellCount} cells, expected exactly {rect}");

                if (zone != null)
                {
                    CommandResult deleted = _world.DeleteZone(zone.ID);
                    if (!deleted.Success) failures.Add("delete failed: " + deleted.Error);
                    if (map.zoneManager.AllZones.Contains(zone) || rect.Cells.Any(c => map.zoneManager.ZoneAt(c) != null))
                        failures.Add("zone still present after delete");
                }
            }
            finally
            {
                if (zone != null && map.zoneManager.AllZones.Contains(zone)) zone.Delete();
            }
            TestHelpers.AssertNone(failures, "stockpile zone");
        }

        [Test]
        public static void WallBlueprintIsPlacedWithDefaultStuff()
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
                if (!result.Success) failures.Add("command failed: " + result.Error);
                Blueprint_Build blueprint = cell.GetThingList(map).OfType<Blueprint_Build>().FirstOrDefault(b => b.def.entityDefToBuild == wall);
                if (blueprint == null) failures.Add($"no vanilla wall blueprint at {cell}");
                else if (blueprint.stuffToUse != stuff) failures.Add($"blueprint stuff {blueprint.stuffToUse?.defName}, expected {stuff?.defName}");
            }
            finally
            {
                // 藍圖、框架或上帝模式下直接蓋好的牆都要拆掉，別留給後面的測試。
                List<Thing> placed = cell.GetThingList(map)
                    .Where(t => !before.Contains(t) && (t.def == wall || t.def.entityDefToBuild == wall))
                    .ToList();
                foreach (Thing thing in placed)
                {
                    if (!thing.Destroyed) thing.Destroy();
                }
            }
            TestHelpers.AssertNone(failures, "place_blueprint");
        }

        /// <summary>
        /// view_map_grid 的兩個圖層逐格對照原版資料：基準不走轉接層的分類，直接用迷霧格、spawn 的 pawn、
        /// 邊緣建築（GetEdifice）、屋頂格與物品清單算出「該是什麼字元」。地圖上沒有的種類（沒牆、沒門、沒岩石）
        /// 就沒有該項的樣本，記在 log 的樣本數裡，不算失敗。
        /// </summary>
        [Test]
        public static void MapGridMatchesVanillaCells()
        {
            if (TestHelpers.SkipWithoutMap(nameof(MapGridMatchesVanillaCells))) return;
            Map map = Find.CurrentMap;
            _world.TakeNewGameErrors();
            Pawn anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            CellRect rect = CellRect.CenteredOn(anchor?.Position ?? map.Center, 12).ClipInsideMap(map);
            var tool = new RimAgent.Tools.MapGridTools(_world);
            var failures = new List<string>();
            var samples = new Dictionary<string, int>();

            // 地圖層
            Dictionary<int, string> rows = ParseGridRows(tool.ViewMapGrid(rect.minX, rect.minZ, rect.maxX, rect.maxZ), failures);
            foreach (IntVec3 c in rect.Cells)
            {
                char shown = GlyphAt(rows, rect, c);
                if (c.Fogged(map))
                {
                    Expect(failures, samples, "fog", c, shown, '?');
                    continue;
                }
                List<Thing> things = c.GetThingList(map);
                List<Pawn> pawns = things.OfType<Pawn>().ToList();
                Building edifice = c.GetEdifice(map);
                if (pawns.Count > 0)
                {
                    char want = pawns.Any(p => p.HostileTo(Faction.OfPlayer)) ? '!' : pawns.Any(p => p.IsColonist) ? '@' : 'o';
                    Expect(failures, samples, "pawn " + want, c, shown, want);
                }
                else if (edifice != null && edifice.def.IsWall) Expect(failures, samples, "wall", c, shown, '#');
                else if (edifice != null && edifice.def.mineable) Expect(failures, samples, "rock", c, shown, '^');
                else if (edifice != null && edifice.def.IsDoor) Expect(failures, samples, "door", c, shown, '+');
                else if (things.Count > 0 && things.All(t => t.def.category == ThingCategory.Item)) Expect(failures, samples, "item", c, shown, 'i');
            }
            if (anchor != null)
            {
                char colonist = GlyphAt(rows, rect, anchor.Position);
                if (colonist != '@' && colonist != '!') failures.Add($"colonist {anchor.LabelShort} at {anchor.Position} is drawn as '{colonist}'");
            }

            // 屋頂層：牆與門當參照，其餘照屋頂格。
            Dictionary<int, string> roofRows = ParseGridRows(tool.ViewMapGrid(rect.minX, rect.minZ, rect.maxX, rect.maxZ, "roof"), failures);
            foreach (IntVec3 c in rect.Cells)
            {
                char shown = GlyphAt(roofRows, rect, c);
                Building edifice = c.GetEdifice(map);
                RoofDef roof = c.GetRoof(map);
                char want = c.Fogged(map) ? '?'
                    : edifice != null && edifice.def.IsWall ? '#'
                    : edifice != null && edifice.def.IsDoor ? '+'
                    : roof == null ? '.' : !roof.isNatural ? 'r' : roof.isThickRoof ? 'M' : 'n';
                Expect(failures, samples, "roof " + want, c, shown, want);
            }

            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            Log.Message($"{TestHelpers.LogPrefix}MapGridMatchesVanillaCells: rect {rect}, samples {string.Join(", ", samples.Select(kv => kv.Key + " x" + kv.Value))}");
            TestHelpers.AssertNone(failures, "view_map_grid differs from vanilla cells");
        }

        /// <summary>
        /// build 圖層對照原版：迷霧為 ?；格上有任何擋住蓋建築的東西為 #（判準直接用原版 CanPlaceBlueprintOver，
        /// 以船艙反應爐當探針：建築、藍圖、框架、天然岩石會擋，電線、地板藍圖、植物、物品不會）；其餘依地形（有地基時看地基）的
        /// affordances 取最強的 H／m／l，三種都沒有為 ~。quicktest 地圖可能全是同一種地形，沒有的字元就沒有樣本，不算失敗。
        /// </summary>
        [Test]
        public static void MapGridBuildLayerMatchesVanillaAffordances()
        {
            if (TestHelpers.SkipWithoutMap(nameof(MapGridBuildLayerMatchesVanillaAffordances))) return;
            Map map = Find.CurrentMap;
            _world.TakeNewGameErrors();
            Pawn anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            CellRect rect = CellRect.CenteredOn(anchor?.Position ?? map.Center, 12).ClipInsideMap(map);
            var tool = new RimAgent.Tools.MapGridTools(_world);
            var failures = new List<string>();
            var samples = new Dictionary<string, int>();
            ThingDef probe = DefDatabase<ThingDef>.GetNamedSilentFail("Ship_Reactor");
            if (probe == null)
            {
                TestHelpers.Skip(nameof(MapGridBuildLayerMatchesVanillaAffordances), "Ship_Reactor def not found");
                return;
            }

            Dictionary<int, string> rows = ParseGridRows(tool.ViewMapGrid(rect.minX, rect.minZ, rect.maxX, rect.maxZ, "build"), failures);
            foreach (IntVec3 c in rect.Cells)
            {
                char shown = GlyphAt(rows, rect, c);
                if (c.Fogged(map))
                {
                    Expect(failures, samples, "fog", c, shown, '?');
                    continue;
                }
                bool blocked = c.GetThingList(map).Any(t => !GenConstruct.CanPlaceBlueprintOver(probe, t.def, null, t.Stuff));
                if (blocked)
                {
                    Expect(failures, samples, "blocked", c, shown, '#');
                    continue;
                }
                TerrainDef foundation = map.terrainGrid.FoundationAt(c);
                List<TerrainAffordanceDef> affordances = (foundation ?? map.terrainGrid.TerrainAt(c)).affordances;
                char want = affordances.Contains(TerrainAffordanceDefOf.Heavy) ? 'H'
                    : affordances.Contains(TerrainAffordanceDefOf.Medium) ? 'm'
                    : affordances.Contains(TerrainAffordanceDefOf.Light) ? 'l' : '~';
                Expect(failures, samples, "terrain " + want, c, shown, want);
            }

            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            Log.Message($"{TestHelpers.LogPrefix}MapGridBuildLayerMatchesVanillaAffordances: rect {rect}, samples {string.Join(", ", samples.Select(kv => kv.Key + " x" + kv.Value))}");
            TestHelpers.AssertNone(failures, "view_map_grid build layer differs from vanilla affordances");
        }

        /// <summary>
        /// 多格建築的藍圖：build 圖層在它覆蓋的每一格都標 #，旁邊的空格不受影響；place_blueprint 回報的佔格與原版
        /// 藍圖的 OccupiedRect 一致（四個朝向都驗）；再往它身上放別的建築時，失敗原因列出占用者的 id 與範圍。結束後拆掉藍圖。
        /// </summary>
        [Test]
        public static void MapGridBuildLayerMarksMultiCellBlueprintAndPlaceBlueprintReportsFootprint()
        {
            const string name = nameof(MapGridBuildLayerMarksMultiCellBlueprintAndPlaceBlueprintReportsFootprint);
            if (TestHelpers.SkipWithoutMap(name)) return;
            Map map = Find.CurrentMap;
            _world.TakeNewGameErrors();
            // 不需研發、至少 2x2 的家具；雙人床與桌子都是。
            ThingDef def = new[] { "DoubleBed", "Table2x2c", "Table2x4c" }
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

            var grid = new RimAgent.Tools.MapGridTools(_world);
            var build = new RimAgent.Tools.BuildTools(_world);
            var failures = new List<string>();
            CellRect view = free.ExpandedBy(1).ClipInsideMap(map);
            try
            {
                for (int rot = 0; rot < 4; rot++)
                {
                    string text = build.PlaceBlueprint(def.defName, "single", center.x, center.z, center.x, center.z, rot, string.Empty);
                    Blueprint_Build placed = free.Cells.SelectMany(c => c.GetThingList(map)).OfType<Blueprint_Build>().Distinct().FirstOrDefault();
                    if (placed == null)
                    {
                        failures.Add($"rot {rot}: no blueprint was placed: {text}");
                        continue;
                    }
                    CellRect vanilla = placed.OccupiedRect();
                    string wantRect = $"({vanilla.minX},{vanilla.minZ})-({vanilla.maxX},{vanilla.maxZ})";
                    if (!text.Contains("covers " + wantRect)) failures.Add($"rot {rot}: reply should say 'covers {wantRect}': {text}");
                    if (!text.Contains($"Size {vanilla.Width}x{vanilla.Height}, rotation {rot}")) failures.Add($"rot {rot}: reply should say 'Size {vanilla.Width}x{vanilla.Height}, rotation {rot}': {text}");

                    Dictionary<int, string> rows = ParseGridRows(grid.ViewMapGrid(view.minX, view.minZ, view.maxX, view.maxZ, "build"), failures);
                    foreach (IntVec3 c in view.Cells)
                    {
                        char shown = GlyphAt(rows, view, c);
                        if (vanilla.Contains(c))
                        {
                            if (shown != '#') failures.Add($"rot {rot}: covered cell {c} drawn '{shown}', expected '#'");
                        }
                        else if (free.Contains(c) && shown == '#')
                        {
                            failures.Add($"rot {rot}: free cell {c} drawn '#'");
                        }
                    }

                    // 往佔格左下角放一道牆：原版回「空間已被占用」，附上擋住的藍圖 id 與範圍。
                    // 不能放同一件：CanReplace 讓同種藍圖直接取代舊的，那是原版的「改朝向」行為。
                    CommandResult overlap = _world.TryPlaceBlueprint(ThingDefOf.Wall.defName, vanilla.minX, vanilla.minZ, 0, null);
                    if (overlap.Success) failures.Add($"rot {rot}: an overlapping blueprint was accepted");
                    else if (!overlap.Message.Contains("#" + placed.thingIDNumber) || !overlap.Message.Contains(wantRect))
                        failures.Add($"rot {rot}: refusal should name #{placed.thingIDNumber} {wantRect}: {overlap.Message}");

                    placed.Destroy();
                }
            }
            finally
            {
                foreach (Thing t in view.Cells.SelectMany(c => c.GetThingList(map)).OfType<Blueprint>().Distinct().ToList())
                {
                    if (!t.Destroyed) t.Destroy();
                }
            }
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "multi-cell blueprint footprint");
        }

        /// <summary>
        /// get_building_info 的資料對照 ThingDef／TerrainDef 欄位：反應爐 6x7、需要 Heavy 地基、不能在屋頂下、是船艙零件；
        /// 並對照所有可建造項目的佔格公式（BuildTools.OccupiedRect）與原版 GenAdj.OccupiedRect：各種尺寸、四個朝向、偶數與奇數邊長都涵蓋。
        /// 不需要地圖，只讀 Def。
        /// </summary>
        [Test]
        public static void BuildingInfoMatchesVanillaDefs()
        {
            var failures = new List<string>();
            ThingDef reactor = DefDatabase<ThingDef>.GetNamedSilentFail("Ship_Reactor");
            if (reactor == null)
            {
                TestHelpers.Skip(nameof(BuildingInfoMatchesVanillaDefs), "Ship_Reactor def not found");
                return;
            }

            BuildingInfo info = _world.GetBuildingInfo("Ship_Reactor");
            if (info == null) throw new AssertionException("GetBuildingInfo(Ship_Reactor) returned null");
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

            string text = new RimAgent.Tools.BuildTools(_world).GetBuildingInfo("Ship_Reactor");
            foreach (string want in new[] { "Size: 6x7", "makes it 7x6", "Heavy support", "must not be under a roof", "Ship part" })
            {
                if (!text.Contains(want)) failures.Add($"tool text lacks '{want}': {text}");
            }

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

            // 佔格公式對照原版：每種尺寸取一個可建造的 ThingDef，四個朝向。
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
            TestHelpers.AssertNone(failures, "get_building_info vs vanilla defs");
        }

        /// <summary>藍圖在字元網格裡是 b，不是成品；拆掉後那一格回到原樣。</summary>
        [Test]
        public static void MapGridDrawsPlacedBlueprint()
        {
            if (TestHelpers.SkipWithoutMap(nameof(MapGridDrawsPlacedBlueprint))) return;
            Map map = Find.CurrentMap;
            _world.TakeNewGameErrors();
            ThingDef wall = ThingDefOf.Wall;
            ThingDef stuff = GenStuff.DefaultStuffFor(wall);
            // 只挑什麼都沒有、看得到的格子：藍圖不會被別的圖層蓋掉。
            CellRect? found = FindFreeRect(map, 1, c => !c.Fogged(map) && c.GetThingList(map).Count == 0
                && GenConstruct.CanPlaceBlueprintAt(wall, c, Rot4.North, map, false, null, null, stuff).Accepted);
            if (found == null)
            {
                TestHelpers.Skip(nameof(MapGridDrawsPlacedBlueprint), "no empty visible cell accepts a wall blueprint");
                return;
            }
            IntVec3 cell = found.Value.CenterCell;
            var tool = new RimAgent.Tools.MapGridTools(_world);
            var before = new HashSet<Thing>(cell.GetThingList(map));
            var failures = new List<string>();
            try
            {
                CellRect view = CellRect.CenteredOn(cell, 1).ClipInsideMap(map);
                char beforeGlyph = GlyphAt(ParseGridRows(tool.ViewMapGrid(view.minX, view.minZ, view.maxX, view.maxZ), failures), view, cell);
                if (beforeGlyph == 'b') failures.Add($"{cell} already drawn as a blueprint before placing one");

                CommandResult result = _world.TryPlaceBlueprint(wall.defName, cell.x, cell.z, 0, null);
                if (!result.Success) failures.Add("command failed: " + result.Error);
                char planned = GlyphAt(ParseGridRows(tool.ViewMapGrid(view.minX, view.minZ, view.maxX, view.maxZ), failures), view, cell);
                if (planned != 'b') failures.Add($"wall blueprint at {cell} is drawn as '{planned}', expected 'b'");
            }
            finally
            {
                foreach (Thing thing in cell.GetThingList(map).Where(t => !before.Contains(t) && (t.def == wall || t.def.entityDefToBuild == wall)).ToList())
                {
                    if (!thing.Destroyed) thing.Destroy();
                }
            }
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "view_map_grid blueprint");
        }

        /// <summary>
        /// quicktest 地圖多半是空地，取樣碰不到牆、門、岩石與屋頂；這裡在空地上直接生成這些東西、設屋頂，
        /// 確認兩個圖層畫出的字元，結束後拆掉並還原屋頂。
        /// </summary>
        [Test]
        public static void MapGridDrawsBuiltStructures()
        {
            if (TestHelpers.SkipWithoutMap(nameof(MapGridDrawsBuiltStructures))) return;
            Map map = Find.CurrentMap;
            _world.TakeNewGameErrors();
            ThingDef rockDef = DefDatabase<ThingDef>.GetNamedSilentFail("Granite");
            CellRect? found = FindFreeRect(map, 5, c => !c.Fogged(map) && c.Standable(map) && c.GetThingList(map).Count == 0
                && !map.roofGrid.Roofed(c) && c.GetZone(map) == null);
            if (found == null || rockDef == null)
            {
                TestHelpers.Skip(nameof(MapGridDrawsBuiltStructures), rockDef == null ? "no Granite def" : "no empty unroofed 5x5 area near the colonists");
                return;
            }
            CellRect rect = found.Value;
            IntVec3 wallCell = new IntVec3(rect.minX + 1, 0, rect.minZ + 1);
            IntVec3 doorCell = new IntVec3(rect.minX + 2, 0, rect.minZ + 1);
            IntVec3 rockCell = new IntVec3(rect.minX + 3, 0, rect.minZ + 3);
            IntVec3 builtRoofCell = new IntVec3(rect.minX + 1, 0, rect.minZ + 3);
            IntVec3 thickRoofCell = new IntVec3(rect.minX + 2, 0, rect.minZ + 3);
            var spawned = new List<Thing>();
            var tool = new RimAgent.Tools.MapGridTools(_world);
            var failures = new List<string>();
            try
            {
                spawned.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, GenStuff.DefaultStuffFor(ThingDefOf.Wall)), wallCell, map));
                spawned.Add(GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Door, GenStuff.DefaultStuffFor(ThingDefOf.Door)), doorCell, map));
                spawned.Add(GenSpawn.Spawn(ThingMaker.MakeThing(rockDef), rockCell, map));
                IntVec3 sandbagCell = new IntVec3(rect.minX + 3, 0, rect.minZ + 1);
                IntVec3 turretCell = new IntVec3(rect.minX + 1, 0, rect.minZ + 2);
                ThingDef sandbags = DefDatabase<ThingDef>.GetNamedSilentFail("Sandbags");
                ThingDef turret = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_MiniTurret");
                if (sandbags != null) spawned.Add(GenSpawn.Spawn(ThingMaker.MakeThing(sandbags, GenStuff.DefaultStuffFor(sandbags)), sandbagCell, map));
                if (turret != null)
                {
                    Thing spawnedTurret = ThingMaker.MakeThing(turret, GenStuff.DefaultStuffFor(turret));
                    spawnedTurret.SetFaction(Faction.OfPlayer);
                    spawned.Add(GenSpawn.Spawn(spawnedTurret, turretCell, map));
                }
                map.roofGrid.SetRoof(builtRoofCell, RoofDefOf.RoofConstructed);
                map.roofGrid.SetRoof(thickRoofCell, RoofDefOf.RoofRockThick);

                Dictionary<int, string> mapRows = ParseGridRows(tool.ViewMapGrid(rect.minX, rect.minZ, rect.maxX, rect.maxZ), failures);
                ExpectGlyph(mapRows, rect, wallCell, '#', "wall", failures);
                ExpectGlyph(mapRows, rect, doorCell, '+', "door", failures);
                ExpectGlyph(mapRows, rect, rockCell, '^', "natural rock", failures);
                if (sandbags != null) ExpectGlyph(mapRows, rect, sandbagCell, 'c', "sandbags", failures);
                if (turret != null) ExpectGlyph(mapRows, rect, turretCell, 't', "mini-turret", failures);

                Dictionary<int, string> roofRows = ParseGridRows(tool.ViewMapGrid(rect.minX, rect.minZ, rect.maxX, rect.maxZ, "roof"), failures);
                ExpectGlyph(roofRows, rect, wallCell, '#', "wall on the roof layer", failures);
                ExpectGlyph(roofRows, rect, builtRoofCell, 'r', "constructed roof", failures);
                ExpectGlyph(roofRows, rect, thickRoofCell, 'M', "thick mountain roof", failures);
                ExpectGlyph(roofRows, rect, new IntVec3(rect.maxX, 0, rect.maxZ), '.', "unroofed cell", failures);
            }
            finally
            {
                foreach (Thing thing in spawned.Where(t => !t.Destroyed)) thing.Destroy();
                map.roofGrid.SetRoof(builtRoofCell, null);
                map.roofGrid.SetRoof(thickRoofCell, null);
            }
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "view_map_grid built structures");
        }

        /// <summary>
        /// 飛船結局的機制路徑（不發射）：在空地生成一整艘船，電腦核心的發射鈕要帶停用原因、反應爐要有啟動鈕，
        /// 零件的發射報告鈕經 use_gizmo 開出對話框並列出完整原因，模型才讀得到。結束後拆掉並關掉對話框。
        /// </summary>
        [Test]
        public static void ShipPartsExposeStartupLaunchAndReport()
        {
            if (TestHelpers.SkipWithoutMap(nameof(ShipPartsExposeStartupLaunchAndReport))) return;
            Map map = Find.CurrentMap;
            _world.TakeNewGameErrors();
            // 由西到東排成一列、底邊對齊，相鄰兩件一定以東西向相接。
            ThingDef[] parts =
            {
                ThingDefOf.Ship_Reactor, ThingDefOf.Ship_Engine, ThingDefOf.Ship_Engine, ThingDefOf.Ship_Engine,
                ThingDefOf.Ship_ComputerCore, ThingDefOf.Ship_SensorCluster, ThingDefOf.Ship_CryptosleepCasket, ThingDefOf.Ship_Beam
            };
            int width = parts.Sum(p => p.size.x);
            int height = parts.Max(p => p.size.z);
            // 整艘船很大，殖民者附近常常放不下；全圖每 4 格試一個角落。
            Predicate<IntVec3> clear = c => !c.Fogged(map) && c.Standable(map) && !map.roofGrid.Roofed(c)
                && c.GetEdifice(map) == null && c.GetThingList(map).All(t => t is Plant);
            CellRect? found = null;
            // 離地圖邊緣留一格：原版 ShipUtility.ShipBuildingsAttachedTo 會讀零件外圍的格子，不檢查是否在圖內。
            for (int cx = 1; found == null && cx + width < map.Size.x - 1; cx += 4)
            {
                for (int cz = 1; cz + height < map.Size.z - 1; cz += 4)
                {
                    var candidate = new CellRect(cx, cz, width, height);
                    if (candidate.Cells.All(c => clear(c))) { found = candidate; break; }
                }
            }
            if (found == null)
            {
                TestHelpers.Skip(nameof(ShipPartsExposeStartupLaunchAndReport), $"no clear unroofed {width}x{height} area near the colonists");
                return;
            }
            var spawned = new List<Building>();
            var windowsBefore = new HashSet<Window>(Find.WindowStack.Windows);
            var failures = new List<string>();
            try
            {
                int x = found.Value.minX;
                foreach (ThingDef def in parts)
                {
                    var probe = new IntVec3(x, 0, found.Value.minZ);
                    CellRect occupied = GenAdj.OccupiedRect(probe, Rot4.North, def.size);
                    IntVec3 center = probe + (probe - new IntVec3(occupied.minX, 0, occupied.minZ));
                    Thing part = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    part.SetFaction(Faction.OfPlayer);
                    spawned.Add((Building)GenSpawn.Spawn(part, center, map, Rot4.North));
                    x += def.size.x;
                }
                Building core = spawned.First(b => b.def == ThingDefOf.Ship_ComputerCore);
                Building reactor = spawned.First(b => b.def == ThingDefOf.Ship_Reactor);
                List<string> vanillaReasons = ShipUtility.LaunchFailReasons(core).ToList();
                if (vanillaReasons.Any(r => r.StartsWith("ShipReportMissingPart".Translate(), StringComparison.Ordinal)))
                    failures.Add("test layout is not connected: " + string.Join("; ", vanillaReasons));

                string launchLabel = "CommandShipLaunch".Translate();
                GizmoInfo launch = _world.GetGizmos(core.thingIDNumber)?.FirstOrDefault(g => g.Label == launchLabel);
                if (launch == null) failures.Add("the computer core has no launch button");
                else if (!launch.Disabled || string.IsNullOrEmpty(launch.DisabledReason)) failures.Add("launch is not disabled with a reason before startup");

                string startupLabel = "CommandShipStartup".Translate();
                GizmoInfo startup = _world.GetGizmos(reactor.thingIDNumber)?.FirstOrDefault(g => g.Label == startupLabel);
                if (startup == null) failures.Add("the hibernating reactor has no startup button");
                else if (startup.Kind != GizmoInfo.ActionKind) failures.Add("startup button kind is " + startup.Kind);

                Building beam = spawned.First(b => b.def == ThingDefOf.Ship_Beam);
                string reportLabel = "CommandShipLaunchReport".Translate();
                GizmoInfo report = _world.GetGizmos(beam.thingIDNumber)?.FirstOrDefault(g => g.Label == reportLabel);
                if (report == null) failures.Add("the beam has no launch report button");
                else
                {
                    CommandResult pressed = _world.UseGizmo(beam.thingIDNumber, report.Number, report.Label, null);
                    if (!pressed.Success) failures.Add("pressing the launch report failed: " + pressed.Error);
                    string cannot = "ShipReportCannotLaunch".Translate();
                    if (!_world.GetPendingChoices().Any(c => c.Text.Contains(cannot)))
                        failures.Add("the launch report dialog is not listed among pending choices");
                }
            }
            finally
            {
                foreach (Window window in Find.WindowStack.Windows.Where(w => !windowsBefore.Contains(w)).ToList()) window.Close(false);
                foreach (Building part in spawned.Where(b => !b.Destroyed)) part.Destroy();
            }
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "ship parts");
        }

        private static void ExpectGlyph(Dictionary<int, string> rows, CellRect rect, IntVec3 cell, char expected, string what, List<string> failures)
        {
            char actual = GlyphAt(rows, rect, cell);
            if (actual != expected) failures.Add($"{what} at {cell} is drawn as '{actual}', expected '{expected}'");
        }

        /// <summary>把 view_map_grid 的輸出拆成 z → 該列字元（去掉 "z12 " 標籤）；格式不對記成失敗。</summary>
        private static Dictionary<int, string> ParseGridRows(string text, List<string> failures)
        {
            var rows = new Dictionary<int, string>();
            if (text.StartsWith("Error:", StringComparison.Ordinal)) failures.Add("view_map_grid: " + text);
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.TrimStart(new[] { ' ' });
                int space = trimmed.IndexOf(' ');
                if (space < 2 || trimmed[0] != 'z' || !int.TryParse(trimmed.Substring(1, space - 1), out int z)) continue;
                rows[z] = trimmed.Substring(space + 1);
            }
            return rows;
        }

        private static char GlyphAt(Dictionary<int, string> rows, CellRect rect, IntVec3 c)
        {
            return rows.TryGetValue(c.z, out string row) && c.x - rect.minX >= 0 && c.x - rect.minX < row.Length ? row[c.x - rect.minX] : '\0';
        }

        private static void Expect(List<string> failures, Dictionary<string, int> samples, string what, IntVec3 c, char shown, char want)
        {
            samples[what] = samples.TryGetValue(what, out int n) ? n + 1 : 1;
            if (shown != want && failures.Count < 20) failures.Add($"{what} at {c}: drawn '{shown}', expected '{want}'");
        }

        /// <summary>剛蓋好、還沒人認領的床要抵掉沒床的人：殖民者去睡覺前，排程器不該一直以為床不夠。</summary>
        [Test]
        public static void UnclaimedBedOffsetsBedlessColonists()
        {
            if (TestHelpers.SkipWithoutMap(nameof(UnclaimedBedOffsetsBedlessColonists))) return;
            Map map = Find.CurrentMap;
            int before = _world.GetAutonomousSignals().BedlessCount;
            if (before == 0)
            {
                TestHelpers.Skip(nameof(UnclaimedBedOffsetsBedlessColonists), "no colonist is short of a bed");
                return;
            }
            // 單人床 1x2、雙人床 2x2，朝北都放得進一塊 2x2。只挑完全沒東西的格子：
            // 生成建築預設會把格子上的植物、汙物直接刪掉，事後無法復原。
            CellRect? found = FindFreeRect(map, 2, c => c.Standable(map) && c.GetThingList(map).Count == 0);
            if (found == null)
            {
                TestHelpers.Skip(nameof(UnclaimedBedOffsetsBedlessColonists), "no empty 2x2 area near the colonists");
                return;
            }
            var cell = new IntVec3(found.Value.minX, 0, found.Value.minZ);
            var failures = new List<string>();
            CheckBed(map, cell, ThingDefOf.Bed, null, Math.Max(0, before - 1), before, "unclaimed single bed", failures);
            CheckBed(map, cell, ThingDefOf.Bed, b => b.Medical = true, before, before, "medical bed", failures);
            CheckBed(map, cell, ThingDefOf.Bed, b => b.ForPrisoners = true, before, before, "prisoner bed", failures);
            ThingDef doubleBed = DefDatabase<ThingDef>.GetNamedSilentFail("DoubleBed");
            if (doubleBed != null) CheckBed(map, cell, doubleBed, null, Math.Max(0, before - 2), before, "unclaimed double bed", failures);
            TestHelpers.AssertNone(failures, "bedless signal");
        }

        /// <summary>生成一張床（可再設定用途），比對沒床人數是否符合預期，再拆掉。</summary>
        private static void CheckBed(Map map, IntVec3 cell, ThingDef def, Action<Building_Bed> configure, int expected, int before,
            string label, List<string> failures)
        {
            var bed = (Building_Bed)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            bed.SetFaction(Faction.OfPlayer);
            try
            {
                GenSpawn.Spawn(bed, cell, map, Rot4.North);
                configure?.Invoke(bed);
                int after = _world.GetAutonomousSignals().BedlessCount;
                if (after != expected) failures.Add($"{label}: bedless {before} -> {after}, expected {expected}");
            }
            finally
            {
                if (!bed.Destroyed) bed.Destroy();
            }
        }

        [Test]
        public static void SetTimeSpeedWritesTickManager()
        {
            if (TestHelpers.SkipWithoutMap(nameof(SetTimeSpeedWritesTickManager))) return;
            TimeSpeed original = Find.TickManager.CurTimeSpeed;
            TimeSpeed target = original == TimeSpeed.Normal ? TimeSpeed.Fast : TimeSpeed.Normal;
            var failures = new List<string>();
            try
            {
                CommandResult result = _world.SetTimeSpeed(target.ToString());
                if (!result.Success) failures.Add("command failed: " + result.Error);
                if (Find.TickManager.CurTimeSpeed != target) failures.Add($"tick manager at {Find.TickManager.CurTimeSpeed}, expected {target}");
                if (_world.SetTimeSpeed("Ultrafast").Success) failures.Add("Ultrafast was accepted");
            }
            finally
            {
                Find.TickManager.CurTimeSpeed = original;
            }
            TestHelpers.AssertNone(failures, "set_time_speed");
        }

        /// <summary>
        /// 用原版產生一個需要玩家接受的任務（強盜營地或交易請求；物資藏匿點等 autoAccept 任務生成即接受，不適用），
        /// 走過列出、拒絕、取消拒絕、接受，再以原版欄位讀回；結束後移除任務。
        /// </summary>
        [Test]
        public static void QuestListDismissAndAccept()
        {
            if (TestHelpers.SkipWithoutMap(nameof(QuestListDismissAndAccept))) return;
            Quest quest = GenerateManualQuest("OpportunitySite_BanditCamp", "TradeRequest");
            if (quest == null)
            {
                TestHelpers.Skip(nameof(QuestListDismissAndAccept), "no manually accepted quest could be generated on this world");
                return;
            }
            var failures = new List<string>();
            try
            {
                QuestInfo info = _world.GetQuests().FirstOrDefault(q => q.Id == quest.id);
                if (info == null) failures.Add("generated quest is missing from GetQuests");
                else
                {
                    if (info.State != QuestInfo.AvailableState) failures.Add("state is " + info.State + ", expected available");
                    if (string.IsNullOrEmpty(info.Name)) failures.Add("empty name");
                    if (info.CanAccept != QuestUtility.CanAcceptQuest(quest).Accepted) failures.Add("CanAccept differs from vanilla");
                }

                CommandResult dismiss = _world.SetQuestDismissed(quest.id, true);
                if (!dismiss.Success || !quest.dismissed) failures.Add("dismiss did not set quest.dismissed: " + dismiss.Error);
                CommandResult restore = _world.SetQuestDismissed(quest.id, false);
                if (!restore.Success || quest.dismissed) failures.Add("restore did not clear quest.dismissed: " + restore.Error);

                if (QuestUtility.CanAcceptQuest(quest).Accepted)
                {
                    int accepter = quest.RequiresAccepter
                        ? PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended
                            .FirstOrDefault(p => QuestUtility.CanPawnAcceptQuest(p, quest))?.thingIDNumber ?? 0
                        : 0;
                    // 有獎勵選項的任務只能「以此獎勵接受」，選第一組。
                    int reward = quest.PartsListForReading.OfType<QuestPart_Choice>().Any() ? 1 : 0;
                    CommandResult accept = _world.AcceptQuest(quest.id, reward, accepter);
                    if (!accept.Success) failures.Add("accept failed: " + accept.Error);
                    else if (quest.State != QuestState.Ongoing) failures.Add("after accept the quest state is " + quest.State);
                    if (_world.AcceptQuest(quest.id, reward, accepter).Success) failures.Add("accepting twice succeeded");
                    if (!_world.GetQuests().Any(q => q.Id == quest.id && q.State == QuestInfo.ActiveState))
                        failures.Add("accepted quest is not listed as active");
                }
                else
                {
                    failures.Add("item stash quest cannot be accepted: " + QuestUtility.CanAcceptQuest(quest).Reason);
                }
            }
            finally
            {
                if (!quest.Historical) quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
                Find.QuestManager.Remove(quest);
            }
            TestHelpers.AssertNone(failures, "quests");
        }

        /// <summary>依序嘗試產生任務，取第一個生成後仍待玩家接受的；生成失敗或自動接受的會移除。</summary>
        private static Quest GenerateManualQuest(params string[] scripts)
        {
            foreach (string name in scripts)
            {
                QuestScriptDef script = DefDatabase<QuestScriptDef>.GetNamedSilentFail(name);
                if (script == null || script.autoAccept) continue;
                Quest quest;
                try
                {
                    quest = QuestUtility.GenerateQuestAndMakeAvailable(script, 500f);
                }
                catch (Exception ex)
                {
                    Log.Message($"{TestHelpers.LogPrefix}{name} could not be generated: {ex.Message}");
                    continue;
                }
                if (quest != null && quest.State == QuestState.NotYetAccepted) return quest;
                if (quest != null) Find.QuestManager.Remove(quest);
            }
            return null;
        }

        /// <summary>
        /// 迫擊砲的「指定射擊目標」是 Command_VerbTarget：列出時要標成 target，按下不給目標要被拒絕且收掉選目標狀態，
        /// 給範圍外的格子要回報打不到，給範圍內的格子要真的設成砲塔的強制目標（以原版 ForcedTarget 讀回）。
        /// </summary>
        [Test]
        public static void MortarGizmoTakesACellTarget()
        {
            if (TestHelpers.SkipWithoutMap(nameof(MortarGizmoTakesACellTarget))) return;
            Map map = Find.CurrentMap;
            ThingDef mortarDef = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_Mortar");
            CellRect? found = mortarDef == null ? null
                : FindFreeRect(map, 3, c => c.Standable(map) && c.GetThingList(map).Count == 0 && !map.roofGrid.Roofed(c));
            if (found == null)
            {
                TestHelpers.Skip(nameof(MortarGizmoTakesACellTarget), mortarDef == null ? "no Turret_Mortar def" : "no empty unroofed 3x3 area near the colonists");
                return;
            }
            var mortar = (Building_TurretGun)ThingMaker.MakeThing(mortarDef, GenStuff.DefaultStuffFor(mortarDef));
            mortar.SetFaction(Faction.OfPlayer);
            var failures = new List<string>();
            try
            {
                GenSpawn.Spawn(mortar, found.Value.CenterCell, map);
                string forceLabel = "CommandSetForceAttackTarget".Translate();
                GizmoInfo gizmo = _world.GetGizmos(mortar.thingIDNumber)?.FirstOrDefault(g => string.Equals(g.Label, forceLabel, StringComparison.OrdinalIgnoreCase));
                if (gizmo == null)
                {
                    failures.Add("the force-target gizmo is not listed");
                }
                else if (gizmo.Disabled)
                {
                    TestHelpers.Skip(nameof(MortarGizmoTakesACellTarget), "force-target gizmo is disabled here: " + gizmo.DisabledReason);
                }
                else
                {
                    CheckMortarTargets(map, mortar, gizmo, failures);
                }
            }
            finally
            {
                if (Find.Targeter.IsTargeting) Find.Targeter.StopTargeting();
                if (!mortar.Destroyed) mortar.Destroy();
            }
            TestHelpers.AssertNone(failures, "mortar force target");
        }

        private static void CheckMortarTargets(Map map, Building_TurretGun mortar, GizmoInfo gizmo, List<string> failures)
        {
            if (gizmo.Kind != GizmoInfo.TargetKind) failures.Add("kind is " + gizmo.Kind + ", expected target");
            if (string.IsNullOrEmpty(gizmo.TargetHint)) failures.Add("no target hint");

            CommandResult none = _world.UseGizmo(mortar.thingIDNumber, gizmo.Number, gizmo.Label, null);
            if (none.Success) failures.Add("pressing without a target succeeded");
            if (Find.Targeter.IsTargeting) failures.Add("targeter was left active after pressing without a target");
            if (mortar.ForcedTarget.IsValid) failures.Add("pressing without a target set a forced target");

            Verb verb = mortar.AttackVerb;
            IntVec3 pos = mortar.Position;
            IntVec3 farCorner = new[] { new IntVec3(0, 0, 0), new IntVec3(map.Size.x - 1, 0, 0), new IntVec3(0, 0, map.Size.z - 1), new IntVec3(map.Size.x - 1, 0, map.Size.z - 1) }
                .OrderByDescending(c => (c - pos).LengthHorizontal).First();
            // 小地圖整張都在迫擊砲射程內，就改用低於最小射程的近處格子測拒絕。
            IntVec3 bad = verb.CanHitTarget(farCorner)
                ? GenRadial.RadialCellsAround(pos, Math.Min(verb.verbProps.minRange, 20f), false)
                    .Where(c => c.InBounds(map) && (c - pos).LengthHorizontal >= 2f && !verb.CanHitTarget(c))
                    .DefaultIfEmpty(IntVec3.Invalid).First()
                : farCorner;
            if (!bad.IsValid)
            {
                Log.Message(TestHelpers.LogPrefix + "mortar can hit every cell tried; skipping the out-of-range check");
            }
            else
            {
                CommandResult far = _world.UseGizmo(mortar.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfCell(bad.x, bad.z));
                if (far.Success) failures.Add("a cell out of range was accepted");
                else if (!far.Message.Contains("cannot")) failures.Add("out-of-range reason is unclear: " + far.Message);
                if (mortar.ForcedTarget.IsValid) failures.Add("out-of-range target became the forced target");
            }

            // 迫擊砲射程遠超過 GenRadial 的半徑上限（約 80 格），改從全圖格子挑。
            float minDistance = verb.verbProps.minRange + 3f;
            float maxDistance = verb.EffectiveRange * 0.7f;
            IntVec3 cell = map.AllCells
                .Where(c => (c - pos).LengthHorizontal > minDistance && (c - pos).LengthHorizontal < maxDistance)
                .OrderBy(c => (c - pos).LengthHorizontalSquared)
                .Where(c => verb.CanHitTarget(c))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
            if (!cell.IsValid)
            {
                TestHelpers.Skip(nameof(MortarGizmoTakesACellTarget), "no cell in the mortar's range to aim at");
                return;
            }
            CommandResult hit = _world.UseGizmo(mortar.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfCell(cell.x, cell.z));
            if (!hit.Success) failures.Add("aiming at " + cell + " failed: " + hit.Error);
            else if (mortar.ForcedTarget.Cell != cell) failures.Add($"forced target is {mortar.ForcedTarget}, expected cell {cell}");
            if (Find.Targeter.IsTargeting) failures.Add("targeter was left active after aiming");
        }

        /// <summary>
        /// 使用物品的按鈕是 Command_Action，按下才開始選目標（CompUsable 當 ITargetingSource）：
        /// 不給目標要被拒絕並收掉選目標狀態；指一個物品或一格要被擋下；指一位用不了的殖民者要帶回遊戲的原因。
        /// 用治療機械血清（健康的殖民者用不了），不會真的用掉任何東西。
        /// </summary>
        [Test]
        public static void UseItemGizmoAsksForAndChecksAColonist()
        {
            if (TestHelpers.SkipWithoutMap(nameof(UseItemGizmoAsksForAndChecksAColonist))) return;
            Map map = Find.CurrentMap;
            ThingDef serumDef = DefDatabase<ThingDef>.GetNamedSilentFail("MechSerumHealer");
            CellRect? found = serumDef == null ? null : FindFreeRect(map, 1, c => c.Standable(map) && c.GetThingList(map).Count == 0);
            if (found == null)
            {
                TestHelpers.Skip(nameof(UseItemGizmoAsksForAndChecksAColonist), serumDef == null ? "no MechSerumHealer def" : "no empty cell near the colonists");
                return;
            }
            Thing serum = ThingMaker.MakeThing(serumDef);
            var failures = new List<string>();
            try
            {
                GenSpawn.Spawn(serum, found.Value.CenterCell, map);
                CompUsable usable = serum.TryGetComp<CompUsable>();
                string label = string.Format("{0} {1}...", "UseGizmo".Translate(), serumDef.label);
                GizmoInfo gizmo = _world.GetGizmos(serum.thingIDNumber)?.FirstOrDefault(g => string.Equals(g.Label, label, StringComparison.OrdinalIgnoreCase));
                if (usable == null || gizmo == null)
                {
                    failures.Add("the use gizmo is not listed");
                }
                else
                {
                    CheckUseItemTargets(map, serum, usable, gizmo, failures);
                }
            }
            finally
            {
                if (Find.Targeter.IsTargeting) Find.Targeter.StopTargeting();
                if (!serum.Destroyed) serum.Destroy();
            }
            TestHelpers.AssertNone(failures, "use item gizmo");
        }

        private static void CheckUseItemTargets(Map map, Thing serum, CompUsable usable, GizmoInfo gizmo, List<string> failures)
        {
            CommandResult none = _world.UseGizmo(serum.thingIDNumber, gizmo.Number, gizmo.Label, null);
            if (none.Success) failures.Add("pressing without a target succeeded");
            else if (!none.Message.Contains("target")) failures.Add("missing-target message does not mention a target: " + none.Message);
            if (Find.Targeter.IsTargeting) failures.Add("targeter was left active after pressing without a target");

            CommandResult cell = _world.UseGizmo(serum.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfCell(serum.Position.x, serum.Position.z));
            if (cell.Success) failures.Add("a plain cell was accepted as the user");
            CommandResult item = _world.UseGizmo(serum.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfThing(serum.thingIDNumber));
            if (item.Success) failures.Add("the item itself was accepted as the user");
            CommandResult missing = _world.UseGizmo(serum.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfThing(-12345));
            if (missing.Success) failures.Add("a thing id that is not on the map was accepted");

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                AcceptanceReport vanilla = usable.CanBeUsedBy(pawn, usable.Props.ignoreOtherReservations);
                if (vanilla.Accepted) continue;
                CommandResult rejected = _world.UseGizmo(serum.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfThing(pawn.thingIDNumber));
                if (rejected.Success) failures.Add($"{pawn.LabelShort} cannot use the serum in vanilla but the command succeeded");
                else if (!vanilla.Reason.NullOrEmpty() && !rejected.Message.Contains(vanilla.Reason.CapitalizeFirst().StripTags()))
                    failures.Add($"reason for {pawn.LabelShort} differs: tool \"{rejected.Message}\", vanilla \"{vanilla.Reason}\"");
                if (Find.Targeter.IsTargeting) failures.Add("targeter was left active after a rejected target");
                return;
            }
            TestHelpers.Skip(nameof(UseItemGizmoAsksForAndChecksAColonist), "every colonist could use the serum; the rejection reason was not checked");
        }

        /// <summary>
        /// 右鍵選單對照原版 FloatMenuMakerMap 的結果（標籤與順序），陌生的號碼、對不上的標籤要被擋下；
        /// 再讓徵召的殖民者用「前往此處」走到附近一格，以原版 Goto 工作讀回。結束後取消徵召與工作。
        /// </summary>
        [Test]
        public static void RightClickMenuMatchesVanillaAndRunsAnOption()
        {
            if (TestHelpers.SkipWithoutMap(nameof(RightClickMenuMatchesVanillaAndRunsAnOption))) return;
            Map map = Find.CurrentMap;
            Pawn pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.Drafted && p.CanTakeOrder && p.drafter != null);
            ThingDef steelDef = ThingDefOf.Steel;
            CellRect? found = pawn == null ? null : FindFreeRect(map, 1, c => c.Standable(map) && c.GetThingList(map).Count == 0);
            if (found == null)
            {
                TestHelpers.Skip(nameof(RightClickMenuMatchesVanillaAndRunsAnOption), pawn == null ? "no free colonist who can take orders" : "no empty cell near the colonists");
                return;
            }
            Thing item = ThingMaker.MakeThing(steelDef);
            item.stackCount = 5;
            var failures = new List<string>();
            _world.TakeNewGameErrors();
            try
            {
                GenSpawn.Spawn(item, found.Value.CenterCell, map);
                CheckMenuMatchesVanilla(pawn, MapTarget.OfThing(item.thingIDNumber), item.Position.ToVector3Shifted(), failures);
                CheckMenuMatchesVanilla(pawn, MapTarget.OfCell(item.Position.x, item.Position.z), item.Position.ToVector3Shifted(), failures);

                FloatMenuInfo menu = _world.GetFloatMenuOptions(pawn.thingIDNumber, MapTarget.OfThing(item.thingIDNumber));
                if (menu.Options.Count > 0)
                {
                    FloatMenuOptionInfo first = menu.Options[0];
                    if (_world.UseFloatMenuOption(pawn.thingIDNumber, MapTarget.OfThing(item.thingIDNumber), first.Number, first.Label + " (not the label)").Success)
                        failures.Add("an option with a different label was run");
                    if (_world.UseFloatMenuOption(pawn.thingIDNumber, MapTarget.OfThing(item.thingIDNumber), menu.Options.Count + 5, first.Label).Success)
                        failures.Add("an option number past the end was run");
                }
                if (_world.GetFloatMenuOptions(pawn.thingIDNumber, MapTarget.OfCell(-5, -5)).Error.Length == 0)
                    failures.Add("a cell outside the map produced a menu");
                if (_world.GetFloatMenuOptions(-1, MapTarget.OfThing(item.thingIDNumber)).Error.Length == 0)
                    failures.Add("an unknown colonist produced a menu");
            }
            finally
            {
                if (!item.Destroyed) item.Destroy();
            }
            CheckGotoOption(map, pawn, failures);
            // 選單在這條呼叫路徑（不在 OnGUI 裡）建構選項時若出錯，原版供應者會記 log 並略過，選單變空；這裡把它當失敗。
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "right-click menu");
        }

        private static void CheckMenuMatchesVanilla(Pawn pawn, MapTarget target, UnityEngine.Vector3 clickPosition, List<string> failures)
        {
            FloatMenuInfo menu = _world.GetFloatMenuOptions(pawn.thingIDNumber, target);
            if (!string.IsNullOrEmpty(menu.Error))
            {
                failures.Add($"{target}: menu error {menu.Error}");
                return;
            }
            List<FloatMenuOption> vanilla = FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, clickPosition, out FloatMenuContext _)
                .OrderByDescending(o => o.Priority).ThenByDescending(o => o.orderInPriority).ToList();
            if (menu.Options.Count != vanilla.Count)
            {
                failures.Add($"{target}: {menu.Options.Count} options, vanilla {vanilla.Count}");
                return;
            }
            for (int i = 0; i < vanilla.Count; i++)
            {
                if (menu.Options[i].Number != i + 1) failures.Add($"{target}: option {i + 1} is numbered {menu.Options[i].Number}");
                if (!string.Equals(menu.Options[i].Label, vanilla[i].Label.StripTags(), StringComparison.Ordinal))
                    failures.Add($"{target}: option {i + 1} is \"{menu.Options[i].Label}\", vanilla \"{vanilla[i].Label}\"");
                if (menu.Options[i].Disabled != vanilla[i].Disabled) failures.Add($"{target}: option {i + 1} disabled flag differs");
            }
        }

        private static void CheckGotoOption(Map map, Pawn pawn, List<string> failures)
        {
            IntVec3 start = pawn.Position;
            IntVec3 cell = GenRadial.RadialCellsAround(start, 8f, false)
                .Where(c => c.InBounds(map) && c.Standable(map) && (c - start).LengthHorizontal >= 4f && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                .DefaultIfEmpty(IntVec3.Invalid)
                .First();
            if (!cell.IsValid)
            {
                TestHelpers.Skip(nameof(RightClickMenuMatchesVanillaAndRunsAnOption), "no reachable cell nearby for the go-here check");
                return;
            }
            try
            {
                pawn.drafter.Drafted = true;
                FloatMenuInfo menu = _world.GetFloatMenuOptions(pawn.thingIDNumber, MapTarget.OfCell(cell.x, cell.z));
                string goHere = "GoHere".Translate();
                FloatMenuOptionInfo option = menu.Options.FirstOrDefault(o => string.Equals(o.Label, goHere, StringComparison.OrdinalIgnoreCase));
                if (!menu.Drafted) failures.Add("menu does not report the colonist as drafted");
                if (option == null)
                {
                    failures.Add("drafted colonist has no \"" + goHere + "\" option; options: " + string.Join("; ", menu.Options.Select(o => o.Label)));
                    return;
                }
                CommandResult result = _world.UseFloatMenuOption(pawn.thingIDNumber, MapTarget.OfCell(cell.x, cell.z), option.Number, option.Label);
                if (!result.Success) failures.Add("go-here failed: " + result.Error);
                else if (pawn.CurJob == null || pawn.CurJob.def != JobDefOf.Goto) failures.Add("current job is " + (pawn.CurJob?.def.defName ?? "none") + ", expected Goto");
                else if ((pawn.CurJob.targetA.Cell - cell).LengthHorizontal > 3.5f) failures.Add($"Goto heads to {pawn.CurJob.targetA.Cell}, expected near {cell}");
            }
            finally
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                pawn.drafter.Drafted = false;
            }
        }

        /// <summary>
        /// 世界物件清單對照原版：同一批物件（不含玩家商隊與路線規劃點）、種類與敵對判定、到起點的距離、
        /// 定居點的地圖狀態；起點格的地形資料對照原版 Tile，被自己的殖民地占著所以不能建立殖民地。
        /// </summary>
        [Test]
        public static void WorldSurveyAndTileMatchVanilla()
        {
            if (TestHelpers.SkipWithoutMap(nameof(WorldSurveyAndTileMatchVanilla))) return;
            var failures = new List<string>();
            _world.TakeNewGameErrors();
            Map map = Find.CurrentMap;
            PlanetTile origin = map.Tile;

            WorldSurveyInfo survey = _world.GetWorldSurvey(-1);
            if (survey.OriginTile != origin.tileId) failures.Add($"origin tile {survey.OriginTile}, vanilla {origin.tileId}");
            if (survey.OriginIsCaravan) failures.Add("a survey without a caravan claims its origin is a caravan");
            var expected = Find.WorldObjects.AllWorldObjects
                .Where(o => o.Spawned && o.Tile.Valid && o.Tile.Layer == origin.Layer && !(o is RoutePlannerWaypoint) && !(o is Caravan { IsPlayerControlled: true }))
                .ToDictionary(o => o.ID);
            foreach (WorldObjectInfo info in survey.Objects)
            {
                if (!expected.TryGetValue(info.Id, out WorldObject obj))
                {
                    failures.Add($"[{info.Id}] {info.Label} is not a vanilla world object of this layer");
                    continue;
                }
                if (info.Tile != obj.Tile.tileId) failures.Add($"{obj.LabelCap}: tile {info.Tile}, vanilla {obj.Tile.tileId}");
                float distance = Find.WorldGrid.ApproxDistanceInTiles(origin, obj.Tile);
                if (Math.Abs(info.DistanceTiles - distance) > 0.01f) failures.Add($"{obj.LabelCap}: distance {info.DistanceTiles}, vanilla {distance}");
                bool hostile = obj.Faction != null && obj.Faction != Faction.OfPlayer && obj.Faction.HostileTo(Faction.OfPlayer);
                if (info.Hostile != hostile) failures.Add($"{obj.LabelCap}: hostile {info.Hostile}, vanilla {hostile}");
                if (obj is Settlement settlement)
                {
                    string kind = settlement.Faction == Faction.OfPlayer ? WorldObjectInfo.PlayerSettlementKind : WorldObjectInfo.FactionSettlementKind;
                    if (info.Kind != kind) failures.Add($"{obj.LabelCap}: kind {info.Kind}, expected {kind}");
                    if (info.HasMap != settlement.HasMap) failures.Add($"{obj.LabelCap}: HasMap {info.HasMap}, vanilla {settlement.HasMap}");
                }
                if (obj is Site && info.Kind != WorldObjectInfo.SiteKind) failures.Add($"{obj.LabelCap}: a site listed as {info.Kind}");
            }
            foreach (WorldObject obj in expected.Values.Where(o => survey.Objects.All(i => i.Id != o.ID)))
                failures.Add($"{obj.LabelCap} [{obj.ID}] is missing from the survey");
            if (!survey.Objects.Any(o => o.Kind == WorldObjectInfo.PlayerSettlementKind && o.HasMap))
                failures.Add("the home settlement with a map is not in the survey");

            IReadOnlyList<TravelEstimateInfo> estimates = _world.GetTravelEstimates(-1, survey.Objects.Take(5).Select(o => o.Tile).ToList());
            foreach (TravelEstimateInfo e in estimates)
            {
                PlanetTile to = new PlanetTile(e.Tile, origin.Layer);
                bool reachable = Find.WorldReachability.CanReach(origin, to);
                if (e.Reachable != reachable) failures.Add($"tile {e.Tile}: reachable {e.Reachable}, vanilla {reachable}");
                if (e.Reachable && to != origin && e.Ticks <= 0) failures.Add($"tile {e.Tile}: reachable but no travel time");
            }

            WorldTileInfo tile = _world.GetWorldTile(origin.tileId, -1);
            RimWorld.Planet.Tile vanillaTile = Find.WorldGrid[origin];
            if (!string.IsNullOrEmpty(tile.Error)) failures.Add("home tile: " + tile.Error);
            if (tile.Biome != vanillaTile.PrimaryBiome.LabelCap) failures.Add($"biome {tile.Biome}, vanilla {vanillaTile.PrimaryBiome.LabelCap}");
            if (tile.Hilliness != vanillaTile.hilliness.GetLabelCap()) failures.Add($"hilliness {tile.Hilliness}, vanilla {vanillaTile.hilliness.GetLabelCap()}");
            if (Math.Abs(tile.TemperatureC - vanillaTile.temperature) > 0.01f) failures.Add($"temperature {tile.TemperatureC}, vanilla {vanillaTile.temperature}");
            if (tile.CanSettle || string.IsNullOrEmpty(tile.SettleBlockedReason)) failures.Add("the home tile claims a colony could be founded there, or gives no reason");
            if (!tile.Objects.Any(o => o.Kind == WorldObjectInfo.PlayerSettlementKind)) failures.Add("the home settlement is not on its own tile");
            if (string.IsNullOrEmpty(_world.GetWorldTile(int.MaxValue, -1).Error)) failures.Add("a tile id outside the world was accepted");

            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "world survey and tile");
        }

        /// <summary>
        /// 任務地點與任務的關聯：產生一個會在世界地圖放地點的任務，清單裡該地點要連到這個任務；結束後移除任務與新增的世界物件。
        /// </summary>
        [Test]
        public static void QuestSiteIsLinkedToItsQuest()
        {
            if (TestHelpers.SkipWithoutMap(nameof(QuestSiteIsLinkedToItsQuest))) return;
            var worldObjectsBefore = new HashSet<WorldObject>(Find.WorldObjects.AllWorldObjects);
            Quest quest = GenerateManualQuest("OpportunitySite_BanditCamp", "TradeRequest");
            if (quest == null)
            {
                TestHelpers.Skip(nameof(QuestSiteIsLinkedToItsQuest), "no manually accepted quest could be generated on this world");
                return;
            }
            var failures = new List<string>();
            try
            {
                // 營地這類據點要等任務接受（QuestPart_SpawnWorldObject 收到訊號）才出現在世界上；接受前查詢略過它是對的。
                List<WorldObject> unspawned = QuestTargets(quest).Where(o => !o.Spawned).ToList();
                if (_world.GetWorldSurvey(-1).Objects.Any(o => unspawned.Any(u => u.ID == o.Id)))
                    failures.Add("the survey lists a quest site that is not on the world yet");
                Pawn accepter = quest.RequiresAccepter
                    ? PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended.FirstOrDefault(p => QuestUtility.CanPawnAcceptQuest(p, quest))
                    : null;
                // 有獎勵選項時原版要先選定再接受，否則會記錯誤並自動選第一項。
                QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
                if (choice != null && choice.choices.Count > 0) choice.Choose(choice.choices[0]);
                quest.Accept(accepter);
                List<WorldObject> targets = QuestTargets(quest).Where(o => o.Spawned).ToList();
                if (targets.Count == 0)
                {
                    TestHelpers.Skip(nameof(QuestSiteIsLinkedToItsQuest), "the accepted quest has no world object on the world");
                    return;
                }
                WorldSurveyInfo survey = _world.GetWorldSurvey(-1);
                foreach (WorldObject target in targets)
                {
                    WorldObjectInfo info = survey.Objects.FirstOrDefault(o => o.Id == target.ID);
                    if (info == null) failures.Add($"quest target {target.LabelCap} [{target.ID}] is not in the survey");
                    else if (!info.Quests.Any(q => q.QuestId == quest.id && q.Active)) failures.Add($"{target.LabelCap} is not linked to the accepted quest {quest.id}");
                }
            }
            finally
            {
                if (!quest.Historical) quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
                Find.QuestManager.Remove(quest);
                foreach (WorldObject created in Find.WorldObjects.AllWorldObjects.Where(o => !worldObjectsBefore.Contains(o)).ToList())
                    if (!created.Destroyed) created.Destroy();
            }
            TestHelpers.AssertNone(failures, "quest links");
        }

        private static List<WorldObject> QuestTargets(Quest quest) =>
            quest.QuestLookTargets.Where(t => t.HasWorldObject).Select(t => t.WorldObject).Where(o => o != null).Distinct().ToList();

        /// <summary>
        /// 商隊走完一趟：用原版從地圖邊緣組一支單人商隊，查詢對照原版，派到相鄰格並以 pather 讀回目的地，
        /// 暫停、繼續、停下，過期的選單被擋下；再把商隊放回家的格子，走「進入地圖」命令，
        /// 殖民者要回到家園地圖上，get_maps 的人數同步。結束時一定把殖民者帶回地圖。
        /// </summary>
        [Test]
        public static void CaravanTravelsPausesStopsAndEntersTheHomeMap()
        {
            if (TestHelpers.SkipWithoutMap(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap))) return;
            Map map = Find.CurrentMap;
            if (!(map.Parent is Settlement) || map.Parent.Faction != Faction.OfPlayer)
            {
                TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "the current map is not a player settlement");
                return;
            }
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && p.IsFreeNonSlaveColonist).ToList();
            if (colonists.Count < 2)
            {
                TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "needs at least two free colonists so the colony is never empty");
                return;
            }

            var failures = new List<string>();
            _world.TakeNewGameErrors();
            var lettersBefore = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
            Pawn pawn = colonists[0];
            Caravan caravan = null;
            try
            {
                caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(new List<Pawn> { pawn }, Faction.OfPlayer, map.Tile, Direction8Way.North, PlanetTile.Invalid);
                if (caravan == null)
                {
                    TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "vanilla could not form a caravan here");
                    return;
                }
                // 原版從地圖邊緣出發時會自動往出口方向的格子走（CaravanExitMapUtility 的 StartPath(directionTile)）；
                // 查詢要照實回報，之後停下來，讓後面的檢查從靜止開始。
                CaravanInfo leaving = _world.GetCaravans().FirstOrDefault(c => c.Id == caravan.ID);
                if (leaving != null && leaving.Moving != caravan.pather.Moving) failures.Add($"Moving {leaving.Moving}, vanilla {caravan.pather.Moving}");
                caravan.pather.StopDead();
                CheckCaravanQuery(caravan, pawn, failures);

                // 單純前往相鄰的可走格（不是家）。
                var neighbors = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(caravan.Tile, neighbors);
                PlanetTile next = neighbors.Where(t => t != map.Tile && !Find.World.Impassable(t) && caravan.CanReach(t))
                    .DefaultIfEmpty(PlanetTile.Invalid).First();
                if (!next.Valid)
                {
                    TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "no passable neighbouring tile to travel to");
                }
                else
                {
                    CheckTravelControls(caravan, next, failures);
                }

                CheckEnterHomeMap(caravan, pawn, map, failures);
            }
            finally
            {
                // 沒走完測試流程時也要把人帶回家園地圖。
                if (caravan != null && !caravan.Destroyed && caravan.Spawned)
                {
                    caravan.pather.StopDead();
                    caravan.Tile = map.Tile;
                    CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, CaravanDropInventoryMode.DoNotDrop, false);
                }
                foreach (Letter letter in Find.LetterStack.LettersListForReading.Where(l => !lettersBefore.Contains(l)).ToList())
                    Find.LetterStack.RemoveLetter(letter);
            }
            if (!pawn.Spawned || pawn.Map != map) failures.Add($"{pawn.LabelShort} did not end up back on the home map");
            if (Find.Targeter.IsTargeting || Find.WorldTargeter.IsTargeting) failures.Add("a targeter was left open");
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "caravan journey");
        }

        private static void CheckCaravanQuery(Caravan caravan, Pawn pawn, List<string> failures)
        {
            CaravanInfo info = _world.GetCaravans().FirstOrDefault(c => c.Id == caravan.ID);
            if (info == null)
            {
                failures.Add("the new caravan is missing from GetCaravans");
                return;
            }
            if (info.Tile != caravan.Tile.tileId) failures.Add($"caravan tile {info.Tile}, vanilla {caravan.Tile.tileId}");
            if (info.Members.Count != 1 || info.Members[0].Id != pawn.thingIDNumber || info.Members[0].Kind != CaravanMemberInfo.ColonistKind)
                failures.Add("caravan members do not match the one colonist that left");
            if (info.Moving) failures.Add("a fresh caravan reports it is moving");
            if (info.DestinationTile != -1) failures.Add("a fresh caravan reports destination " + info.DestinationTile);
            if (Math.Abs(info.MassCapacity - caravan.MassCapacity) > 0.01f) failures.Add($"mass capacity {info.MassCapacity}, vanilla {caravan.MassCapacity}");
            if (Math.Abs(info.FoodDays - DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(caravan)) > 0.01f) failures.Add("food days differ from vanilla");
            bool settleBlocked = SettleInEmptyTileUtility.SettleCommand(caravan).Disabled;
            if (info.CanSettle == settleBlocked) failures.Add($"CanSettle {info.CanSettle}, vanilla command disabled {settleBlocked}");
            if (settleBlocked && string.IsNullOrEmpty(info.SettleBlockedReason)) failures.Add("settling is blocked but no reason is given");
            if (info.CanCamp == SettleInEmptyTileUtility.SetupCamp(caravan).Disabled) failures.Add("CanCamp differs from the vanilla camp command");

            WorldSurveyInfo survey = _world.GetWorldSurvey(caravan.ID);
            if (!survey.OriginIsCaravan || survey.OriginTile != caravan.Tile.tileId) failures.Add("a survey from the caravan does not start at the caravan");
            if (survey.Objects.Any(o => o.Id == caravan.ID)) failures.Add("the survey lists the caravan it starts from");
            // 沒打算在這裡真的定居：只在原版也說不行時驗證命令會被擋下。
            if (settleBlocked)
            {
                CommandResult settle = _world.SettleCaravan(caravan.ID, CaravanSettleAction.Settle);
                if (settle.Success) failures.Add("settling succeeded although the vanilla command is disabled");
                if (caravan.Destroyed || !caravan.Spawned) failures.Add("a refused settle removed the caravan");
            }
            if (_world.SettleCaravan(caravan.ID, "build").Success) failures.Add("an unknown settle action succeeded");
        }

        private static void CheckTravelControls(Caravan caravan, PlanetTile next, List<string> failures)
        {
            CaravanOptionsInfo options = _world.GetCaravanOptions(caravan.ID, next.tileId);
            if (!string.IsNullOrEmpty(options.Error) || options.Options.Count == 0)
            {
                failures.Add("no orders for the neighbouring tile: " + options.Error);
                return;
            }
            FloatMenuOptionInfo travel = options.Options[0];
            if (travel.Disabled || !travel.Label.StartsWith("Travel to tile " + next.tileId, StringComparison.Ordinal))
                failures.Add($"first order is \"{travel.Label}\" (disabled {travel.Disabled})");
            if (options.Options.Any(o => o.Label.Contains("(Dev:"))) failures.Add("a developer-mode order was offered");

            // 過期的標籤擋下，商隊不動。
            CommandResult stale = _world.SendCaravan(caravan.ID, next.tileId, 1, "Travel somewhere else");
            if (stale.Success || caravan.pather.Moving) failures.Add("an order with a wrong label was carried out");

            CommandResult sent = _world.SendCaravan(caravan.ID, next.tileId, travel.Number, travel.Label);
            if (!sent.Success) failures.Add("send failed: " + sent.Error);
            if (!caravan.pather.Moving || caravan.pather.Destination != next) failures.Add("the caravan's pather has no destination " + next.tileId);
            CaravanInfo moving = _world.GetCaravans().First(c => c.Id == caravan.ID);
            if (!moving.Moving || moving.DestinationTile != next.tileId) failures.Add($"GetCaravans destination {moving.DestinationTile}, expected {next.tileId}");
            if (moving.EtaTicks <= 0) failures.Add("a travelling caravan has no arrival estimate");

            if (!_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Pause).Success || !caravan.pather.Paused) failures.Add("pause did not pause the pather");
            if (!_world.GetCaravans().First(c => c.Id == caravan.ID).Paused) failures.Add("GetCaravans does not show the pause");
            if (!_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Resume).Success || caravan.pather.Paused) failures.Add("resume did not clear the pause");
            if (!_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Stop).Success || caravan.pather.Moving) failures.Add("stop left the caravan moving");
            if (_world.GetCaravans().First(c => c.Id == caravan.ID).DestinationTile != -1) failures.Add("a stopped caravan still reports a destination");
            if (_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Pause).Success) failures.Add("pausing a stopped caravan succeeded");
        }

        private static void CheckEnterHomeMap(Caravan caravan, Pawn pawn, Map map, List<string> failures)
        {
            // 瞬間回到家的格子（原版開發者「Dev: instantly」做的事），再走正式的進入命令。
            caravan.pather.StopDead();
            caravan.Tile = map.Tile;
            CaravanInfo here = _world.GetCaravans().FirstOrDefault(c => c.Id == caravan.ID);
            if (here == null || here.ObjectsHere.Count == 0) failures.Add("the caravan on the home tile does not list the home settlement as an object here");

            string enterLabel = "EnterMap".Translate(map.Parent.Label).ToString();
            CaravanOptionsInfo options = _world.GetCaravanOptions(caravan.ID, map.Tile.tileId);
            if (options.Options.Count == 0 || !options.Options[0].Disabled) failures.Add("plain travel to the caravan's own tile should be disabled");
            FloatMenuOptionInfo enter = options.Options.FirstOrDefault(o => o.Label.StartsWith(enterLabel, StringComparison.Ordinal) && !o.Disabled);
            if (enter == null)
            {
                failures.Add($"no enabled \"{enterLabel}\" order; orders: {string.Join("; ", options.Options.Select(o => o.Number + ") " + o.Label).ToArray())}");
                return;
            }
            CommandResult entered = _world.SendCaravan(caravan.ID, map.Tile.tileId, enter.Number, enter.Label);
            if (!entered.Success) failures.Add("entering the home map failed: " + entered.Error);
            if (!pawn.Spawned || pawn.Map != map) failures.Add($"{pawn.LabelShort} is not spawned on the home map after entering");
            if (!caravan.Destroyed && caravan.Spawned) failures.Add("the caravan still exists after its members entered the map");
            if (_world.GetCaravans().Any(c => c.Id == caravan.ID)) failures.Add("the entered caravan is still listed");
            RimAgent.Game.MapInfo mapInfo = _world.GetMaps().FirstOrDefault(m => m.Id == map.uniqueID);
            if (mapInfo == null) failures.Add("the home map is missing from GetMaps");
            else if (mapInfo.ColonistCount != map.mapPawns.FreeColonistsSpawnedCount) failures.Add($"GetMaps counts {mapInfo.ColonistCount} colonists, vanilla {map.mapPawns.FreeColonistsSpawnedCount}");
        }

        /// <summary>
        /// 組商隊：在家園地圖上放好銀兩、食物與一隻殖民地動物，規劃的數字對照原版計算器；超重被擋下且不集結；
        /// 再送出，檢查原版的集結狀態（Lord、LordJob 的貨物清單、成員），最後中止集結，成員恢復平常、貨物原封不動。
        /// 測試期間不推進遊戲時間，所以沒有人真的離開地圖；結束時一律清掉 Lord 與放出來的東西。
        /// </summary>
        [Test]
        public static void CaravanPlansFormsAndCancelsWithVanillaState()
        {
            if (TestHelpers.SkipWithoutMap(nameof(CaravanPlansFormsAndCancelsWithVanillaState))) return;
            Map map = Find.CurrentMap;
            if (!(map.Parent is Settlement) || map.Parent.Faction != Faction.OfPlayer)
            {
                TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "the current map is not a player settlement");
                return;
            }
            Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.Drafted && p.IsFreeNonSlaveColonist);
            if (colonist == null || map.mapPawns.FreeColonistsSpawnedCount < 2)
            {
                TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "needs a walking colonist and at least two colonists");
                return;
            }
            var neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
            PlanetTile destination = neighbors.Where(t => !Find.World.Impassable(t) && Find.WorldReachability.CanReach(map.Tile, t))
                .DefaultIfEmpty(PlanetTile.Invalid).First();
            if (!destination.Valid)
            {
                TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "no reachable neighbouring tile to plan a trip to");
                return;
            }
            ThingDef foodDef = DefDatabase<ThingDef>.GetNamedSilentFail("Pemmican") ?? ThingDefOf.MealSimple;

            // 放貨物的格子：玩家看得到、站得住、沒東西、殖民者走得到；家園區暫時設成包含它們，結束時還原。
            List<IntVec3> cells = GenRadial.RadialCellsAround(colonist.Position, 20f, true)
                .Where(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetFirstItem(map) == null && c.GetEdifice(map) == null
                            && colonist.CanReach(c, PathEndMode.Touch, Danger.Deadly))
                .Take(8).ToList();
            if (cells.Count < 8)
            {
                TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "not enough free cells near a colonist");
                return;
            }

            var failures = new List<string>();
            _world.TakeNewGameErrors();
            var lettersBefore = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
            var homeBefore = cells.ToDictionary(c => c, c => map.areaManager.Home[c]);
            var spawned = new List<Thing>();
            Pawn animal = null;
            bool createdAnimal = false;
            Lord lord = null;
            try
            {
                foreach (IntVec3 c in cells) map.areaManager.Home[c] = true;
                Thing silver = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Silver), cells[0], map);
                silver.stackCount = 300;
                Thing food = GenSpawn.Spawn(ThingMaker.MakeThing(foodDef), cells[1], map);
                food.stackCount = 40;
                spawned.Add(silver);
                spawned.Add(food);

                animal = map.mapPawns.PawnsInFaction(Faction.OfPlayer)
                    .FirstOrDefault(p => p.Spawned && p.RaceProps.Animal && p.RaceProps.allowedOnCaravan && !p.Downed && !p.Drafted && p.GetLord() == null);
                if (animal == null)
                {
                    animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Muffalo, Faction.OfPlayer));
                    GenSpawn.Spawn(animal, cells[2], map);
                    createdAnimal = true;
                }

                CheckCaravanCargoQuery(silver, food, failures);

                var request = new CaravanRequest
                {
                    PawnIds = new[] { colonist.thingIDNumber, animal.thingIDNumber },
                    Cargo = new[] { new TradeLine { DefName = silver.def.defName, Count = 100 }, new TradeLine { DefName = food.def.defName, Count = 30 } },
                    DestinationTile = destination.tileId
                };
                CaravanPlanInfo plan = _world.PlanCaravan(request);
                if (!string.IsNullOrEmpty(plan.Error))
                {
                    failures.Add("plan failed: " + plan.Error);
                    TestHelpers.AssertNone(failures, "caravan forming");
                    return;
                }
                CheckPlanMatchesVanilla(plan, colonist, animal, silver, food, destination, map, failures);

                // 沒選目的地、超過現有數量：原版視窗不讓送出。
                CaravanPlanInfo noRoute = _world.PlanCaravan(new CaravanRequest { PawnIds = request.PawnIds, Cargo = request.Cargo, DestinationTile = -1 });
                if (noRoute.Blockers.Count == 0) failures.Add("a plan without a destination has no blocker");
                if (_world.FormCaravan(new CaravanRequest { PawnIds = request.PawnIds, Cargo = request.Cargo, DestinationTile = -1 }).Success)
                    failures.Add("forming without a destination succeeded");
                CaravanPlanInfo tooMuch = _world.PlanCaravan(new CaravanRequest
                {
                    PawnIds = request.PawnIds,
                    Cargo = new[] { new TradeLine { DefName = silver.def.defName, Count = 999999 } },
                    DestinationTile = destination.tileId
                });
                if (string.IsNullOrEmpty(tooMuch.Error)) failures.Add("asking for more silver than the colony has was not an error");

                // 超重：三堆鋼，遠超一個人加一隻動物扛得動的量。
                for (int i = 3; i < 6; i++)
                {
                    Thing steel = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), cells[i], map);
                    steel.stackCount = steel.def.stackLimit;
                    spawned.Add(steel);
                }
                int steelAvailable = spawned.Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);
                CaravanPlanInfo heavy = _world.PlanCaravan(new CaravanRequest
                {
                    PawnIds = new[] { colonist.thingIDNumber },
                    Cargo = new[] { new TradeLine { DefName = ThingDefOf.Steel.defName, Count = steelAvailable } },
                    DestinationTile = destination.tileId
                });
                string tooBig = "TooBigCaravanMassUsage".Translate().Resolve();
                if (string.IsNullOrEmpty(heavy.Error) && heavy.MassUsage > heavy.MassCapacity && !heavy.Blockers.Contains(tooBig))
                    failures.Add("an overloaded plan does not carry the game's reason: " + string.Join("; ", heavy.Blockers.ToArray()));
                if (string.IsNullOrEmpty(heavy.Error) && heavy.MassUsage > heavy.MassCapacity)
                {
                    int lordsBefore = map.lordManager.lords.Count;
                    CommandResult refused = _world.FormCaravan(new CaravanRequest { PawnIds = new[] { colonist.thingIDNumber },
                        Cargo = new[] { new TradeLine { DefName = ThingDefOf.Steel.defName, Count = steelAvailable } }, DestinationTile = destination.tileId });
                    if (refused.Success || map.lordManager.lords.Count != lordsBefore || colonist.IsFormingCaravan())
                        failures.Add("an overloaded caravan was formed");
                }

                if (plan.Blockers.Count > 0)
                {
                    TestHelpers.AssertNone(failures, "caravan planning");
                    TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState),
                        "vanilla would refuse this caravan here, so forming is not exercised: " + string.Join("; ", plan.Blockers.ToArray()));
                    return;
                }

                // 送出：原版集結狀態。
                CommandResult formed = _world.FormCaravan(request);
                if (!formed.Success)
                {
                    failures.Add("forming failed: " + formed.Error);
                    TestHelpers.AssertNone(failures, "caravan forming");
                }
                if (!colonist.IsFormingCaravan() || !animal.IsFormingCaravan()) failures.Add("the colonist and the animal are not forming a caravan");
                colonist.TryGetFormingCaravanLord(out lord);
                var job = lord?.LordJob as LordJob_FormAndSendCaravan;
                if (job == null) failures.Add("the colonist's lord is not a LordJob_FormAndSendCaravan");
                else
                {
                    int silverQueued = job.transferables.Where(t => t.HasAnyThing && t.ThingDef == ThingDefOf.Silver).Sum(t => t.CountToTransfer);
                    int foodQueued = job.transferables.Where(t => t.HasAnyThing && t.ThingDef == foodDef).Sum(t => t.CountToTransfer);
                    if (silverQueued != 100 || foodQueued != 30) failures.Add($"the lord job queues silver {silverQueued}, food {foodQueued}; expected 100 and 30");
                    if (job.transferables.Any(t => t.AnyThing is Pawn)) failures.Add("pawns were left in the lord job's item list");
                    if (!lord.ownedPawns.Contains(colonist) || !lord.ownedPawns.Contains(animal)) failures.Add("the lord does not own both members");
                }
                FormingCaravanInfo listed = _world.GetFormingCaravans().FirstOrDefault(f => lord != null && f.Id == lord.loadID);
                if (listed == null) failures.Add("GetFormingCaravans does not list the new caravan");
                else
                {
                    if (!listed.Members.Any(m => m.Id == colonist.thingIDNumber) || !listed.Members.Any(m => m.Id == animal.thingIDNumber))
                        failures.Add("the forming caravan lists the wrong members");
                    if (!listed.Cargo.Any(c => c.EndsWith(" x100", StringComparison.Ordinal))) failures.Add("the forming caravan does not list the silver: " + string.Join(", ", listed.Cargo.ToArray()));
                    if (string.IsNullOrEmpty(listed.Status)) failures.Add("the forming caravan has no status");
                }
                if (Find.WorldObjects.Caravans.Any(c => c.PawnsListForReading.Contains(colonist))) failures.Add("the caravan left the map within the same frame");

                // 中止：成員恢復平常，貨物沒動。
                if (listed != null)
                {
                    CommandResult cancelled = _world.CancelFormingCaravan(listed.Id);
                    if (!cancelled.Success) failures.Add("cancelling failed: " + cancelled.Error);
                }
                if (colonist.IsFormingCaravan() || animal.IsFormingCaravan() || colonist.GetLord() is Lord leftover && leftover.LordJob is LordJob_FormAndSendCaravan)
                    failures.Add("the members still belong to a forming lord after cancelling");
                if (_world.GetFormingCaravans().Any(f => lord != null && f.Id == lord.loadID)) failures.Add("the cancelled caravan is still listed");
                if (_world.CancelFormingCaravan(lord?.loadID ?? -12345).Success) failures.Add("cancelling a caravan that no longer exists succeeded");
                if (!silver.Spawned || silver.stackCount != 300 || !food.Spawned || food.stackCount != 40) failures.Add("cancelling changed the cargo on the ground");
                if (colonist.inventory.innerContainer.Any(t => t.def == ThingDefOf.Silver || t.def == foodDef)) failures.Add("the colonist still carries cargo after cancelling");
                lord = null;
            }
            finally
            {
                Lord stuck = colonist.GetLord();
                if (lord != null && lord.lordManager != null) CaravanFormingUtility.StopFormingCaravan(lord);
                else if (stuck != null && stuck.LordJob is LordJob_FormAndSendCaravan) CaravanFormingUtility.StopFormingCaravan(stuck);
                foreach (Thing thing in spawned.Where(t => !t.Destroyed)) thing.Destroy();
                // 放下的貨物若被撿了又放，清掉殖民者身上殘留的測試物品。
                foreach (Thing carried in colonist.inventory.innerContainer.Where(t => t.def == ThingDefOf.Silver || t.def == ThingDefOf.Steel).ToList())
                    carried.Destroy();
                if (createdAnimal && animal != null && !animal.Destroyed) animal.Destroy();
                foreach (KeyValuePair<IntVec3, bool> before in homeBefore) map.areaManager.Home[before.Key] = before.Value;
                foreach (Letter letter in Find.LetterStack.LettersListForReading.Where(l => !lettersBefore.Contains(l)).ToList())
                    Find.LetterStack.RemoveLetter(letter);
            }
            foreach (string error in _world.TakeNewGameErrors()) failures.Add("game logged: " + error);
            TestHelpers.AssertNone(failures, "caravan forming");
        }

        private static void CheckCaravanCargoQuery(Thing silver, Thing food, List<string> failures)
        {
            IReadOnlyList<CaravanCargoInfo> cargo = _world.GetCaravanCargo();
            CaravanCargoInfo silverInfo = cargo.FirstOrDefault(c => c.DefName == silver.def.defName);
            CaravanCargoInfo foodInfo = cargo.FirstOrDefault(c => c.DefName == food.def.defName);
            if (silverInfo == null || silverInfo.Count < 300) failures.Add("the cargo query does not list the 300 silver we put in the home area");
            else if (Math.Abs(silverInfo.MassEach - silver.def.BaseMass) > 0.0001f) failures.Add("silver mass per unit differs from the def");
            if (foodInfo == null || foodInfo.Count < 40) failures.Add("the cargo query does not list the food we put in the home area");
            // 不在清單裡的不能出現：被預約的東西、非物品。
            if (cargo.Any(c => string.IsNullOrEmpty(c.DefName) || c.Count <= 0)) failures.Add("the cargo query lists an empty entry");
        }

        /// <summary>規劃的數字逐項對照原版計算器（用測試自己組的 transferable，不經轉接層的草稿）。</summary>
        private static void CheckPlanMatchesVanilla(CaravanPlanInfo plan, Pawn colonist, Pawn animal, Thing silver, Thing food,
            PlanetTile destination, Map map, List<string> failures)
        {
            var pawns = new List<Pawn> { colonist, animal };
            var transferables = new List<TransferableOneWay>();
            foreach (Pawn pawn in pawns)
            {
                var transferable = new TransferableOneWay();
                transferable.things.Add(pawn);
                transferable.AdjustTo(1);
                transferables.Add(transferable);
            }
            var silverRow = new TransferableOneWay();
            silverRow.things.Add(silver);
            silverRow.AdjustTo(100);
            var foodRow = new TransferableOneWay();
            foodRow.things.Add(food);
            foodRow.AdjustTo(30);
            transferables.Add(silverRow);
            transferables.Add(foodRow);

            const IgnorePawnsInventoryMode mode = IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload;
            float usage = CollectionsMassCalculator.MassUsageTransferables(transferables, mode, false, false);
            float capacity = CollectionsMassCalculator.CapacityTransferables(transferables, new System.Text.StringBuilder());
            PlanetTile start = CaravanExitMapUtility.BestExitTileToGoTo(destination, map);
            int ticksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(pawns, usage, capacity, false, null);
            float tilesPerDay = TilesPerDayCalculator.ApproxTilesPerDay(transferables, usage, capacity, map.Tile, start, false, null);
            float visibility = CaravanVisibilityCalculator.Visibility(transferables, new System.Text.StringBuilder());
            var foraged = ForagedFoodPerDayCalculator.ForagedFoodPerDay(transferables, map.Biome, Faction.OfPlayer, new System.Text.StringBuilder());
            float foodDays;
            int eta;
            using (WorldPath path = map.Tile.Layer.Pather.FindPath(map.Tile, destination, null))
            {
                foodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, map.Tile, mode, Faction.OfPlayer, path, 0f, ticksPerMove);
                eta = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(map.Tile, destination, path, 0f, ticksPerMove, Find.TickManager.TicksAbs);
            }

            if (Math.Abs(plan.MassUsage - usage) > 0.01f) failures.Add($"mass usage {plan.MassUsage}, vanilla {usage}");
            if (Math.Abs(plan.MassCapacity - capacity) > 0.01f) failures.Add($"mass capacity {plan.MassCapacity}, vanilla {capacity}");
            if (plan.TicksPerMove != ticksPerMove) failures.Add($"ticks per move {plan.TicksPerMove}, vanilla {ticksPerMove}");
            if (Math.Abs(plan.TilesPerDay - tilesPerDay) > 0.01f) failures.Add($"tiles per day {plan.TilesPerDay}, vanilla {tilesPerDay}");
            if (Math.Abs(plan.FoodDays - foodDays) > 0.05f) failures.Add($"days of food {plan.FoodDays}, vanilla {foodDays}");
            if (Math.Abs(plan.Visibility - visibility) > 0.001f) failures.Add($"visibility {plan.Visibility}, vanilla {visibility}");
            if (Math.Abs(plan.ForagedPerDay - foraged.Item2) > 0.001f) failures.Add($"foraged per day {plan.ForagedPerDay}, vanilla {foraged.Item2}");
            if (Math.Abs(plan.EtaTicks - eta) > 60) failures.Add($"arrival estimate {plan.EtaTicks} ticks, vanilla {eta}");
            if (plan.DestinationTile != destination.tileId || !plan.DestinationReachable) failures.Add("the plan does not report the destination as reachable");
            if (plan.Members.Count != 2 || plan.Members.All(m => m.Kind != CaravanMemberInfo.AnimalKind)) failures.Add("the plan does not list the animal as an animal");
            if (plan.Cargo.Sum(c => c.Count) != 130) failures.Add("the plan lists " + plan.Cargo.Sum(c => c.Count) + " cargo items, expected 130");
            // 食物不夠五天時，原版會彈出警告框；兩邊要一致。
            bool warnsAboutFood = plan.Warnings.Contains("DaysWorthOfFoodWarningDialog".Translate(foodDays.ToString("0.#")).Resolve())
                                  || plan.Warnings.Contains("DaysWorthOfFoodWarningDialog_NoFood".Translate().Resolve());
            if ((foodDays < 5f) != warnsAboutFood) failures.Add($"food warning {warnsAboutFood} with {foodDays} days of food");
        }

        private static KeyValuePair<string, Func<object>> Query(string name, Func<object> call) =>
            new KeyValuePair<string, Func<object>>(name, call);

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
                return Array.Empty<T>();
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
}
