using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DevTools.Testing;
using RimAgent.Game;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimAgent.InGameTests;

internal sealed partial class MapWorldTests
{
    /// <summary>
    /// 迫擊砲的「指定射擊目標」是 Command_VerbTarget：列出時要標成 target，按下不給目標要被拒絕且收掉選目標狀態，
    /// 給範圍外的格子要回報打不到，給範圍內的格子要真的設成砲塔的強制目標（以原版 ForcedTarget 讀回）。
    /// </summary>
    [Test]
    public void MortarGizmoTakesACellTarget()
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

    private void CheckMortarTargets(Map map, Building_TurretGun mortar, GizmoInfo gizmo, List<string> failures)
    {
        CheckGizmoIsTarget(gizmo, failures);

        CommandResult none = _world.UseGizmo(mortar.thingIDNumber, gizmo.Number, gizmo.Label, target: null);
        if (none.Success) failures.Add("pressing without a target succeeded");
        if (Find.Targeter.IsTargeting) failures.Add("targeter was left active after pressing without a target");
        if (mortar.ForcedTarget.IsValid) failures.Add("pressing without a target set a forced target");

        Verb verb = mortar.AttackVerb;
        IntVec3 pos = mortar.Position;
        IntVec3 bad = FindOutOfRangeCell(map, pos, verb);
        if (!bad.IsValid)
        {
            Log.Message(TestHelpers.LogPrefix + "mortar can hit every cell tried; skipping the out-of-range check");
        }
        else
        {
            CommandResult far = _world.UseGizmo(mortar.thingIDNumber, gizmo.Number, gizmo.Label, MapTarget.OfCell(bad.x, bad.z));
            if (far.Success) failures.Add("a cell out of range was accepted");
            else if (!far.Message.Contains("cannot", StringComparison.Ordinal)) failures.Add("out-of-range reason is unclear: " + far.Message);
            if (mortar.ForcedTarget.IsValid) failures.Add("out-of-range target became the forced target");
        }

        // 迫擊砲射程遠超過 GenRadial 的半徑上限（約 80 格），改從全圖格子挑。
        IntVec3 cell = FindInRangeCell(map, pos, verb);
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

    private static void CheckGizmoIsTarget(GizmoInfo gizmo, List<string> failures)
    {
        if (gizmo.Kind != GizmoInfo.TargetKind) failures.Add("kind is " + gizmo.Kind + ", expected target");
        if (string.IsNullOrEmpty(gizmo.TargetHint)) failures.Add("no target hint");
    }

    private static IntVec3 FindOutOfRangeCell(Map map, IntVec3 pos, Verb verb)
    {
        IntVec3 farCorner = new[]
        {
            new IntVec3(0, 0, 0), new IntVec3(map.Size.x - 1, 0, 0),
            new IntVec3(0, 0, map.Size.z - 1), new IntVec3(map.Size.x - 1, 0, map.Size.z - 1)
        }
        .OrderByDescending(c => (c - pos).LengthHorizontal).First();
        // 小地圖整張都在迫擊砲射程內，就改用低於最小射程的近處格子測拒絕。
        return verb.CanHitTarget(farCorner)
            ? GenRadial.RadialCellsAround(pos, Math.Min(verb.verbProps.minRange, 20f), useCenter: false)
                .Where(c => c.InBounds(map) && (c - pos).LengthHorizontal >= 2f && !verb.CanHitTarget(c))
                .DefaultIfEmpty(IntVec3.Invalid).First()
            : farCorner;
    }

    private static IntVec3 FindInRangeCell(Map map, IntVec3 pos, Verb verb)
    {
        float minDistance = verb.verbProps.minRange + 3f;
        float maxDistance = verb.EffectiveRange * 0.7f;
        return map.AllCells
            .Where(c => (c - pos).LengthHorizontal > minDistance && (c - pos).LengthHorizontal < maxDistance && verb.CanHitTarget(c))
            .OrderBy(c => (c - pos).LengthHorizontalSquared)
            .DefaultIfEmpty(IntVec3.Invalid)
            .First();
    }

    /// <summary>
    /// 使用物品的按鈕是 Command_Action，按下才開始選目標（CompUsable 當 ITargetingSource）：
    /// 不給目標要被拒絕並收掉選目標狀態；指一個物品或一格要被擋下；指一位用不了的殖民者要帶回遊戲的原因。
    /// 用治療機械血清（健康的殖民者用不了），不會真的用掉任何東西。
    /// </summary>
    [Test]
    public void UseItemGizmoAsksForAndChecksAColonist()
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

    private void CheckUseItemTargets(Map map, Thing serum, CompUsable usable, GizmoInfo gizmo, List<string> failures)
    {
        CommandResult none = _world.UseGizmo(serum.thingIDNumber, gizmo.Number, gizmo.Label, target: null);
        if (none.Success) failures.Add("pressing without a target succeeded");
        else if (!none.Message.Contains("target", StringComparison.Ordinal)) failures.Add("missing-target message does not mention a target: " + none.Message);
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
            else if (!vanilla.Reason.NullOrEmpty() && !rejected.Message.Contains(vanilla.Reason.CapitalizeFirst().StripTags(), StringComparison.Ordinal))
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
    public void RightClickMenuMatchesVanillaAndRunsAnOption()
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
        }
        finally
        {
            if (!item.Destroyed) item.Destroy();
        }
        CheckGotoOption(map, pawn, failures);
        CheckInvalidTargetsDontProduceMenus(pawn, item, failures);
        // 選單在這條呼叫路徑（不在 OnGUI 裡）建構選項時若出錯，原版供應者會記 log 並略過，選單變空；這裡把它當失敗。
        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "right-click menu");
    }

    private void CheckInvalidTargetsDontProduceMenus(Pawn pawn, Thing item, List<string> failures)
    {
        if (_world.GetFloatMenuOptions(pawn.thingIDNumber, MapTarget.OfCell(-5, -5)).Error.Length == 0)
            failures.Add("a cell outside the map produced a menu");
        if (_world.GetFloatMenuOptions(-1, MapTarget.OfThing(item.thingIDNumber)).Error.Length == 0)
            failures.Add("an unknown colonist produced a menu");
    }

    private void CheckMenuMatchesVanilla(Pawn pawn, MapTarget target, UnityEngine.Vector3 clickPosition, List<string> failures)
    {
        FloatMenuInfo menu = _world.GetFloatMenuOptions(pawn.thingIDNumber, target);
        if (!string.IsNullOrEmpty(menu.Error))
        {
            failures.Add($"{target}: menu error {menu.Error}");
            return;
        }
        List<FloatMenuOption> vanilla = [.. FloatMenuMakerMap.GetOptions([pawn], clickPosition, out FloatMenuContext _)
            .OrderByDescending(o => o.Priority).ThenByDescending(o => o.orderInPriority)];
        if (menu.Options.Count != vanilla.Count)
        {
            failures.Add($"{target}: {menu.Options.Count} options, vanilla {vanilla.Count}");
            return;
        }
        for (int i = 0; i < vanilla.Count; i++)
        {
            if (menu.Options[i].Number != i + 1) failures.Add(string.Create(CultureInfo.InvariantCulture, $"{target}: option {i + 1} is numbered {menu.Options[i].Number}"));
            if (!string.Equals(menu.Options[i].Label, vanilla[i].Label.StripTags(), StringComparison.Ordinal))
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{target}: option {i + 1} is \"{menu.Options[i].Label}\", vanilla \"{vanilla[i].Label}\""));
            if (menu.Options[i].Disabled != vanilla[i].Disabled) failures.Add(string.Create(CultureInfo.InvariantCulture, $"{target}: option {i + 1} disabled flag differs"));
        }
    }

    private void CheckGotoOption(Map map, Pawn pawn, List<string> failures)
    {
        IntVec3 start = pawn.Position;
        IntVec3 cell = GenRadial.RadialCellsAround(start, 8f, useCenter: false)
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
}
