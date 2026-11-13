using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimAgent.Game;
using RimAgent.Tools;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 模組化房型的遊戲內檢查：每個房型用 place_blueprint 的 layout 形狀放藍圖（四個方向），再把同一份清單直接蓋好，
/// 確認房間封閉與各房型的關鍵擺設，並各截一張圖給人眼比對影片。
/// </summary>
internal static class LayoutCheck
{
    private const int Size = 13;

    /// <summary>一個房型要檢查的內容：各房間挑一格（應各自封閉且互不相同），加上房型專屬的檢查。</summary>
    private sealed class Spec
    {
        public string Layout;
        public (string Name, int X, int Z)[] Rooms;
        public Action<Map, Func<int, int, IntVec3>, List<string>> Extra;
    }

    private static readonly Spec[] Specs =
    {
        new Spec
        {
            Layout = "RimAgent_StarterCabin13",
            Rooms = new[]
            {
                ("freezer", 2, 11), ("entry", 6, 10), ("prison", 10, 11), ("hall", 6, 5),
                ("bedroom1", 1, 2), ("bedroom2", 4, 2), ("bedroom3", 7, 2), ("bedroom4", 11, 2)
            },
            Extra = (map, L, failures) =>
            {
                if (L(9, 5).GetRoom(map) != L(6, 5).GetRoom(map)) failures.Add("research nook is not part of the hall");
                Building dispenser = L(3, 8).GetFirstThing<Building_NutrientPasteDispenser>(map);
                if (dispenser == null) failures.Add("no nutrient paste dispenser at local (3,8)");
                else if (dispenser.InteractionCell != L(6, 8)) failures.Add($"dispenser serves {dispenser.InteractionCell - L(0, 0)}, expected local (6,8)");
                else if (HopperCount(map, dispenser) != 3) failures.Add($"dispenser sees {HopperCount(map, dispenser)} hoppers, expected 3");
                Building cooler = L(0, 10).GetFirstThing<Building_Cooler>(map);
                if (cooler == null) failures.Add("no cooler at local (0,10)");
                else if ((cooler.Position + IntVec3.North.RotatedBy(cooler.Rotation)).GetRoom(map) != L(2, 11).GetRoom(map))
                    failures.Add("cooler does not blow cold air into the freezer");
            }
        },
        new Spec
        {
            Layout = "RimAgent_TrapCorridorStarter13",
            Rooms = new[] { ("hall", 6, 9), ("prison", 2, 3), ("research", 10, 3) },
            Extra = (map, L, failures) =>
            {
                // 走廊朝南開口：和房型外面是同一塊室外
                if (L(6, 2).GetRoom(map) != L(6, -1).GetRoom(map)) failures.Add("trap corridor is not open to the south");
                Building bench = L(9, 2).GetEdifice(map);
                if (bench?.def.defName != "SimpleResearchBench") failures.Add("no research bench at local (9,2)");
                else if (bench.InteractionCell != L(9, 3)) failures.Add($"research bench is used from {bench.InteractionCell - L(0, 0)}, expected local (9,3)");
                Building endTable = L(2, 8).GetEdifice(map);
                foreach ((int x, int z) in new[] { (1, 8), (2, 9), (3, 8) })
                {
                    if (!(L(x, z).GetEdifice(map) is Building_Bed bed)) { failures.Add($"no bed at local ({x},{z})"); continue; }
                    if (endTable == null || !bed.GetSleepingSlotPos(0).AdjacentToCardinal(endTable.Position))
                        failures.Add($"bed at local ({x},{z}) does not have its head at the end table");
                }
            }
        },
        new Spec
        {
            Layout = "RimAgent_Freezer13",
            Rooms = new[] { ("freezer", 6, 9), ("butcher", 10, 2) },
            Extra = (map, L, failures) =>
            {
                // 四台冷氣都把冷風吹進冷庫
                Room freezer = L(6, 9).GetRoom(map);
                foreach ((int x, int z) in new[] { (1, 12), (0, 10), (0, 2), (1, 0) })
                {
                    Building cooler = L(x, z).GetFirstThing<Building_Cooler>(map);
                    if (cooler == null) failures.Add($"no cooler at local ({x},{z})");
                    else if ((cooler.Position + IntVec3.North.RotatedBy(cooler.Rotation)).GetRoom(map) != freezer)
                        failures.Add($"cooler at local ({x},{z}) does not blow cold air into the freezer");
                }
                foreach ((int x, int z) in new[] { (1, 8), (1, 4) })
                {
                    Building stove = L(x, z).GetEdifice(map);
                    if (stove?.def.defName != "ElectricStove") failures.Add($"no electric stove at local ({x},{z})");
                    else if (stove.InteractionCell != L(x + 1, z)) failures.Add($"stove at local ({x},{z}) is used from {stove.InteractionCell - L(0, 0)}");
                }
                Building butcher = L(10, 4).GetEdifice(map);
                if (butcher?.def.defName != "TableButcher") failures.Add("no butcher table at local (10,4)");
                else if (butcher.InteractionCell.GetRoom(map) != L(10, 2).GetRoom(map)) failures.Add("butcher table is not used from inside the butcher room");
            }
        },
    };

    /// <summary>逐一檢查房型；每個蓋好的房型中心放進 <paramref name="builtCenters"/> 供截圖。</summary>
    public static List<string> Run(RimWorldGameWorld world, out string skipReason, List<(string Layout, IntVec3 Center)> builtCenters)
    {
        skipReason = null;
        Map map = Find.CurrentMap;
        var failures = new List<string>();
        Find.ResearchManager.DebugSetAllProjectsFinished();
        var tools = new BuildTools(world);

        foreach (Spec spec in Specs)
        {
            void Fail(string message) => failures.Add(spec.Layout + " " + message);

            // 同一塊空地上四個方向各放一次藍圖（正方形，範圍相同），每次都要整組放得下。
            CellRect? area = FindClearArea(map);
            if (area == null)
            {
                skipReason = $"no clear {Size + 2}x{Size + 2} area for {spec.Layout}";
                return failures;
            }
            IntVec3 center = area.Value.CenterCell;
            PlaceAllRotations(world, tools, spec.Layout, center, Fail);

            // 朝北那組藍圖換成真的建築與地板，檢查房間。
            LayoutPlan plan = Build(world, map, spec.Layout, center);
            builtCenters.Add((spec.Layout, center));

            int ox = plan.Rect.MinX, oz = plan.Rect.MinZ;
            IntVec3 L(int x, int z) => new IntVec3(ox + x, 0, oz + z);
            var seen = new Dictionary<Room, string>();
            foreach ((string name, int x, int z) in spec.Rooms)
            {
                Room room = L(x, z).GetRoom(map);
                if (room == null) { Fail($"{name}: no room"); continue; }
                if (room.TouchesMapEdge) Fail($"{name}: open to the map edge");
                if (!room.ProperRoom) Fail($"{name}: not a proper room");
                if (seen.TryGetValue(room, out string other)) Fail($"{name} shares a room with {other}");
                else seen[room] = name;
                Log.Message(TestHelpers.LogPrefix + $"LayoutCheck {spec.Layout} {name}: {room.CellCount} cells, role {room.Role?.defName ?? "none"}");
            }
            var extra = new List<string>();
            spec.Extra(map, L, extra);
            extra.ForEach(Fail);
        }

        // 其他房型（轉換自 13x13 Cell Prefabs）：同一塊空地輪流放四個方向，每次都要整組放得下。
        var checkedNames = new HashSet<string>(Specs.Select(s => s.Layout));
        CellRect? shared = FindClearArea(map);
        if (shared == null)
        {
            skipReason = $"no clear {Size + 2}x{Size + 2} area for the imported layouts";
            return failures;
        }
        int imported = 0;
        foreach (LayoutInfo info in world.Layouts.Where(l => !checkedNames.Contains(l.DefName)))
        {
            PlaceAllRotations(world, tools, info.DefName, shared.Value.CenterCell, m => failures.Add(info.DefName + " " + m));
            imported++;
        }
        Log.Message(TestHelpers.LogPrefix + $"LayoutCheck imported layouts checked: {imported}");

        // 挑幾個蓋起來截圖，和原模組的預覽圖比對。
        foreach (string layout in Showcase.Where(l => DefDatabase<PrefabDef>.GetNamedSilentFail(l) != null))
        {
            CellRect? area = FindClearArea(map);
            if (area == null) break;
            Build(world, map, layout, area.Value.CenterCell);
            builtCenters.Add((layout, area.Value.CenterCell));
        }
        return failures;
    }

    /// <summary>截圖比對用的轉換房型。</summary>
    private static readonly string[] Showcase =
    {
        "RimAgent_CP_13_13_BeginHouse1", "RimAgent_CP_8Room_YingYangGao", "RimAgent_CP_BigResearchRoom"
    };

    private static void PlaceAllRotations(RimWorldGameWorld world, BuildTools tools, string layout, IntVec3 center, Action<string> fail)
    {
        for (int rot = 0; rot < 4; rot++)
        {
            int total = world.GetLayoutPlan(layout, center.x, center.z, rot).Pieces.Count;
            string reply = tools.PlaceBlueprint(layout, "layout", center.x, center.z, 0, 0, rot, "");
            Log.Message(TestHelpers.LogPrefix + $"LayoutCheck {layout} rotation {rot}: {reply}");
            if (!reply.StartsWith($"Placed {total} of {total}", StringComparison.Ordinal))
            {
                fail($"rotation {rot}: {reply}");
                if (rot == 0) LogMissingFloors(world, layout, center);
            }
            world.RemoveBlueprints(new CellRectSpec(center.x - 6, center.z - 6, center.x + 6, center.z + 6));
        }
    }

    /// <summary>地板藍圖沒放上去的格子，列出那格上有什麼，方便查衝突。</summary>
    private static void LogMissingFloors(RimWorldGameWorld world, string layout, IntVec3 center)
    {
        Map map = Find.CurrentMap;
        LayoutPlan plan = world.GetLayoutPlan(layout, center.x, center.z, 0);
        foreach (LayoutPiece p in plan.Pieces.Where(p => DefDatabase<TerrainDef>.GetNamedSilentFail(p.DefName) != null))
        {
            var cell = new IntVec3(p.X, 0, p.Z);
            List<Thing> things = cell.GetThingList(map);
            if (things.Any(t => t is Blueprint b && b.def.entityDefToBuild.defName == p.DefName)) continue;
            Log.Message(TestHelpers.LogPrefix + $"LayoutCheck {layout} floor {p.DefName} missing at local {cell - new IntVec3(plan.Rect.MinX, 0, plan.Rect.MinZ)}: "
                + string.Join(", ", things.Select(t => t.def.defName)));
        }
    }

    /// <summary>把朝北的房型直接蓋成真的建築與地板（不用藍圖），回傳它的清單。</summary>
    private static LayoutPlan Build(RimWorldGameWorld world, Map map, string layout, IntVec3 center)
    {
        LayoutPlan plan = world.GetLayoutPlan(layout, center.x, center.z, 0);
        world.RemoveBlueprints(plan.Rect);
        foreach (LayoutPiece p in plan.Pieces)
        {
            var cell = new IntVec3(p.X, 0, p.Z);
            TerrainDef terrain = DefDatabase<TerrainDef>.GetNamedSilentFail(p.DefName);
            if (terrain != null)
            {
                map.terrainGrid.SetTerrain(cell, terrain);
                continue;
            }
            ThingDef def = DefDatabase<ThingDef>.GetNamed(p.DefName);
            ThingDef stuff = string.IsNullOrEmpty(p.StuffDefName) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(p.StuffDefName);
            Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(thing, cell, map, new Rot4(p.Rotation));
        }
        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
        return plan;
    }

    private static int HopperCount(Map map, Building dispenser)
    {
        return GenAdj.CellsAdjacentCardinal(dispenser).Count(c => c.InBounds(map) && c.GetThingList(map).Any(t => t.def == ThingDefOf.Hopper));
    }

    /// <summary>找一塊 15x15（房型外加一圈）沒霧、站得住、沒有建築的空地。</summary>
    private static CellRect? FindClearArea(Map map)
    {
        int s = Size + 2;
        for (int cz = 10; cz + s < map.Size.z - 10; cz += 3)
        {
            for (int cx = 10; cx + s < map.Size.x - 10; cx += 3)
            {
                var candidate = new CellRect(cx, cz, s, s);
                if (candidate.Cells.All(c => !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null
                    && c.GetThingList(map).All(t => t is Plant || t is Pawn) && !c.GetThingList(map).Any(t => t is Blueprint)
                    && map.terrainGrid.TerrainAt(c).affordances.Contains(TerrainAffordanceDefOf.Heavy)))
                {
                    return candidate;
                }
            }
        }
        // 隨機測試地圖空地不夠時，退而求其次：地基撐得住重建築、沒霧、沒人，就把上面的植物、石塊和岩壁清掉。
        for (int cz = 10; cz + s < map.Size.z - 10; cz += 3)
        {
            for (int cx = 10; cx + s < map.Size.x - 10; cx += 3)
            {
                var candidate = new CellRect(cx, cz, s, s);
                if (!candidate.Cells.All(c => !c.Fogged(map) && !c.GetThingList(map).Any(t => t is Pawn || t is Blueprint || t.Faction == Faction.OfPlayer || !t.def.destroyable)
                    && map.terrainGrid.TerrainAt(c).affordances.Contains(TerrainAffordanceDefOf.Heavy))) continue;
                foreach (IntVec3 c in candidate.Cells)
                {
                    foreach (Thing t in c.GetThingList(map).ToList()) t.Destroy();
                }
                return candidate;
            }
        }
        return null;
    }
}

/// <summary>
/// 隔離環境 Config 有標記檔 RimAgentLayoutCheck.xml 時，進圖後跑一次，結果以 "[RimAgent.InGameTests] LayoutCheck PASS/FAIL" 警告輸出；
/// 蓋好後逐一對準每個房型截圖，存到遊戲資料夾的 layout-&lt;房型&gt;.png（路徑寫在 log）。
/// </summary>
public sealed class LayoutCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentLayoutCheck.xml";
    private static bool _done;
    private readonly List<(string Layout, IntVec3 Center)> _shots = new List<(string, IntVec3)>();
    private int _shotFrame = -1;
    private int _nextAimFrame = -1;

    public LayoutCheckComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (_nextAimFrame >= 0 && Time.frameCount >= _nextAimFrame)
        {
            _nextAimFrame = -1;
            _shotFrame = AimAt(_shots[0].Center);
        }
        if (_shotFrame >= 0 && Time.frameCount >= _shotFrame)
        {
            // 鏡頭已就位：截目前這張。截圖在影格結束才拍，隔幾格再把鏡頭移到下一個房型。
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "layout-" + _shots[0].Layout + ".png");
            ScreenCapture.CaptureScreenshot(path);
            Log.Warning(TestHelpers.LogPrefix + "LayoutCheck screenshot " + path);
            _shots.RemoveAt(0);
            _shotFrame = -1;
            if (_shots.Count > 0) _nextAimFrame = Time.frameCount + 5;
        }
        if (_done || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        _done = true;
        string marker = Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName);
        if (!File.Exists(marker)) return;
        string prefix = TestHelpers.LogPrefix + "LayoutCheck ";
        try
        {
            List<string> failures = LayoutCheck.Run(new RimWorldGameWorld(Current.Game), out string skipReason, _shots);
            if (skipReason != null) Log.Warning(prefix + "SKIP: " + skipReason);
            else Log.Warning(prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));

            if (_shots.Count > 0)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                _shotFrame = AimAt(_shots[0].Center);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(prefix + "FAIL exception: " + ex);
        }
    }

    /// <summary>鏡頭對準房型中心，回傳等畫面穩定後要截圖的影格。</summary>
    private static int AimAt(IntVec3 center)
    {
        Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), false);
        Find.CameraDriver.JumpToCurrentMapLoc(center);
        Find.CameraDriver.SetRootPosAndSize(center.ToVector3Shifted(), 16f);
        return Time.frameCount + 30;
    }
}
