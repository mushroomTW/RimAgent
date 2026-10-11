using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimAgent.Game;
using RimTestRedux;
using RimWorld;
using Verse;
using Verse.AI;

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
            for (int cx = 0; found == null && cx + width < map.Size.x; cx += 4)
            {
                for (int cz = 0; cz + height < map.Size.z; cz += 4)
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
