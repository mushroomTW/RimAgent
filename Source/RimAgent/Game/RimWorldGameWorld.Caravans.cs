#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    private const string CaravanNotFound = "caravan not found";


    /// <summary>原版組隊視窗對背包的算法（非重組模式）。</summary>
    private const IgnorePawnsInventoryMode CaravanInventoryMode = IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload;

    /// <summary>
    /// 組隊視窗的草稿：選好的成員與貨物（原版計算器吃 <see cref="TransferableOneWay"/>）、算好的規劃，
    /// 以及送出所需的起始格、出口與集合點。成員一人一個 transferable，避免原版把同種動物疊在一起後只取前 N 隻。
    /// </summary>
    private sealed class CaravanDraft
    {
        public readonly List<Pawn> Pawns = [];
        public readonly List<TransferableOneWay> Transferables = [];
        public PlanetTile Destination = PlanetTile.Invalid;
        public PlanetTile StartingTile = PlanetTile.Invalid;
        public IntVec3 ExitSpot = IntVec3.Invalid;
        public IntVec3 MeetingPoint = IntVec3.Invalid;
        public CaravanPlanInfo Plan = new();
    }

    public IReadOnlyList<CaravanCargoInfo> GetCaravanCargo()
    {
        // AllReachableColonyItems 回傳共用的暫存清單，先複製再分組。
        return [.. CaravanFormingUtility.AllReachableColonyItems(Map, allowEvenIfOutsideHomeArea: false, allowEvenIfReserved: false, canMinify: false)
            .GroupBy(t => t.GetInnerIfMinified().def)
            .Select(g => new CaravanCargoInfo
            {
                DefName = g.Key.defName,
                Label = string.IsNullOrEmpty(g.Key.label) ? g.Key.defName : g.Key.label,
                Count = g.Sum(t => t.stackCount),
                MassEach = g.Key.BaseMass
            })
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)];
    }

    public CaravanPlanInfo PlanCaravan(CaravanRequest request)
    {
        return BuildCaravanDraft(request).Plan;
    }

    public CommandResult FormCaravan(CaravanRequest request)
    {
        CaravanDraft draft = BuildCaravanDraft(request);
        CaravanPlanInfo plan = draft.Plan;
        if (!string.IsNullOrEmpty(plan.Error)) return CommandResult.Fail(plan.Error);
        if (plan.Blockers.Count > 0) return CommandResult.Fail(string.Join("; ", plan.Blockers.ToArray()));

        // 與視窗 TryFormAndSendCaravan 最後一步相同：倒地的另外列，由同伴扛到出口。
        CaravanFormingUtility.StartFormingCaravan(
            [.. draft.Pawns.Where(p => !p.Downed)], [.. draft.Pawns.Where(p => p.Downed)], Faction.OfPlayer,
            draft.Transferables, draft.MeetingPoint, draft.ExitSpot, draft.StartingTile, draft.Destination);
        Messages.Message("CaravanFormationProcessStarted".Translate(), draft.Pawns[0], MessageTypeDefOf.PositiveEvent, historical: false);

        string members = string.Join(", ", draft.Pawns.Select(p => p.LabelShort).ToArray());
        string cargo;
        if (plan.Cargo.Count == 0) cargo = "no cargo";
        else cargo = string.Join(", ", plan.Cargo.Select(c => c.Label + " x" + c.Count.ToString(CultureInfo.InvariantCulture)).ToArray());
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Started forming a caravan with {members}; they gather {cargo} at ({draft.MeetingPoint.x},{draft.MeetingPoint.z}) ") +
                                string.Create(CultureInfo.InvariantCulture, $"and then leave the map at ({draft.ExitSpot.x},{draft.ExitSpot.z}) for tile {draft.Destination.tileId}"));
    }

    private static CaravanDraft RejectCaravan(CaravanDraft draft, string error)
    {
        draft.Plan = new CaravanPlanInfo { Error = error };
        return draft;
    }

    private static CaravanDraft BuildCaravanDraft(CaravanRequest request)
    {
        var draft = new CaravanDraft();
        if (request == null) return RejectCaravan(draft, "no caravan requested");
        Map map = Map;
        if (map == null) return RejectCaravan(draft, "there is no current map");

        // 成員：只收組隊視窗會列出的人（AllSendablePawns），其餘說明為什麼不行。
        List<Pawn> sendable = Dialog_FormCaravan.AllSendablePawns(map, reform: false);
        if (!ValidateCaravanPawns(request, sendable, map, draft, out var rejectReason)) return rejectReason;

        // 貨物：與視窗物品頁同一份清單（AddItemsToTransferables），同種物品依原版規則併成一行。
        var items = new List<TransferableOneWay>();
        foreach (Thing thing in CaravanFormingUtility.AllReachableColonyItems(map, allowEvenIfOutsideHomeArea: false, allowEvenIfReserved: false, canMinify: false).ToList())
        {
            TransferableOneWay transferable = TransferableUtility.TransferableMatching(thing, items, TransferAsOneMode.PodsOrCaravanPacking);
            if (transferable == null)
            {
                transferable = new TransferableOneWay();
                items.Add(transferable);
            }
            if (!transferable.things.Contains(thing)) transferable.things.Add(thing);
        }
        CaravanDraft cargoReject = ValidateCaravanCargo(request, items, draft, out var cargoLines);
        if (cargoReject != null) return cargoReject;
        draft.Transferables.AddRange(items.Where(t => t.CountToTransfer > 0));

        FillCaravanPlan(draft, request, map, cargoLines);
        return draft;
    }

    private static CaravanDraft ValidateCaravanCargo(CaravanRequest request, List<TransferableOneWay> items, CaravanDraft draft, out List<CaravanPlanCargo> cargoLines)
    {
        cargoLines = new List<CaravanPlanCargo>();
        foreach (TradeLine line in request.Cargo ?? [])
        {
            if (!TryAllocateCargoLine(line, items, out var cargoLine, out var rejectReason)) return RejectCaravan(draft, rejectReason);
            cargoLines.Add(cargoLine);
        }
        return null;
    }

    private static bool TryAllocateCargoLine(TradeLine line, List<TransferableOneWay> items, out CaravanPlanCargo cargoLine, out string rejectReason)
    {
        cargoLine = null;
        rejectReason = null;
        int remaining = line.Count;
        ThingDef def = null;
        foreach (TransferableOneWay transferable in items)
        {
            if (remaining <= 0) break;
            ThingDef inner = transferable.AnyThing.GetInnerIfMinified().def;
            if (!string.Equals(inner.defName, line.DefName, StringComparison.OrdinalIgnoreCase)) continue;
            def = inner;
            int take = Math.Min(remaining, transferable.GetMaximumToTransfer() - transferable.CountToTransfer);
            if (take <= 0) continue;
            transferable.AdjustTo(transferable.CountToTransfer + take);
            remaining -= take;
        }
        if (def == null) { rejectReason = $"the colony has no {line.DefName} to carry; the game only lists items in storage or the home area that nobody has reserved"; return false; }
        string label = string.IsNullOrEmpty(def.label) ? def.defName : def.label;
        if (remaining > 0) { rejectReason = string.Create(CultureInfo.InvariantCulture, $"the colony can only supply {line.Count - remaining} {label} for the caravan (asked for {line.Count}); the game only lists items in storage or the home area that nobody has reserved"); return false; }
        cargoLine = new CaravanPlanCargo { DefName = def.defName, Label = label, Count = line.Count, MassEach = def.BaseMass };
        return true;
    }

    private static bool ValidateCaravanPawns(CaravanRequest request, List<Pawn> sendable, Map map, CaravanDraft draft, out CaravanDraft rejectReason)
    {
        rejectReason = null;
        foreach (int id in (request.PawnIds ?? []).Distinct())
        {
            Pawn pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
            if (pawn == null) { rejectReason = RejectCaravan(draft, string.Create(CultureInfo.InvariantCulture, $"no pawn with id {id} is on this map")); return false; }
            if (!sendable.Contains(pawn)) { rejectReason = RejectCaravan(draft, $"{pawn.LabelShort} cannot join a caravan: {WhyNotSendable(pawn)}"); return false; }
            // 被徵召的人不吃集結的職責（原版「強制出發」也會先解除徵召）。
            if (pawn.Drafted) { rejectReason = RejectCaravan(draft, $"{pawn.LabelShort} is drafted; undraft them first"); return false; }
            draft.Pawns.Add(pawn);
            var transferable = new TransferableOneWay();
            transferable.things.Add(pawn);
            transferable.AdjustTo(1);
            draft.Transferables.Add(transferable);
        }
        return true;
    }

    private static string WhyNotSendable(Pawn pawn)
    {
        if (pawn.Dead) return "it is dead";
        if (pawn.Faction != Faction.OfPlayer && !pawn.IsPrisonerOfColony) return "it does not belong to the colony";
        if (!pawn.RaceProps.allowedOnCaravan) return "this kind of creature cannot travel with caravans";
        if (pawn.IsQuestHelper() || pawn.IsQuestLodger()) return "quest guests cannot join caravans";
        if (pawn.InMentalState) return "it is in a mental break";
        if (pawn.IsPrisoner && !pawn.guest.PrisonerIsSecure) return "the prisoner is not secured in a cell";
        if (pawn.GetLord() != null) return "it is busy with a group activity";
        return "the game does not offer it for caravans";
    }

    private static void FillCaravanPlan(CaravanDraft draft, CaravanRequest request, Map map, List<CaravanPlanCargo> cargoLines)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        PlanetTile origin = map.Tile;
        var plan = new CaravanPlanInfo { Members = [.. draft.Pawns.Select(ToCaravanMember)], Cargo = cargoLines };

        if (!origin.LayerDef.canFormCaravans) blockers.Add("caravans cannot be formed on this world layer");

        PlanetTile destination = ResolveCaravanDestination(request, origin, plan, blockers);
        plan.DestinationReachable = destination.Valid;
        if (destination.Valid)
        {
            draft.Destination = destination;
            draft.StartingTile = CaravanExitMapUtility.BestExitTileToGoTo(destination, map);
            plan.DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(origin, destination);
            if (!draft.StartingTile.Valid) blockers.Add("MessageNoValidExitTile".Translate().Resolve());
        }

        // 數字：與 Dialog_FormCaravan 的 MassUsage／MassCapacity／TilesPerDay／DaysWorthOfFood／ForagedFoodPerDay／Visibility／TicksToArrive 同一組呼叫。
        List<TransferableOneWay> transferables = draft.Transferables;
        plan.MassUsage = CollectionsMassCalculator.MassUsageTransferables(transferables, CaravanInventoryMode, includePawnsMass: false, ignoreSpawnedCorpsesGearAndInventory: false);
        plan.MassCapacity = CollectionsMassCalculator.CapacityTransferables(transferables, new StringBuilder());
        if (draft.Pawns.Count > 0)
        {
            plan.TilesPerDay = TilesPerDayCalculator.ApproxTilesPerDay(transferables, plan.MassUsage, plan.MassCapacity, origin, draft.StartingTile, isShuttle: false, explanation: null);
            plan.TicksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(draft.Pawns, plan.MassUsage, plan.MassCapacity, isShuttle: false, explanation: null);
        }
        CalculateCaravanFoodAndRot(transferables, origin, destination, plan);

        var foraged = ForagedFoodPerDayCalculator.ForagedFoodPerDay(transferables, map.Biome, Faction.OfPlayer, new StringBuilder());
        plan.ForagedFoodLabel = foraged.Item1 != null ? foraged.Item1.label : string.Empty;
        plan.ForagedPerDay = foraged.Item2;
        plan.Visibility = CaravanVisibilityCalculator.Visibility(transferables, new StringBuilder());

        // 送出時的拒絕（CheckForErrors）。
        if (!draft.Pawns.Any(p => CaravanUtility.IsOwner(p, Faction.OfPlayer) && !p.Downed))
            blockers.Add((ModsConfig.IdeologyActive ? "CaravanMustHaveAtLeastOneNonSlaveColonist" : "CaravanMustHaveAtLeastOneColonist").Translate().Resolve());
        if (plan.MassUsage > plan.MassCapacity) blockers.Add("TooBigCaravanMassUsage".Translate().Resolve());
        AddUnreachableItemBlockers(draft.Pawns, transferables, blockers);

        // 警告（TrySend 的「確定嗎」清單，只收我們能原樣算的幾項）。
        AddCaravanWarnings(plan, transferables, origin, draft.Pawns, warnings);

        // 出口與集合點（TryFormAndSendCaravan）。起始格或隊伍不成立時不必找。
        if (draft.StartingTile.Valid && draft.Pawns.Any(p => !p.Downed))
            FindCaravanSpots(draft, map, blockers, warnings);

        plan.Blockers = blockers;
        plan.Warnings = warnings;
        draft.Plan = plan;
    }

    private static PlanetTile ResolveCaravanDestination(CaravanRequest request, PlanetTile origin, CaravanPlanInfo plan, List<string> blockers)
    {
        PlanetTile destination = PlanetTile.Invalid;
        if (request.DestinationTile < 0)
        {
            blockers.Add("MessageMustChooseRouteFirst".Translate().Resolve());
        }
        else
        {
            PlanetTile asked = ToPlanetTile(request.DestinationTile, origin);
            if (!TileInWorld(asked)) blockers.Add(string.Create(CultureInfo.InvariantCulture, $"tile {request.DestinationTile} does not exist on this layer of the world"));
            else if (!Find.WorldReachability.CanReach(origin, asked)) blockers.Add(string.Create(CultureInfo.InvariantCulture, $"tile {asked.tileId} cannot be reached by land from here"));
            else destination = asked;
            plan.DestinationTile = request.DestinationTile;
            plan.DestinationLabel = TileInWorld(asked) ? DescribeWorldTile(asked) : string.Empty;
        }
        return destination;
    }

    private static void CalculateCaravanFoodAndRot(List<TransferableOneWay> transferables, PlanetTile origin, PlanetTile destination, CaravanPlanInfo plan)
    {
        if (destination.Valid)
        {
            using WorldPath path = origin.Layer.Pather.FindPath(origin, destination, caravan: null);
            plan.FoodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, origin, CaravanInventoryMode, Faction.OfPlayer, path, 0f, plan.TicksPerMove);
            plan.DaysUntilRot = DaysUntilRotCalculator.ApproxDaysUntilRot(transferables, origin, CaravanInventoryMode, path, 0f, plan.TicksPerMove);
            plan.EtaTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin, destination, path, 0f, plan.TicksPerMove, Find.TickManager.TicksAbs);
        }
        else
        {
            plan.FoodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, origin, CaravanInventoryMode, Faction.OfPlayer);
            plan.DaysUntilRot = DaysUntilRotCalculator.ApproxDaysUntilRot(transferables, origin, CaravanInventoryMode);
        }
    }

    private static void AddUnreachableItemBlockers(List<Pawn> pawns, List<TransferableOneWay> transferables, List<string> blockers)
    {
        foreach (TransferableOneWay transferable in transferables)
        {
            if (transferable.ThingDef.category != ThingCategory.Item || transferable.CountToTransfer <= 0) continue;
            if (IsTransferableReachable(transferable, pawns)) continue;
            blockers.Add(transferable.CountToTransfer == 1
                ? "CaravanItemIsUnreachableSingle".Translate(transferable.ThingDef.label).Resolve()
                : "CaravanItemIsUnreachableMulti".Translate(transferable.CountToTransfer, transferable.ThingDef.label).Resolve());
        }
    }

    private static bool IsTransferableReachable(TransferableOneWay transferable, List<Pawn> pawns)
    {
        int reachable = 0;
        foreach (Thing thing in transferable.things)
        {
            if (!thing.Spawned) continue;
            if (pawns.Any(p => p.IsColonist && p.CanReach(thing, PathEndMode.Touch, Danger.Deadly))) reachable += thing.stackCount;
            if (reachable >= transferable.CountToTransfer) break;
        }
        return reachable >= transferable.CountToTransfer;
    }

    private static void AddCaravanWarnings(CaravanPlanInfo plan, List<TransferableOneWay> transferables, PlanetTile origin, List<Pawn> pawns, List<string> warnings)
    {
        if (plan.FoodDays < 5f)
        {
            warnings.Add(plan.FoodDays < 0.1f
                ? "DaysWorthOfFoodWarningDialog_NoFood".Translate().Resolve()
                : "DaysWorthOfFoodWarningDialog".Translate(plan.FoodDays.ToString("0.#", CultureInfo.InvariantCulture)).Resolve());
        }
        else if (MostCaravanFoodWillRotSoon(transferables, origin))
        {
            warnings.Add("CaravanFoodWillRotSoonWarningDialog".Translate().Resolve());
        }
        if (!pawns.Any(p => CaravanUtility.IsOwner(p, Faction.OfPlayer) && !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled))
        {
            warnings.Add("CaravanIncapableOfSocial".Translate().Resolve());
        }
    }

    /// <summary>視窗的 MostFoodWillRotSoon：帶的食物營養有七成五以上不到五天就壞。</summary>
    private static bool MostCaravanFoodWillRotSoon(List<TransferableOneWay> transferables, PlanetTile tile)
    {
        float soon = 0f;
        float later = 0f;
        foreach (TransferableOneWay transferable in transferables)
        {
            if (!transferable.HasAnyThing || transferable.CountToTransfer <= 0 || !transferable.ThingDef.IsNutritionGivingIngestible || transferable.AnyThing is Corpse) continue;
            float days = 600f;
            CompRottable rottable = transferable.AnyThing.TryGetComp<CompRottable>();
            if (rottable != null) days = DaysUntilRotCalculator.ApproxTicksUntilRot_AssumeTimePassesBy(rottable, tile) / 60000f;
            float nutrition = transferable.ThingDef.GetStatValueAbstract(StatDefOf.Nutrition) * transferable.CountToTransfer;
            if (days < 5f) soon += nutrition;
            else later += nutrition;
        }
        return soon + later > 0f && soon / (soon + later) >= 0.75f;
    }

    /// <summary>找出口與集合點；找不到就加進拒絕原因。邏輯照 Dialog_FormCaravan.TryFormAndSendCaravan（它們是私有方法）。</summary>
    private static void FindCaravanSpots(CaravanDraft draft, Map map, List<string> blockers, List<string> warnings)
    {
        List<Pawn> pawns = draft.Pawns;
        IntVec3 root = pawns.Aggregate(IntVec3.Zero, (acc, p) => acc + p.Position) / pawns.Count;
        string direction = Find.WorldGrid.GetDirection8WayFromTo(map.Tile, draft.StartingTile).LabelShort();
        IntVec3 exit;
        if (!RCellFinder.TryFindClosestEdgeCellTo(root, map, out exit) && !TryFindCaravanExitSpot(map, pawns, draft.StartingTile, reachableForEveryColonist: true, out exit))
        {
            if (!TryFindCaravanExitSpot(map, pawns, draft.StartingTile, reachableForEveryColonist: false, out exit))
            {
                blockers.Add("CaravanCouldNotFindExitSpot".Translate(direction).Resolve());
                return;
            }
            warnings.Add("CaravanCouldNotFindReachableExitSpot".Translate(direction).Resolve());
        }
        IntVec3 meeting;
        if (!TryFindCaravanPackingSpot(map, exit, out meeting))
        {
            blockers.Add("CaravanCouldNotFindPackingSpot".Translate(direction).Resolve());
            return;
        }
        Pawn roamer = pawns.Find(ropee => AnimalPenUtility.NeedsToBeManagedByRope(ropee) && !pawns.Any(roper => roper.IsColonist
            && GatherAnimalsAndSlavesForCaravanUtility.CanRoperTakeAnimalToDest(roper, ropee, exit)
            && GatherAnimalsAndSlavesForCaravanUtility.CanRoperTakeAnimalToDest(roper, ropee, meeting)));
        if (roamer != null)
        {
            blockers.Add("CaravanRoamerCannotReachSpots".Translate(roamer.LabelShort, roamer).Resolve());
            return;
        }
        draft.ExitSpot = exit;
        draft.MeetingPoint = meeting;
    }

    private static bool TryFindCaravanExitSpot(Map map, List<Pawn> pawns, PlanetTile startingTile, bool reachableForEveryColonist, out IntVec3 spot)
    {
        CaravanExitMapUtility.GetExitMapEdges(Find.WorldGrid, map.Tile, startingTile, out Rot4 primary, out Rot4 secondary);
        spot = IntVec3.Invalid;
        if (primary != Rot4.Invalid && TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, primary, out spot)) return true;
        if (secondary != Rot4.Invalid && TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, secondary, out spot)) return true;
        if (TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, primary.Rotated(RotationDirection.Clockwise), out spot)) return true;
        return TryFindCaravanExitSpotOnEdge(map, pawns, reachableForEveryColonist, primary.Rotated(RotationDirection.Counterclockwise), out spot);
    }

    private static bool TryFindCaravanExitSpotOnEdge(Map map, List<Pawn> pawns, bool reachableForEveryColonist, Rot4 exitDirection, out IntVec3 spot)
    {
        if (reachableForEveryColonist)
        {
            return CellFinder.TryFindRandomEdgeCellWith(
                c => !c.Fogged(map) && c.Standable(map) && pawns.TrueForAll(p => !p.IsColonist || p.Downed || p.CanReach(c, PathEndMode.Touch, Danger.Deadly)),
                map, exitDirection, CellFinder.EdgeRoadChance_Always, out spot);
        }
        IntVec3 best = IntVec3.Invalid;
        int bestReachable = -1;
        foreach (IntVec3 cell in CellRect.WholeMap(map).GetEdgeCells(exitDirection).InRandomOrder())
        {
            if (cell.Fogged(map) || !cell.Standable(map)) continue;
            int reachable = pawns.Count(p => p.IsColonist && !p.Downed && p.CanReach(cell, PathEndMode.Touch, Danger.Deadly));
            if (reachable > bestReachable)
            {
                bestReachable = reachable;
                best = cell;
            }
        }
        spot = best;
        return best.IsValid;
    }

    private static bool TryFindCaravanPackingSpot(Map map, IntVec3 exitSpot, out IntVec3 packingSpot)
    {
        TraverseParms traverseParams = TraverseParms.For(TraverseMode.PassDoors);
        List<Thing> spots = [.. map.listerThings.ThingsOfDef(ThingDefOf.CaravanPackingSpot).Where(t => map.reachability.CanReach(exitSpot, t, PathEndMode.OnCell, traverseParams))];
        if (spots.Count > 0)
        {
            packingSpot = spots.RandomElement().Position;
            return true;
        }
        return RCellFinder.TryFindRandomSpotJustOutsideColony(exitSpot, map, out packingSpot);
    }

    public IReadOnlyList<FormingCaravanInfo> GetFormingCaravans()
    {
        var result = new List<FormingCaravanInfo>();
        foreach (Map map in Find.Maps)
        {
            foreach (Lord lord in map.lordManager.lords)
            {
                if (lord.LordJob is not LordJob_FormAndSendCaravan job || lord.faction != Faction.OfPlayer) continue;
                result.Add(new FormingCaravanInfo
                {
                    Id = lord.loadID,
                    Status = PlainText(job.Status),
                    Members = [.. lord.ownedPawns.Concat(job.downedPawns).Distinct().Select(ToCaravanMember)],
                    Cargo = [.. job.transferables.Where(t => t.HasAnyThing && t.CountToTransfer > 0).Select(t => t.Label + " x" + t.CountToTransfer.ToString(CultureInfo.InvariantCulture))],
                    ExitX = job.ExitSpot.x,
                    ExitZ = job.ExitSpot.z
                });
            }
        }
        return result;
    }

    public CommandResult CancelFormingCaravan(int formingId)
    {
        foreach (Map map in Find.Maps)
        {
            Lord lord = map.lordManager.lords.FirstOrDefault(l => l.loadID == formingId && l.LordJob is LordJob_FormAndSendCaravan);
            if (lord == null) continue;
            string names = string.Join(", ", lord.ownedPawns.Select(p => p.LabelShort).ToArray());
            CaravanFormingUtility.StopFormingCaravan(lord);
            return CommandResult.Ok($"Stopped forming the caravan ({names}); they put down what they were carrying and go back to their usual work");
        }
        return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no caravan is being formed with id {formingId}"));
    }


    private static Caravan FindPlayerCaravan(int id)
    {
        return Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == id && c.IsPlayerControlled);
    }

    public IReadOnlyList<CaravanInfo> GetCaravans()
    {
        return [.. Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled).Select(ToCaravanInfo)];
    }

    private static CaravanInfo ToCaravanInfo(Caravan caravan)
    {
        // 就地建立的可否與原因直接讀原版商隊按鈕（Caravan.GetGizmos 的同一組判斷）。
        Command settle = SettleInEmptyTileUtility.SettleCommand(caravan);
        Command camp = SettleInEmptyTileUtility.SetupCamp(caravan);
        // 沒在走時 Destination 是上次的殘值（struct 預設是有效的 0 號格），只在 Moving 時讀。
        bool moving = caravan.pather.Moving;
        PlanetTile destination = moving ? caravan.pather.Destination : PlanetTile.Invalid;
        return new CaravanInfo
        {
            Id = caravan.ID,
            Name = caravan.LabelCap,
            Tile = caravan.Tile,
            Terrain = caravan.Tile.Tile.PrimaryBiome.LabelCap + ", " + caravan.Tile.Tile.hilliness.GetLabelCap(),
            Members = [.. caravan.PawnsListForReading.Select(ToCaravanMember)],
            Moving = moving,
            Paused = caravan.pather.Paused,
            Resting = caravan.NightResting,
            Immobilized = caravan.ImmobilizedByMass,
            AllOwnersDowned = caravan.AllOwnersDowned,
            AllOwnersInMentalBreak = caravan.AllOwnersHaveMentalBreak,
            DestinationTile = destination.Valid ? destination.tileId : -1,
            DestinationLabel = destination.Valid ? DescribeWorldTile(destination) : string.Empty,
            ArrivalAction = moving ? SafeArrivalActionLabel(caravan.pather.ArrivalAction) : string.Empty,
            EtaTicks = moving ? CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan, allowCaching: false) : 0,
            FoodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(caravan),
            MassUsage = caravan.MassUsage,
            MassCapacity = caravan.MassCapacity,
            TicksPerMove = caravan.TicksPerMove,
            CanSettle = !settle.Disabled,
            SettleBlockedReason = settle.Disabled ? PlainText(settle.disabledReason) : string.Empty,
            CanCamp = !camp.Disabled,
            CampBlockedReason = camp.Disabled ? PlainText(camp.disabledReason) : string.Empty,
            ObjectsHere = [.. Find.WorldObjects.ObjectsAt(caravan.Tile).Where(o => o != caravan).Select(o => o.LabelCap + " [" + o.ID.ToString(CultureInfo.InvariantCulture) + "]")]
        };
    }

    /// <summary>抵達動作的標籤；模組的動作可能在欄位為空時丟例外，當作沒有。</summary>
    private static string SafeArrivalActionLabel(CaravanArrivalAction action)
    {
        if (action == null) return string.Empty;
        try
        {
            return PlainText(action.Label);
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent: could not read a caravan arrival action label: " + ex.Message);
            return string.Empty;
        }
    }

    private static CaravanMemberInfo ToCaravanMember(Pawn pawn)
    {
        string kind = CaravanMemberInfo.OtherKind;
        if (pawn.IsPrisoner) kind = CaravanMemberInfo.PrisonerKind;
        else if (pawn.IsSlave) kind = CaravanMemberInfo.SlaveKind;
        else if (pawn.RaceProps.Animal) kind = CaravanMemberInfo.AnimalKind;
        else if (pawn.RaceProps.IsMechanoid) kind = CaravanMemberInfo.MechKind;
        else if (pawn.IsColonist) kind = CaravanMemberInfo.ColonistKind;
        return new CaravanMemberInfo
        {
            Id = pawn.thingIDNumber,
            Name = pawn.LabelShort,
            Kind = kind,
            Downed = pawn.Downed,
            InMentalState = pawn.InMentalState,
            HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f
        };
    }

    private sealed class CaravanOrder
    {
        public string Label = string.Empty;
        public bool Disabled;
        public Action Run;
    }

    /// <summary>
    /// 第 1 項是單純前往（與玩家在世界地圖對空地按右鍵相同：AutoOrderToTileNow 的 BestGotoDestNear 加 StartPath），
    /// 其餘是原版世界右鍵選單 FloatMenuMakerWorld.ChoicesAtFor 對這格上所有世界物件產生的選項，開發者模式的瞬間抵達選項不收。
    /// </summary>
    private static List<CaravanOrder> BuildCaravanOrders(Caravan caravan, PlanetTile to)
    {
        var orders = new List<CaravanOrder>();
        string name = "Travel to tile " + to.tileId.ToString(CultureInfo.InvariantCulture);
        if (to == caravan.Tile)
        {
            orders.Add(new CaravanOrder { Label = name + " (the caravan is already here)", Disabled = true });
        }
        else
        {
            PlanetTile destination = CaravanUtility.BestGotoDestNear(to, caravan);
            if (!destination.Valid)
            {
                orders.Add(new CaravanOrder { Label = name + " (no reachable tile nearby)", Disabled = true });
            }
            else
            {
                orders.Add(new CaravanOrder
                {
                    Label = destination == to ? name : name + " (stops at the nearest reachable tile " + destination.tileId.ToString(CultureInfo.InvariantCulture) + ")",
                    Run = () => caravan.pather.StartPath(destination, arrivalAction: null, repathImmediately: true)
                });
            }
        }
        try
        {
            AddWorldCaravanOrders(orders, to, caravan);
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent: building the caravan world menu failed: " + ex);
        }
        return orders;
    }

    private static void AddWorldCaravanOrders(List<CaravanOrder> orders, PlanetTile to, Caravan caravan)
    {
        foreach (FloatMenuOption option in FloatMenuMakerWorld.ChoicesAtFor(to, caravan))
        {
            if (option.Label.Contains("(Dev:", StringComparison.Ordinal)) continue;
            FloatMenuOption chosen = option;
            orders.Add(new CaravanOrder { Label = chosen.Label.StripTags(), Disabled = chosen.Disabled, Run = () => chosen.Chosen(colonistOrdering: false, floatMenu: null) });
        }
    }

    public CaravanOptionsInfo GetCaravanOptions(int caravanId, int tile)
    {
        Caravan caravan = FindPlayerCaravan(caravanId);
        if (caravan == null) return new CaravanOptionsInfo { Error = CaravanNotFound };
        PlanetTile to = ToPlanetTile(tile, caravan.Tile);
        if (!TileInWorld(to)) return new CaravanOptionsInfo { Error = string.Create(CultureInfo.InvariantCulture, $"tile {tile} does not exist on this layer of the world"), CaravanName = caravan.LabelCap };
        return new CaravanOptionsInfo
        {
            CaravanName = caravan.LabelCap,
            Options = [.. BuildCaravanOrders(caravan, to).Select((o, i) => new FloatMenuOptionInfo { Number = i + 1, Label = o.Label, Disabled = o.Disabled })]
        };
    }

    public CommandResult SendCaravan(int caravanId, int tile, int optionNumber, string label)
    {
        Caravan caravan = FindPlayerCaravan(caravanId);
        if (caravan == null) return CommandResult.Fail(CaravanNotFound);
        PlanetTile to = ToPlanetTile(tile, caravan.Tile);
        if (!TileInWorld(to)) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"tile {tile} does not exist on this layer of the world"));
        List<CaravanOrder> orders = BuildCaravanOrders(caravan, to);
        CaravanOrder order = optionNumber >= 1 && optionNumber <= orders.Count ? orders[optionNumber - 1] : null;
        if (order == null || !string.Equals(order.Label, label, StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"{caravan.LabelCap}'s orders for tile {tile} changed; call get_world_tile again"));
        }
        if (order.Disabled) return CommandResult.Fail($"\"{label}\" is disabled");

        bool startedOnTile = to == caravan.Tile;
        StopAllTargeting();
        var before = new HashSet<Window>(Find.WindowStack.Windows);
        var watch = new MessageWatch();
        // 先記下出發前的移動狀態：本來就在走的篷車不能因為「正在移動」就被當成這道命令生效。
        bool wasMoving = caravan.pather.Moving;
        PlanetTile destinationBefore = caravan.pather.Destination;
        CommandResult runFail = RunCaravanOrder(order, label);
        if (runFail != null) return runFail;

        // 命令若接著要選目標或開別的視窗，這裡沒辦法回應，收掉並回報；確認框與對話留著給 ChooseOption。
        bool targeting = Find.Targeter.IsTargeting || Find.WorldTargeter.IsTargeting;
        StopAllTargeting();
        CommandResult windowFailure = CloseUnsupportedWindows(before, label);
        // 命令已經執行：若篷車已出發／離開世界地圖，後續的目標或視窗只是多餘的附帶步驟，不能回報成單純失敗。
        bool orderTookEffect = caravan.Destroyed || !caravan.Spawned
            || (caravan.pather.Moving && (!wasMoving || caravan.pather.Destination != destinationBefore));
        CommandResult disruption = HandleFollowUpDisruptions(targeting, windowFailure, orderTookEffect, label, watch);
        if (disruption != null)
        {
            return disruption;
        }
        return FinalizeCaravanSend(caravan, to, startedOnTile, before, watch, label);
    }

    private static CommandResult HandleFollowUpDisruptions(bool targeting, CommandResult windowFailure, bool orderTookEffect, string label, MessageWatch watch)
    {
        if (targeting)
        {
            string noTarget = $"\"{label}\" asks for a further target, which is not supported yet; it was cancelled";
            return orderTookEffect
                ? CommandResult.Ok(WithGameMessages($"\"{label}\" partly done: the order already took effect, but the extra step was cancelled ({noTarget})", watch))
                : CommandResult.Fail(noTarget);
        }
        if (windowFailure != null)
        {
            return orderTookEffect
                ? CommandResult.Ok(WithGameMessages($"\"{label}\" partly done: the order already took effect, but a follow-up window was closed ({windowFailure.Message})", watch))
                : windowFailure;
        }
        return null;
    }

    private static CommandResult FinalizeCaravanSend(Caravan caravan, PlanetTile to, bool startedOnTile, HashSet<Window> before, MessageWatch watch, string label)
    {
        if (caravan.Destroyed || !caravan.Spawned)
            return CommandResult.Ok(WithGameMessages($"\"{label}\" done: the caravan has left the world map (it entered a map or was dissolved)", watch));
        if (caravan.pather.Moving)
            return CommandResult.Ok(WithGameMessages($"{caravan.LabelCap} is on its way to {DescribeWorldTile(caravan.pather.Destination)}", watch));
        if (Find.WindowStack.Windows.Any(w => !before.Contains(w)))
            return CommandResult.Ok(WithGameMessages($"\"{label}\" is waiting for an answer", watch));
        if (startedOnTile)
            return CommandResult.Ok(WithGameMessages($"\"{label}\" done at {DescribeWorldTile(to)}", watch));
        return CommandResult.Fail(WithGameMessages($"{caravan.LabelCap} did not start moving", watch));
    }

    private static CommandResult RunCaravanOrder(CaravanOrder order, string label)
    {
        try
        {
            order.Run();
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent: caravan order \"" + label + "\" threw: " + ex);
            return CommandResult.Fail($"\"{label}\" failed in the game: {ex.Message}");
        }
    }

    public CommandResult SetCaravanMovement(int caravanId, string action)
    {
        Caravan caravan = FindPlayerCaravan(caravanId);
        if (caravan == null) return CommandResult.Fail(CaravanNotFound);
        Caravan_PathFollower pather = caravan.pather;
        switch (action)
        {
            case CaravanMovementAction.Pause:
                if (!pather.Moving) return CommandResult.Fail($"{caravan.LabelCap} has no destination, so there is nothing to pause");
                if (pather.Paused) return CommandResult.Ok($"{caravan.LabelCap} is already paused.");
                pather.Paused = true;
                return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{caravan.LabelCap} paused on tile {caravan.Tile.tileId}; its destination is kept."));
            case CaravanMovementAction.Resume:
                if (!pather.Moving) return CommandResult.Fail($"{caravan.LabelCap} has no destination; send it somewhere first");
                if (!pather.Paused) return CommandResult.Ok($"{caravan.LabelCap} is already travelling.");
                pather.Paused = false;
                return CommandResult.Ok($"{caravan.LabelCap} resumed its journey to {DescribeWorldTile(pather.Destination)}.");
            case CaravanMovementAction.Stop:
                if (!pather.Moving) return CommandResult.Ok($"{caravan.LabelCap} is already stopped.");
                pather.StopDead();
                return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{caravan.LabelCap} stopped on tile {caravan.Tile.tileId}; its destination was cleared."));
            default:
                return CommandResult.Fail($"unknown action \"{action}\"");
        }
    }

    public CommandResult SettleCaravan(int caravanId, string action)
    {
        Caravan caravan = FindPlayerCaravan(caravanId);
        if (caravan == null) return CommandResult.Fail(CaravanNotFound);
        bool camp = action == CaravanSettleAction.Camp;
        if (!camp && action != CaravanSettleAction.Settle) return CommandResult.Fail($"unknown action \"{action}\"");
        // 與玩家按商隊按鈕同一條路：可否判斷、確認框與背景生成地圖都在原版的按鈕動作裡。
        Command command = camp ? SettleInEmptyTileUtility.SetupCamp(caravan) : SettleInEmptyTileUtility.SettleCommand(caravan);
        string verb = camp ? "set up camp" : "found a colony";
        if (command.Disabled) return CommandResult.Fail($"{caravan.LabelCap} cannot {verb} here: {PlainText(command.disabledReason)}");

        StopAllTargeting();
        var before = new HashSet<Window>(Find.WindowStack.Windows);
        var watch = new MessageWatch();
        try
        {
            ((Command_Action)command).action();
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent: caravan " + verb + " threw: " + ex);
            return CommandResult.Fail($"could not {verb} in the game: {ex.Message}");
        }
        CommandResult windowFailure = CloseUnsupportedWindows(before, verb);
        if (windowFailure != null) return windowFailure;
        if (Find.WindowStack.Windows.Any(w => !before.Contains(w)))
            return CommandResult.Ok(WithGameMessages(string.Create(CultureInfo.InvariantCulture, $"The game asks to confirm before {caravan.LabelCap} can {verb} on tile {caravan.Tile.tileId}"), watch));
        return CommandResult.Ok(WithGameMessages(
            string.Create(CultureInfo.InvariantCulture, $"{caravan.LabelCap} is starting to {verb} on tile {caravan.Tile.tileId}. The map is generated in the background; it shows up in get_maps when it is ready"), watch));
    }
}
