using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimAgent.Game;
using RimTestRedux;
using RimWorld;
using Verse;

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
