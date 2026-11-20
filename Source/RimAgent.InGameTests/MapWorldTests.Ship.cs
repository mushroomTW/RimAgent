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
    /// <summary>
    /// 飛船結局的機制路徑（不發射）：在空地生成一整艘船，電腦核心的發射鈕要帶停用原因、反應爐要有啟動鈕，
    /// 零件的發射報告鈕經 use_gizmo 開出對話框並列出完整原因，模型才讀得到。結束後拆掉並關掉對話框。
    /// </summary>
    [Test]
    public void ShipPartsExposeStartupLaunchAndReport()
    {
        if (TestHelpers.SkipWithoutMap(nameof(ShipPartsExposeStartupLaunchAndReport))) return;
        Map map = Find.CurrentMap;
        _world.TakeNewGameErrors();
        // 由西到東排成一列、底邊對齊，相鄰兩件一定以東西向相接。
        ThingDef[] parts =
        [
            ThingDefOf.Ship_Reactor, ThingDefOf.Ship_Engine, ThingDefOf.Ship_Engine, ThingDefOf.Ship_Engine,
            ThingDefOf.Ship_ComputerCore, ThingDefOf.Ship_SensorCluster, ThingDefOf.Ship_CryptosleepCasket, ThingDefOf.Ship_Beam
        ];
        CellRect? found = FindShipArea(map, parts);
        if (found == null)
        {
            TestHelpers.Skip(nameof(ShipPartsExposeStartupLaunchAndReport), $"no clear unroofed {parts.Sum(p => p.size.x)}x{parts.Max(p => p.size.z)} area near the colonists");
            return;
        }
        var windowsBefore = new HashSet<Window>(Find.WindowStack.Windows);
        var failures = new List<string>();
        List<Building> spawned = [];
        try
        {
            spawned = SpawnShipParts(map, parts, found.Value);
            Building core = spawned.First(b => b.def == ThingDefOf.Ship_ComputerCore);
            Building reactor = spawned.First(b => b.def == ThingDefOf.Ship_Reactor);
            List<string> vanillaReasons = [.. ShipUtility.LaunchFailReasons(core)];
            if (vanillaReasons.Any(r => r.StartsWith("ShipReportMissingPart".Translate(), StringComparison.Ordinal)))
                failures.Add("test layout is not connected: " + string.Join("; ", vanillaReasons));

            CheckLaunchGizmo(core, failures);
            CheckStartupGizmo(reactor, failures);
            Building beam = spawned.First(b => b.def == ThingDefOf.Ship_Beam);
            CheckReportGizmo(beam, failures);
        }
        finally
        {
            foreach (Window window in Find.WindowStack.Windows.Where(w => !windowsBefore.Contains(w)).ToList()) window.Close(false);
            foreach (Building part in spawned.Where(b => !b.Destroyed)) part.Destroy();
        }
        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "ship parts");
    }

    /// <summary>在全圖每 4 格試一個角落，找到可放下整艘船（不霧、可站、無屋頂、無建築、全為植物）的空地。</summary>
    private static CellRect? FindShipArea(Map map, ThingDef[] parts)
    {
        int width = parts.Sum(p => p.size.x);
        int height = parts.Max(p => p.size.z);
        Predicate<IntVec3> clear = c => !c.Fogged(map) && c.Standable(map) && !map.roofGrid.Roofed(c)
            && c.GetEdifice(map) == null && c.GetThingList(map).All(t => t is Plant);
        // 離地圖邊緣留一格：原版 ShipUtility.ShipBuildingsAttachedTo 會讀零件外圍的格子，不檢查是否在圖內。
        for (int cx = 1; cx + width < map.Size.x - 1; cx += 4)
        {
            for (int cz = 1; cz + height < map.Size.z - 1; cz += 4)
            {
                var candidate = new CellRect(cx, cz, width, height);
                if (candidate.Cells.All(c => clear(c))) return candidate;
            }
        }
        return null;
    }

    /// <summary>依零件尺寸由西到東在空地上生成船上的全部建築。</summary>
    private static List<Building> SpawnShipParts(Map map, ThingDef[] parts, CellRect found)
    {
        var spawned = new List<Building>();
        int x = found.minX;
        foreach (ThingDef def in parts)
        {
            var probe = new IntVec3(x, 0, found.minZ);
            CellRect occupied = GenAdj.OccupiedRect(probe, Rot4.North, def.size);
            IntVec3 center = probe + (probe - new IntVec3(occupied.minX, 0, occupied.minZ));
            Thing part = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            part.SetFaction(Faction.OfPlayer);
            spawned.Add((Building)GenSpawn.Spawn(part, center, map, Rot4.North));
            x += def.size.x;
        }
        return spawned;
    }

    /// <summary>檢查電腦核心的發射按鈕：在啟動前應為停用並帶原因。</summary>
    private void CheckLaunchGizmo(Building core, List<string> failures)
    {
        string launchLabel = "CommandShipLaunch".Translate();
        GizmoInfo launch = _world.GetGizmos(core.thingIDNumber)?.FirstOrDefault(g => g.Label == launchLabel);
        if (launch == null) failures.Add("the computer core has no launch button");
        else if (!launch.Disabled || string.IsNullOrEmpty(launch.DisabledReason)) failures.Add("launch is not disabled with a reason before startup");
    }

    /// <summary>檢查反應爐的啟動按鈕：應存在且種類為動作。</summary>
    private void CheckStartupGizmo(Building reactor, List<string> failures)
    {
        string startupLabel = "CommandShipStartup".Translate();
        GizmoInfo startup = _world.GetGizmos(reactor.thingIDNumber)?.FirstOrDefault(g => g.Label == startupLabel);
        if (startup == null) failures.Add("the hibernating reactor has no startup button");
        else if (startup.Kind != GizmoInfo.ActionKind) failures.Add("startup button kind is " + startup.Kind);
    }

    /// <summary>檢查光束的發射報告按鈕：按下應成功，且「無法發射」對話框應列在待處理選擇中。</summary>
    private void CheckReportGizmo(Building beam, List<string> failures)
    {
        string reportLabel = "CommandShipLaunchReport".Translate();
        GizmoInfo report = _world.GetGizmos(beam.thingIDNumber)?.FirstOrDefault(g => g.Label == reportLabel);
        if (report == null)
        {
            failures.Add("the beam has no launch report button");
        }
        else
        {
            CommandResult pressed = _world.UseGizmo(beam.thingIDNumber, report.Number, report.Label, null);
            if (!pressed.Success) failures.Add("pressing the launch report failed: " + pressed.Error);
            string cannot = "ShipReportCannotLaunch".Translate();
            if (!_world.GetPendingChoices().Any(c => c.Text.Contains(cannot)))
                failures.Add("the launch report dialog is not listed among pending choices");
        }
    }

    [Test]
    public void ShipLayoutCheckMatchesVanillaConnectivity()
    {
        if (TestHelpers.SkipWithoutMap(nameof(ShipLayoutCheckMatchesVanillaConnectivity))) return;
        List<string> failures = ShipLayoutCheck.Run(_world, out string skipReason);
        if (skipReason != null)
        {
            TestHelpers.Skip(nameof(ShipLayoutCheckMatchesVanillaConnectivity), skipReason);
            return;
        }
        TestHelpers.AssertNone(failures, "ship layout");
    }
}
