using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimAgent.Game;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimAgent.InGameTests;

/// <summary>採礦、拆除、禁止、取消標記、活動區、移動、右鍵選單、指令按鈕與任務題。</summary>
public sealed partial class BenchmarkComponent
{
    private const string PenLabel = "畜欄區";

    private static readonly List<Thing> _spawned = [];
    private static readonly List<Plant> _markedTrees = [];
    private static Area_Allowed _pen;
    private static Pawn _tameAnimal;
    private static Pawn _mover;
    private static IntVec3 _moveTarget;
    private static Pawn _wearer;
    private static Apparel _hat;
    private static Building_Door _door;
    private static Quest _quest;

    private static BenchTask MineSteelTask() =>
        new()
        {
            Id = "mine_steel",
            Instruction = "殖民者附近有一小塊 3 格的鋼鐵礦。把這 3 格都標記開採，不要標記其他東西。",
            Setup = map => SpawnRow(map, 3, _ => ThingMaker.MakeThing(ThingDefOf.MineableSteel), playerOwned: false),
            Cleanup = _ => DestroySpawnedThings(),
            Measure = map => new()
            {
                [TargetKey] = _spawned.Count(t => map.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null),
                [OthersKey] = map.designationManager.AllDesignations.Count - _spawned.Count(t => map.designationManager.DesignationAt(t.Position, DesignationDefOf.Mine) != null)
            },
            Check = (before, after, _) => Fails(
                after[TargetKey] == 3 ? null : string.Create(CultureInfo.InvariantCulture, $"steel cells marked for mining {after[TargetKey]} (need 3)"),
                Delta(before, after, OthersKey) == 0 ? null : "other things were marked"),
            Solve = world => All(_spawned.Select(t => world.AddDesignationOnCell(t.Position.x, t.Position.z, DesignationDefOf.Mine.defName)))
        };

    private static BenchTask DeconstructWallTask() =>
        new()
        {
            Id = "deconstruct_wall",
            Instruction = "殖民者附近有一段 3 格長的花崗岩磚牆。把這段牆標記拆除，其他建築不要動。",
            Setup = map => SpawnRow(map, 3, _ => ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite), playerOwned: true),
            Cleanup = _ => DestroySpawnedThings(),
            Measure = map => new()
            {
                [TargetKey] = _spawned.Count(t => map.designationManager.DesignationOn(t, DesignationDefOf.Deconstruct) != null),
                [OthersKey] = map.designationManager.AllDesignations.Count - _spawned.Count(t => map.designationManager.DesignationOn(t, DesignationDefOf.Deconstruct) != null)
            },
            Check = (before, after, _) => Fails(
                after[TargetKey] == 3 ? null : string.Create(CultureInfo.InvariantCulture, $"wall cells marked for deconstruction {after[TargetKey]} (need 3)"),
                Delta(before, after, OthersKey) == 0 ? null : "other things were marked"),
            Solve = world => All(_spawned.Select(t => world.AddDesignationOnThing(t.thingIDNumber, DesignationDefOf.Deconstruct.defName)))
        };

    /// <summary>旁邊另放一堆被禁止的鋼鐵，整片解除就會失敗。</summary>
    private static BenchTask UnforbidWoodTask() =>
        new()
        {
            Id = "unforbid_wood",
            Instruction = "殖民者附近有一堆被禁止的木材。解除禁止讓殖民者可以搬運；旁邊被禁止的鋼鐵維持禁止。",
            Setup = map => SpawnRow(map, 3, i => i switch { 0 => Stack(ThingDefOf.WoodLog, 50), 2 => Stack(ThingDefOf.Steel, 50), _ => null }, playerOwned: false, forbid: true),
            Cleanup = _ => DestroySpawnedThings(),
            Measure = _ => new()
            {
                [TargetKey] = _spawned.Count(t => t.def == ThingDefOf.WoodLog && t.Spawned && !t.IsForbidden(Faction.OfPlayer)),
                [OthersKey] = _spawned.Count(t => t.def == ThingDefOf.Steel && t.Spawned && !t.IsForbidden(Faction.OfPlayer))
            },
            Check = (_, after, _) => Fails(
                after[TargetKey] == 1 ? null : "the wood is still forbidden",
                after[OthersKey] == 0 ? null : "the steel was unforbidden"),
            Solve = world =>
            {
                IntVec3 wood = _spawned.First(t => t.def == ThingDefOf.WoodLog).Position;
                return world.SetForbidden(new CellRectSpec(wood.x, wood.z, wood.x, wood.z), null, forbidden: false);
            }
        };

    private static BenchTask CancelCutTask() =>
        new()
        {
            Id = "cancel_cut",
            Instruction = "剛才誤把殖民者附近 4 棵樹標記成砍伐了。把這 4 個砍伐標記取消，其他標記不要動。",
            Setup = map =>
            {
                IntVec3 center = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
                _markedTrees.Clear();
                _markedTrees.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                    .Where(p => p.def.plant.IsTree && !p.Position.Fogged(map) && !map.designationManager.AllDesignationsOn(p).Any())
                    .OrderBy(p => (p.Position - center).LengthHorizontalSquared).Take(4));
                if (_markedTrees.Count < 4) return "fewer than 4 unmarked trees";
                foreach (Plant tree in _markedTrees) map.designationManager.AddDesignation(new Designation(tree, DesignationDefOf.CutPlant));
                return null;
            },
            Cleanup = _ => _markedTrees.Clear(),
            Measure = map => new()
            {
                [TargetKey] = _markedTrees.Count(t => map.designationManager.DesignationOn(t, DesignationDefOf.CutPlant) != null),
                [OthersKey] = map.designationManager.AllDesignations.Count - _markedTrees.Count(t => map.designationManager.DesignationOn(t, DesignationDefOf.CutPlant) != null)
            },
            Check = (before, after, _) => Fails(
                after[TargetKey] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after[TargetKey]} of the 4 trees are still marked"),
                Delta(before, after, OthersKey) == 0 ? null : "other marks changed"),
            Solve = world => All(_markedTrees.Select(t => world.RemoveDesignationOnThing(t.thingIDNumber, DesignationDefOf.CutPlant.defName)))
        };

    private static BenchTask AnimalAreaTask() =>
        new()
        {
            Id = "animal_area",
            Instruction = $"殖民地有一頭剛馴服的羊駝（alpaca）。把牠的活動區域限制在「{PenLabel}」。",
            Setup = map =>
            {
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Alpaca");
                if (kind == null) return "missing pawn kind Alpaca";
                if (!map.areaManager.CanMakeNewAllowed() || !map.areaManager.TryMakeNewAllowed(out _pen)) return "no room for another allowed area";
                _pen.SetLabel(PenLabel);
                IntVec3 cell = NearColonists(map);
                foreach (IntVec3 c in CellRect.CenteredOn(cell, 2).ClipInsideMap(map)) _pen[c] = true;
                _tameAnimal = (Pawn)GenSpawn.Spawn(PawnGenerator.GeneratePawn(kind, Faction.OfPlayer), cell, map);
                return null;
            },
            Cleanup = _ =>
            {
                DestroySpawned(ref _tameAnimal);
                _pen?.Delete();
                _pen = null;
            },
            Measure = _ => new() { [TargetKey] = _pen != null && _tameAnimal?.playerSettings?.AreaRestrictionInPawnCurrentMap == _pen ? 1 : 0 },
            Check = (_, after, _) => after[TargetKey] == 1 ? null : "the alpaca is not restricted to " + PenLabel,
            Solve = world => world.SetAnimalArea(_tameAnimal.thingIDNumber, PenLabel)
        };

    private static BenchTask DraftMoveTask()
    {
        var task = new BenchTask
        {
            Id = "draft_move",
            Tier = Hard,
            Instruction = "徵召指定的殖民者，命令這位殖民者移動到指定的格子。",
            Measure = map => new()
            {
                [TargetKey] = _mover is { Drafted: true } && (_mover.Position == _moveTarget || _mover.pather.Destination.Cell == _moveTarget) ? 1 : 0,
                [OthersKey] = map.mapPawns.FreeColonistsSpawned.Count(p => p != _mover && p.Drafted)
            },
            Check = (before, after, _) => Fails(
                after[TargetKey] == 1 ? null : string.Create(CultureInfo.InvariantCulture, $"{_mover?.LabelShort} is not drafted and heading to ({_moveTarget.x},{_moveTarget.z})"),
                Delta(before, after, OthersKey) <= 0 ? null : "other colonists were drafted"),
            Solve = world => world.SetDrafted(_mover.thingIDNumber, drafted: true) is { Success: false } draft ? draft : world.OrderMoveTo(_mover.thingIDNumber, _moveTarget.x, _moveTarget.z)
        };
        task.Setup = map =>
        {
            _mover = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.drafter != null && !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent));
            if (_mover == null) return "no colonist can be drafted";
            List<IntVec3> spots = [.. GenRadial.RadialCellsAround(_mover.Position, 10f, useCenter: false)
                .Where(c => (c - _mover.Position).LengthHorizontalSquared >= 36 && c.InBounds(map) && c.Standable(map) && !c.Fogged(map) && c.GetFirstPawn(map) == null
                    && map.reachability.CanReach(_mover.Position, c, PathEndMode.OnCell, TraverseParms.For(_mover)))];
            if (spots.Count == 0) return "no reachable cell near the colonist";
            _moveTarget = spots[0];
            task.Instruction = string.Create(CultureInfo.InvariantCulture, $"徵召殖民者 {_mover.LabelShort}，命令這位殖民者移動到 ({_moveTarget.x},{_moveTarget.z})。其他殖民者不要徵召。");
            return null;
        };
        task.Cleanup = _ => _mover = null;
        return task;
    }

    /// <summary>挑沒戴帽子的殖民者，穿上不必換掉身上的衣服，還原時只要拿掉帽子。</summary>
    private static BenchTask WearHatTask()
    {
        var task = new BenchTask
        {
            Id = "wear_hat",
            Tier = Hard,
            Instruction = "讓指定的殖民者去穿上地上的帽子。",
            Measure = _ => new()
            {
                [TargetKey] = _wearer != null && _hat != null
                    && (_wearer.apparel.WornApparel.Contains(_hat) || (_wearer.CurJobDef == JobDefOf.Wear && _wearer.CurJob.targetA.Thing == _hat)) ? 1 : 0
            },
            Check = (_, after, _) => after[TargetKey] == 1 ? null : string.Create(CultureInfo.InvariantCulture, $"{_wearer?.LabelShort} is not wearing or going to wear the hat"),
            Solve = world =>
            {
                FloatMenuInfo menu = world.GetFloatMenuOptions(_wearer.thingIDNumber, MapTarget.OfThing(_hat.thingIDNumber));
                string label = "ForceWear".Translate(_hat.LabelShort, _hat).Resolve();
                FloatMenuOptionInfo wear = menu.Options.FirstOrDefault(o => !o.Disabled && o.Label.Contains(label, System.StringComparison.Ordinal));
                return wear == null ? CommandResult.Fail("no wear option: " + menu.Error) : world.UseFloatMenuOption(_wearer.thingIDNumber, MapTarget.OfThing(_hat.thingIDNumber), wear.Number, wear.Label);
            }
        };
        task.Setup = map =>
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_CowboyHat");
            if (def == null) return "missing Apparel_CowboyHat";
            _wearer = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.apparel != null && !p.Downed && !p.Drafted
                && !p.apparel.WornApparel.Any(a => a.def.apparel.layers.Contains(ApparelLayerDefOf.Overhead)));
            if (_wearer == null) return "every colonist already wears a hat";
            _hat = (Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            if (!GenPlace.TryPlaceThing(_hat, _wearer.Position, map, ThingPlaceMode.Near)) return "could not place the hat";
            _hat.SetForbidden(value: false, warnOnFail: false);
            task.Instruction = $"{_wearer.LabelShort} 旁邊的地上有一頂{_hat.LabelNoCount}。讓 {_wearer.LabelShort} 現在就去把它穿上。";
            return null;
        };
        task.Cleanup = _ =>
        {
            if (_wearer?.CurJob?.targetA.Thing == _hat) _wearer.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (_hat is { Destroyed: false }) _hat.Destroy();
            _hat = null;
            _wearer = null;
        };
        return task;
    }

    private static BenchTask DoorHoldOpenTask() =>
        new()
        {
            Id = "door_hold_open",
            Tier = Hard,
            Instruction = "殖民者附近有一扇單獨立著的木門。把這扇門設成保持開啟。",
            Setup = map =>
            {
                _door = SpawnBuilding(map, "Door") as Building_Door;
                return _door == null ? "could not place a door near the colonists" : null;
            },
            Cleanup = _ =>
            {
                if (_door is { Destroyed: false }) _door.Destroy();
                _door = null;
            },
            Measure = _ => new() { [TargetKey] = _door?.HoldOpen == true ? 1 : 0 },
            Check = (_, after, _) => after[TargetKey] == 1 ? null : "the door is not held open",
            Solve = world =>
            {
                string label = "CommandToggleDoorHoldOpen".Translate();
                GizmoInfo gizmo = world.GetGizmos(_door.thingIDNumber).FirstOrDefault(g => g.Label == label);
                return gizmo == null ? CommandResult.Fail("no hold open button") : world.UseGizmo(_door.thingIDNumber, gizmo.Number, gizmo.Label, null);
            }
        };

    private static BenchTask AcceptQuestTask()
    {
        var task = new BenchTask
        {
            Id = "accept_quest",
            Tier = Hard,
            Instruction = "接受指定的任務。",
            Measure = _ => new() { [TargetKey] = _quest?.State == QuestState.Ongoing ? 1 : 0 },
            Check = (_, after, _) => after[TargetKey] == 1 ? null : $"quest \"{_quest?.name}\" was not accepted",
            Solve = world =>
            {
                int accepter = _quest.RequiresAccepter
                    ? PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended.FirstOrDefault(p => QuestUtility.CanPawnAcceptQuest(p, _quest))?.thingIDNumber ?? 0
                    : 0;
                int reward = _quest.PartsListForReading.OfType<QuestPart_Choice>().Any() ? 1 : 0;
                return world.AcceptQuest(_quest.id, reward, accepter);
            }
        };
        task.Setup = _ =>
        {
            _quest = MapWorldTests.GenerateManualQuest("OpportunitySite_BanditCamp", "TradeRequest");
            if (_quest == null) return "no manually accepted quest could be generated on this world";
            if (!QuestUtility.CanAcceptQuest(_quest).Accepted) return "the generated quest cannot be accepted: " + QuestUtility.CanAcceptQuest(_quest).Reason;
            task.Instruction = $"任務清單裡有一個新任務「{_quest.name}」。接受這個任務；要選獎勵時選第一個。其他任務不要動。";
            return null;
        };
        task.Cleanup = _ =>
        {
            if (_quest == null) return;
            if (!_quest.Historical) _quest.End(QuestEndOutcome.Unknown, sendLetter: false, playSound: false);
            Find.QuestManager.Remove(_quest);
            _quest = null;
        };
        return task;
    }

    /// <summary>在殖民者附近找一排完全空著的格子，依序放上 <paramref name="make"/> 做出的東西（回傳 null 的格子留空）。</summary>
    private static string SpawnRow(Map map, int length, System.Func<int, Thing> make, bool playerOwned, bool forbid = false)
    {
        _spawned.Clear();
        IntVec3 center = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
        foreach (IntVec3 start in GenRadial.RadialCellsAround(center, 25f, useCenter: false))
        {
            List<IntVec3> row = [.. Enumerable.Range(0, length).Select(i => start + new IntVec3(i, 0, 0))];
            if (!row.All(c => c.InBounds(map) && IsOpen(map, c) && c.GetThingList(map).Count == 0)) continue;
            for (int i = 0; i < length; i++)
            {
                if (make(i) is not { } thing) continue;
                if (playerOwned) thing.SetFaction(Faction.OfPlayer);
                Thing placed = GenSpawn.Spawn(thing, row[i], map);
                if (forbid) placed.SetForbidden(value: true, warnOnFail: false);
                _spawned.Add(placed);
            }
            return null;
        }
        return string.Create(CultureInfo.InvariantCulture, $"no empty row of {length} cells near the colonists");
    }

    private static Thing Stack(ThingDef def, int count)
    {
        Thing thing = ThingMaker.MakeThing(def);
        thing.stackCount = count;
        return thing;
    }

    private static void DestroySpawnedThings()
    {
        foreach (Thing thing in _spawned.Where(t => !t.Destroyed)) thing.Destroy();
        _spawned.Clear();
    }

    private static CommandResult All(IEnumerable<CommandResult> results) =>
        results.FirstOrDefault(r => !r.Success) ?? CommandResult.Ok(string.Empty);
}
