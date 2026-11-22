using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DevTools.Testing;
using HarmonyLib;
using RimAgent.Game;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

internal sealed partial class MapWorldTests
{
    [Test]
    public void SetWorkPriorityWritesVanillaSetting()
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
        List<Pawn> others = [.. Find.CurrentMap.mapPawns.FreeColonistsSpawned.Where(p => p != pawn && p.workSettings != null)];
        var failures = new List<string>();
        try
        {
            // 先關掉手動優先級，讓命令一定走「開啟」那條路；清掉其他殖民者的工作順序髒標記，看命令有沒有通知到他們。
            Current.Game.playSettings.useWorkPriorities = false;
            foreach (Pawn other in others) Traverse.Create(other.workSettings).Field("workGiversDirty").SetValue(value: false);

            CommandResult result = _world.SetWorkPriority(pawn.thingIDNumber, workType.defName, target);
            if (!result.Success) failures.Add(CommandFailed + result.Error);
            if (!Current.Game.playSettings.useWorkPriorities) failures.Add("manual priorities were not turned on for priority " + target.ToString(CultureInfo.InvariantCulture));
            if (pawn.workSettings.GetPriority(workType) != target)
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{pawn.LabelShort} {workType.defName}: vanilla priority {pawn.workSettings.GetPriority(workType)}, expected {target}"));
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
    public void SetTimeAssignmentWritesVanillaTimetable()
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
            if (!result.Success) failures.Add(CommandFailed + result.Error);
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
    public void SetTimeSpeedWritesTickManager()
    {
        if (TestHelpers.SkipWithoutMap(nameof(SetTimeSpeedWritesTickManager))) return;
        TimeSpeed original = Find.TickManager.CurTimeSpeed;
        TimeSpeed target = original == TimeSpeed.Normal ? TimeSpeed.Fast : TimeSpeed.Normal;
        var failures = new List<string>();
        try
        {
            CommandResult result = _world.SetTimeSpeed(target.ToString());
            if (!result.Success) failures.Add(CommandFailed + result.Error);
            if (Find.TickManager.CurTimeSpeed != target) failures.Add($"tick manager at {Find.TickManager.CurTimeSpeed}, expected {target}");
            if (_world.SetTimeSpeed("Ultrafast").Success) failures.Add("Ultrafast was accepted");
        }
        finally
        {
            Find.TickManager.CurTimeSpeed = original;
        }
        TestHelpers.AssertNone(failures, "set_time_speed");
    }
}
