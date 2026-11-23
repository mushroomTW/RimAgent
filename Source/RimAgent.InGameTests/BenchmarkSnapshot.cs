using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 一題佈置前的地圖與殖民者狀態；題目結束後還原，讓每題、每輪的起點相同。
/// 研究完成與佈置時取消的藍圖不還原。
/// </summary>
internal sealed class BenchmarkSnapshot
{
    private sealed record ZoneState(Zone Zone, HashSet<IntVec3> Cells, StorageSettings Storage, ThingDef Plant);

    private sealed record PawnState(Pawn Pawn, Dictionary<WorkTypeDef, int> Work, List<TimeAssignmentDef> Times, MedicalCareCategory? Care, bool Drafted, HashSet<Hediff> Hediffs);

    private readonly Map _map;
    private readonly List<ZoneState> _zones;
    private readonly List<PawnState> _pawns;
    private readonly HashSet<Thing> _structures;
    private readonly HashSet<Designation> _marks;
    private readonly bool _useWorkPriorities;

    public BenchmarkSnapshot(Map map)
    {
        _map = map;
        _zones = [.. map.zoneManager.AllZones.Select(z => new ZoneState(z, [.. z.Cells], Copy((z as Zone_Stockpile)?.settings), (z as Zone_Growing)?.GetPlantDefToGrow()))];
        _pawns = [.. map.mapPawns.FreeColonistsSpawned.Select(Capture)];
        _structures = [.. Structures()];
        _marks = [.. map.designationManager.AllDesignations];
        _useWorkPriorities = Current.Game.playSettings.useWorkPriorities;
    }

    public void Restore()
    {
        if (Current.Game?.Maps.Contains(_map) != true) return;
        HashSet<Zone> known = [.. _zones.Select(s => s.Zone)];
        foreach (Zone zone in _map.zoneManager.AllZones.Where(z => !known.Contains(z)).ToList()) zone.Delete();
        foreach (Thing thing in Structures().Where(t => !_structures.Contains(t)).ToList())
        {
            thing.Destroy(thing is Building and not Frame ? DestroyMode.Vanish : DestroyMode.Cancel);
        }
        foreach (Designation mark in _map.designationManager.AllDesignations.Where(d => !_marks.Contains(d)).ToList())
        {
            _map.designationManager.RemoveDesignation(mark);
        }
        _zones.ForEach(RestoreZone);
        Current.Game.playSettings.useWorkPriorities = _useWorkPriorities;
        _pawns.ForEach(RestorePawn);
    }

    private IEnumerable<Thing> Structures() =>
        _map.listerThings.AllThings.Where(t => t is Blueprint or Building && t.Faction == Faction.OfPlayer);

    private static StorageSettings Copy(StorageSettings source)
    {
        if (source == null) return null;
        var copy = new StorageSettings();
        copy.CopyFrom(source);
        return copy;
    }

    private void RestoreZone(ZoneState state)
    {
        Zone zone = state.Zone;
        foreach (IntVec3 cell in zone.Cells.Where(c => !state.Cells.Contains(c)).ToList()) zone.RemoveCell(cell);
        if (!_map.zoneManager.AllZones.Contains(zone)) _map.zoneManager.RegisterZone(zone);
        foreach (IntVec3 cell in state.Cells.Where(c => _map.zoneManager.ZoneAt(c) == null)) zone.AddCell(cell);
        if (zone is Zone_Stockpile stockpile) stockpile.settings.CopyFrom(state.Storage);
        if (zone is Zone_Growing growing) growing.SetPlantDefToGrow(state.Plant);
    }

    private static PawnState Capture(Pawn pawn) => new(
        pawn,
        DefDatabase<WorkTypeDef>.AllDefs.Where(w => pawn.workSettings?.EverWork == true && !pawn.WorkTypeIsDisabled(w)).ToDictionary(w => w, pawn.workSettings.GetPriority),
        pawn.timetable == null ? null : [.. pawn.timetable.times],
        pawn.playerSettings?.medCare,
        pawn.Drafted,
        [.. pawn.health.hediffSet.hediffs]);

    private static void RestorePawn(PawnState state)
    {
        Pawn pawn = state.Pawn;
        if (pawn.Dead || pawn.Destroyed) return;
        foreach (KeyValuePair<WorkTypeDef, int> work in state.Work.Where(w => !pawn.WorkTypeIsDisabled(w.Key))) pawn.workSettings.SetPriority(work.Key, work.Value);
        if (state.Times != null) pawn.timetable.times = [.. state.Times];
        if (state.Care is { } care) pawn.playerSettings.medCare = care;
        if (pawn.drafter != null && pawn.Drafted != state.Drafted) pawn.drafter.Drafted = state.Drafted;
        foreach (Hediff added in pawn.health.hediffSet.hediffs.Where(h => !state.Hediffs.Contains(h)).ToList()) pawn.health.RemoveHediff(added);
    }
}
