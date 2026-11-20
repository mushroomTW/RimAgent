using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RimAgent.Game;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// check_ship_layout 與原版 ShipUtility.ShipBuildingsAttachedTo 的對照，用真實零件擺出相連、隔一格與只碰到角的情況。
/// DevTools 測試與標記檔元件共用；run_test_cycle 傳不了 DevTools 的測試參數，隔離環境靠標記檔跑。
/// </summary>
internal static class ShipLayoutCheck
{
    private const int Width = 13;
    private const int Height = 7;
    private const string GameLogged = "game logged: ";

    public static List<string> Run(RimWorldGameWorld world, out string skipReason)
    {
        skipReason = null;
        Map map = Find.CurrentMap;
        world.TakeNewGameErrors();
        var failures = new List<string>();
        CellRect? found = FindClearArea(map);
        if (found == null)
        {
            skipReason = $"no clear {Width}x{Height} area";
            return failures;
        }
        int ox = found.Value.minX, oz = found.Value.minZ;
        var spawned = new List<Building>();
        // CA1859：使用具體且可為 null 的 Blueprint_Build？而非泛型基類 Blueprint。
        Blueprint_Build casketBlueprint = null;

        Building Spawn(ThingDef def, int minX, int minZ)
        {
            Thing part = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            part.SetFaction(Faction.OfPlayer);
            var building = (Building)GenSpawn.Spawn(part, CenterFor(def, minX, minZ), map, Rot4.North);
            spawned.Add(building);
            return building;
        }

        try
        {
            // 核心與引擎 A 邊貼邊；引擎 B 與 A 隔一格；感測器與 A 只碰到角、與 B 邊貼邊。
            Building core = Spawn(ThingDefOf.Ship_ComputerCore, ox + 3, oz);
            Building engineA = Spawn(ThingDefOf.Ship_Engine, ox + 5, oz);
            Building engineB = Spawn(ThingDefOf.Ship_Engine, ox + 9, oz);
            Building sensor = Spawn(ThingDefOf.Ship_SensorCluster, ox + 8, oz + 4);
            // 休眠艙藍圖貼著核心西側，朝北時站位正好落在核心上。
            casketBlueprint = GenConstruct.PlaceBlueprintForBuild(ThingDefOf.Ship_CryptosleepCasket, CenterFor(ThingDefOf.Ship_CryptosleepCasket, ox + 2, oz), map, Rot4.North, Faction.OfPlayer, stuff: null);

            VerifyShipLayoutResult(new ShipLayoutContext
            {
                Failures = failures,
                World = world,
                Map = map,
                Core = core,
                EngineA = engineA,
                EngineB = engineB,
                Sensor = sensor,
                CasketBlueprint = casketBlueprint,
                Spawned = spawned,
                Ox = ox,
                Oz = oz
            });
        }
        finally
        {
            if (casketBlueprint is { Destroyed: false }) casketBlueprint.Destroy();
            foreach (Building part in spawned.Where(b => !b.Destroyed)) part.Destroy();
        }
        foreach (string error in world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        return failures;
    }

    /// <summary>船舶配置驗證所需的所有狀態，避免 VerifyShipLayoutResult 參數過多。</summary>
    private sealed class ShipLayoutContext
    {
        public List<string> Failures { get; init; }
        public RimWorldGameWorld World { get; init; }
        public Map Map { get; init; }
        public Building Core { get; init; }
        public Building EngineA { get; init; }
        public Building EngineB { get; init; }
        public Building Sensor { get; init; }
        public Blueprint_Build CasketBlueprint { get; init; }
        public List<Building> Spawned { get; init; }
        public int Ox { get; init; }
        public int Oz { get; init; }
    }

    /// <summary>驗證船舶配置檢查結果，將不符的項目加入 failures。</summary>
    private static void VerifyShipLayoutResult(ShipLayoutContext ctx)
    {
        string text = new RimAgent.Tools.ShipTools(ctx.World).CheckShipLayout();
        Log.Message(TestHelpers.LogPrefix + "check_ship_layout reply:\n" + text);
        string group1 = text.Split('\n').FirstOrDefault(l => l.StartsWith("Group 1:", StringComparison.Ordinal)) ?? string.Empty;
        List<Building> vanilla = [.. ShipUtility.ShipBuildingsAttachedTo(ctx.Core)];
        foreach (Building part in ctx.Spawned)
        {
            bool inGroup1 = group1.Contains("#" + part.thingIDNumber.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal);
            if (inGroup1 != vanilla.Contains(part)) ctx.Failures.Add($"{part.def.defName}: tool says group 1={inGroup1}, vanilla attached={vanilla.Contains(part)}");
        }
        if (!vanilla.Contains(ctx.EngineA) || vanilla.Contains(ctx.EngineB) || vanilla.Contains(ctx.Sensor)) ctx.Failures.Add("test layout is not what it should be");
        if (!group1.Contains("#" + ctx.CasketBlueprint.thingIDNumber.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal)) ctx.Failures.Add("casket blueprint not connected through its edge: " + group1);
        if (!text.Contains("Group 2 is not connected", StringComparison.Ordinal)) ctx.Failures.Add("gap not reported: " + text);

        IntVec3 entry = ThingUtility.InteractionCellWhenAt(ThingDefOf.Ship_CryptosleepCasket, ctx.CasketBlueprint.Position, ctx.CasketBlueprint.Rotation, ctx.Map);
        int casketId = ctx.CasketBlueprint.thingIDNumber;
        ShipPartInfo casketInfo = ctx.World.GetShipParts().FirstOrDefault(p => p.Id == casketId);
        if (casketInfo?.State != ShipPartState.Blueprint) ctx.Failures.Add("casket blueprint state: " + casketInfo?.State);
        if (casketInfo?.InteractionCell != (entry.x, entry.z)) ctx.Failures.Add($"casket entry: {casketInfo?.InteractionCell} vs {entry}");
        if (!ctx.Core.OccupiedRect().Contains(entry)) ctx.Failures.Add("test layout: casket entry should be on the core, is " + entry);
        if (!text.Contains("colonists enter from", StringComparison.Ordinal)) ctx.Failures.Add("blocked entry not reported: " + text);

        IReadOnlyList<string> removed = ctx.World.RemoveBlueprints(new CellRectSpec(ctx.Ox + 2, ctx.Oz, ctx.Ox + 2, ctx.Oz));
        if (removed.Count != 1 || !ctx.CasketBlueprint.Destroyed) ctx.Failures.Add("remove_blueprint: " + string.Join("; ", removed));
        if (ctx.Spawned.Any(b => b.Destroyed)) ctx.Failures.Add("remove_blueprint touched a finished building");
    }

    /// <summary>離地圖邊緣留一格：原版連通判定會讀零件外圍的格子，不檢查是否在圖內。</summary>
    private static CellRect? FindClearArea(Map map)
    {
        for (int cx = 1; cx + Width < map.Size.x - 1; cx += 4)
        {
            for (int cz = 1; cz + Height < map.Size.z - 1; cz += 4)
            {
                var candidate = new CellRect(cx, cz, Width, Height);
                if (candidate.Cells.All(c => !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && c.GetThingList(map).TrueForAll(t => t is Plant))) return candidate;
            }
        }
        return null;
    }

    /// <summary>以左下角指定位置，換算成原版要的中心格。</summary>
    private static IntVec3 CenterFor(ThingDef def, int minX, int minZ)
    {
        var probe = new IntVec3(minX, 0, minZ);
        CellRect occupied = GenAdj.OccupiedRect(probe, Rot4.North, def.size);
        return probe + (probe - new IntVec3(occupied.minX, 0, occupied.minZ));
    }
}

/// <summary>隔離環境 Config 有標記檔時，進圖後跑一次，結果以 "[RimAgent.InGameTests] ShipLayoutCheck PASS/FAIL" 警告輸出。</summary>
public sealed class ShipLayoutCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentShipLayoutCheck.xml";
    private bool _done;

    public ShipLayoutCheckComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (_done || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        _done = true;
        if (!File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName))) return;
        string prefix = TestHelpers.LogPrefix + "ShipLayoutCheck ";
        try
        {
            List<string> failures = ShipLayoutCheck.Run(new RimWorldGameWorld(Current.Game), out string skipReason);
            if (skipReason != null) Log.Warning(prefix + "SKIP: " + skipReason);
            else Log.Warning(prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));
        }
        catch (Exception ex)
        {
            Log.Warning(prefix + "FAIL exception: " + ex);
        }
    }
}
