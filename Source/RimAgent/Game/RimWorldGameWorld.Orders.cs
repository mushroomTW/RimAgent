using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public CommandResult SetDrafted(int pawnId, bool drafted)
    {
        Pawn? pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (pawn.drafter == null) return CommandResult.Fail($"{pawn.LabelShort} cannot be drafted");
        if (pawn.Downed && drafted) return CommandResult.Fail($"{pawn.LabelShort} is downed");
        pawn.drafter.Drafted = drafted;
        return CommandResult.Ok();
    }

    public CommandResult OrderMoveTo(int pawnId, int x, int z)
    {
        Map map = Map;
        Pawn? pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (!pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is not drafted");
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
        if (!cell.Standable(map)) return CommandResult.Fail("cell is not standable");
        if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach that cell");

        Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
        job.playerForced = true;
        return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
            ? CommandResult.Ok()
            : CommandResult.Fail($"{pawn.LabelShort} refused the move order");
    }

    public CommandResult OrderAttack(int pawnId, int targetId)
    {
        Pawn? pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (!pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is not drafted");
        Thing? target = FindThing(targetId);
        if (target == null) return CommandResult.Fail("target not found on the map");
        // 不打自己人、囚犯與友好派系：模型一次幻覺就能開火，那是外交事件。無派系的野生動物照舊可打（狩獵）。
        if (target.Faction == Faction.OfPlayer) return CommandResult.Fail($"{target.LabelShort} belongs to the colony");
        if (target is Pawn { IsPrisonerOfColony: true }) return CommandResult.Fail($"{target.LabelShort} is the colony's prisoner");
        if (target.Faction != null && !target.HostileTo(Faction.OfPlayer)) return CommandResult.Fail($"{target.LabelShort} belongs to a faction that is not hostile to the colony");

        bool ranged = pawn.equipment?.Primary?.def.IsRangedWeapon == true;
        Job job = JobMaker.MakeJob(ranged ? JobDefOf.AttackStatic : JobDefOf.AttackMelee, target);
        job.playerForced = true;
        return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
            ? CommandResult.Ok()
            : CommandResult.Fail($"{pawn.LabelShort} refused the attack order");
    }

    public CommandResult OrderJob(int pawnId, string job, int targetId)
    {
        Pawn? pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (pawn.Downed) return CommandResult.Fail($"{pawn.LabelShort} is downed");
        if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return CommandResult.Fail($"{pawn.LabelShort} cannot use their hands");
        Thing? target = FindThing(targetId);
        if (target == null) return CommandResult.Fail("target not found on the map");
        if (!pawn.CanReach(target, PathEndMode.ClosestTouch, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach {target.LabelShort}");

        return job switch
        {
            "rescue" => OrderRescue(pawn, target as Pawn),
            "capture" => OrderCapture(pawn, target as Pawn),
            "tend" => OrderTend(pawn, target as Pawn),
            "strip" => OrderStrip(pawn, target),
            "equip" => OrderEquip(pawn, target),
            "enter_casket" => OrderEnterCasket(pawn, target),
            _ => CommandResult.Fail($"unknown job \"{job}\""),
        };
    }

    // 以下檢查照遊戲的右鍵選單（FloatMenuOptionProvider_*），只拒絕會惹惱派系或需要玩家另外確認的情況。
    private static CommandResult OrderRescue(Pawn pawn, Pawn? patient)
    {
        if (patient == null) return CommandResult.Fail("only pawns can be rescued");
        // 任務地圖上等人救的難民走「提供幫助」，救了就加入。
        if (patient.mindState.WillJoinColonyIfRescued)
        {
            return TakeOrderedJob(pawn, JobMaker.MakeJob(JobDefOf.OfferHelp, patient), $"offering help to {patient.LabelShort}; they join once helped");
        }
        if (!patient.Downed) return CommandResult.Fail($"{patient.LabelShort} is not downed");
        if (patient.Faction != null && patient.Faction.HostileTo(Faction.OfPlayer)) return CommandResult.Fail($"{patient.LabelShort} is hostile; use capture to take them prisoner");
        if (patient.IsPrisonerOfColony || patient.IsSlaveOfColony) return CommandResult.Fail($"{patient.LabelShort} is the colony's prisoner or slave; wardens and doctors handle them");
        if (!HealthAIUtility.CanRescueNow(pawn, patient, forced: true)) return CommandResult.Fail($"{pawn.LabelShort} cannot rescue {patient.LabelShort} right now");
        if (patient.playerSettings?.medCare == MedicalCareCategory.NoCare) return CommandResult.Fail($"{patient.LabelShort}'s medical care is set to no care");

        Building_Bed? bed = RestUtility.FindBedFor(patient, pawn, checkSocialProperness: false)
                            ?? RestUtility.FindBedFor(patient, pawn, checkSocialProperness: false, ignoreOtherReservations: true);
        if (bed == null) return CommandResult.Fail(patient.RaceProps.Animal ? "no free animal bed" : "no free bed for them; build or free a bed first");
        Job rescue = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
        rescue.count = 1;
        return TakeOrderedJob(pawn, rescue, string.Create(CultureInfo.InvariantCulture, $"carrying {patient.LabelShort} to {bed.LabelShort} at ({bed.Position.x}, {bed.Position.z})"));
    }

    private static CommandResult OrderCapture(Pawn pawn, Pawn? prisoner)
    {
        if (prisoner == null) return CommandResult.Fail("only pawns can be captured");
        if (!prisoner.CanBeCaptured()) return CommandResult.Fail($"{prisoner.LabelShort} cannot be captured (they must be downed and not already the colony's)");
        // 抓非敵對派系的人會惹惱整個派系，那是外交決定，不讓模型一句話就做。
        if (prisoner.Faction != null && prisoner.Faction != Faction.OfPlayer && !prisoner.Faction.Hidden && !prisoner.Faction.HostileTo(Faction.OfPlayer))
        {
            return CommandResult.Fail($"capturing {prisoner.LabelShort} would anger {prisoner.Faction.Name}; refused");
        }
        if (!HealthAIUtility.CanRescueNow(pawn, prisoner, forced: true)) return CommandResult.Fail($"{pawn.LabelShort} cannot capture {prisoner.LabelShort} right now");

        Building_Bed? bed = RestUtility.FindBedFor(prisoner, pawn, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner)
                            ?? RestUtility.FindBedFor(prisoner, pawn, checkSocialProperness: false, ignoreOtherReservations: true, GuestStatus.Prisoner);
        if (bed == null) return CommandResult.Fail("no free prisoner bed; set a bed for prisoners first");
        Job capture = JobMaker.MakeJob(JobDefOf.Capture, prisoner, bed);
        capture.count = 1;
        string note = string.Create(CultureInfo.InvariantCulture, $"taking {prisoner.LabelShort} to {bed.LabelShort} at ({bed.Position.x}, {bed.Position.z})");
        if (!prisoner.guest.Recruitable) note += "; they are unrecruitable";
        return TakeOrderedJob(pawn, capture, note);
    }

    private static CommandResult OrderTend(Pawn doctor, Pawn? patient)
    {
        if (!ValidateTendOrder(doctor, patient, out string? failReason))
        {
            return CommandResult.Fail(failReason);
        }

        // 找藥與建工作同 WorkGiver_Tend：藥在別人身上（背包、架子）時第三個目標是它的持有者。
        Thing? medicine = HealthAIUtility.FindBestMedicine(doctor, patient);
        Job tend = CreateTendJob(patient, medicine);
        tend.count = 1;
        tend.draftedTend = doctor.Drafted;
        return TakeOrderedJob(doctor, tend, $"tending {patient.LabelShort} " + (medicine == null ? "without medicine" : "with " + medicine.def.label));
    }

    private static bool ValidateTendOrder(Pawn doctor, [NotNullWhen(true)] Pawn? patient, [NotNullWhen(false)] out string? failReason)
    {
        failReason = null;
        if (patient == null) { failReason = "only pawns can be tended"; return false; }
        if (doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)) { failReason = $"{doctor.LabelShort} is incapable of doctoring"; return false; }
        if (!patient.health.HasHediffsNeedingTend()) { failReason = $"{patient.LabelShort} does not need tending"; return false; }
        bool ours = patient.IsColonist || patient.IsQuestLodger() || patient.IsPrisonerOfColony || patient.IsSlaveOfColony
                    || (patient.Faction == Faction.OfPlayer && patient.IsAnimal);
        if (!patient.Downed && (patient.HostileTo(doctor.Faction) || !ours)) { failReason = $"{patient.LabelShort} will not accept treatment"; return false; }
        if (patient == doctor && doctor.playerSettings != null && !doctor.playerSettings.selfTend) { failReason = $"self-tend is disabled for {doctor.LabelShort}"; return false; }
        if (patient.InAggroMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria)) { failReason = $"{patient.LabelShort} is in an aggressive mental state"; return false; }
        return true;
    }

    private static Job CreateTendJob(Pawn patient, Thing? medicine)
    {
        if (medicine == null)
        {
            return JobMaker.MakeJob(JobDefOf.TendPatient, patient);
        }
        if (medicine.SpawnedParentOrMe != medicine)
        {
            return JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine, medicine.SpawnedParentOrMe);
        }
        return JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
    }

    private static CommandResult OrderStrip(Pawn pawn, Thing target)
    {
        if (!StrippableUtility.CanBeStrippedByColony(target)) return CommandResult.Fail($"{target.LabelShort} cannot be stripped by the colony");
        if (target is Pawn living && living.HasExtraHomeFaction()) return CommandResult.Fail($"{target.LabelShort} is quest-related");
        Pawn? owner = target as Pawn ?? (target as Corpse)?.InnerPawn;
        if (owner?.Faction != null && owner.Faction != Faction.OfPlayer && !owner.Faction.HostileTo(Faction.OfPlayer))
        {
            return CommandResult.Fail($"stripping {target.LabelShort} would hurt relations with {owner.Faction.Name}; refused");
        }
        return TakeOrderedJobUnforbiddingTarget(pawn, JobMaker.MakeJob(JobDefOf.Strip, target), target,
            $"stripping {target.LabelShort}");
    }

    private static CommandResult OrderEquip(Pawn pawn, Thing item)
    {
        if (pawn.equipment == null || !item.HasComp<CompEquippable>()) return CommandResult.Fail($"{item.LabelShort} is not a weapon or equipment");
        if (item.def.IsWeapon && pawn.WorkTagIsDisabled(WorkTags.Violent)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of violence");
        if (item.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of shooting");
        if (item.IsBurning()) return CommandResult.Fail($"{item.LabelShort} is burning");
        if (pawn.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanEquip(item, pawn)) return CommandResult.Fail($"{pawn.LabelShort} cannot equip it (quest-related)");
        if (!EquipmentUtility.CanEquip(item, pawn, out string reason, checkBonded: false)) return CommandResult.Fail(reason);
        // 靈魂綁定武器在遊戲裡要玩家按確認，不代答。
        if (EquipmentUtility.AlreadyBondedToWeapon(item, pawn) || !EquipmentUtility.GetPersonaWeaponConfirmationText(item, pawn).NullOrEmpty())
        {
            return CommandResult.Fail($"{item.LabelShort} is a persona weapon; bonding needs the player's confirmation in-game");
        }
        return TakeOrderedJobUnforbiddingTarget(pawn, JobMaker.MakeJob(JobDefOf.Equip, item), item,
            $"equipping {item.LabelShort}");
    }

    private static CommandResult OrderEnterCasket(Pawn pawn, Thing target)
    {
        if (target is not Building_CryptosleepCasket casket) return CommandResult.Fail($"{target.LabelShort} is not a cryptosleep casket");
        if (pawn.IsQuestLodger()) return CommandResult.Fail("guests cannot use cryptosleep caskets");
        if (casket.HasAnyContents) return CommandResult.Fail($"{casket.LabelShort} is already occupied");
        if (!pawn.CanReach(casket, PathEndMode.InteractionCell, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach {casket.LabelShort}");
        // 心靈羈絆的伴侶就在附近時，遊戲會跳確認（分開會有負面效果），不代答。
        if (ModsConfig.BiotechActive
            && pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.PsychicBond) is Hediff_PsychicBond bond
            && ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson(pawn, bond))
        {
            return CommandResult.Fail($"entering would separate {pawn.LabelShort} from their psychic bond partner; that needs the player's confirmation in-game");
        }
        return TakeOrderedJob(pawn, JobMaker.MakeJob(JobDefOf.EnterCryptosleepCasket, casket),
            string.Create(CultureInfo.InvariantCulture, $"entering {casket.LabelShort} at ({casket.Position.x}, {casket.Position.z})"));
    }

    private static CommandResult TakeOrderedJobUnforbiddingTarget(Pawn pawn, Job job, Thing target, string note)
    {
        bool wasForbidden = target.IsForbidden(Faction.OfPlayer);
        target.SetForbidden(value: false, warnOnFail: false);
        bool accepted = false;
        try
        {
            CommandResult result = TakeOrderedJob(pawn, job, note);
            accepted = result.Success;
            return result;
        }
        finally
        {
            if (!accepted && wasForbidden) target.SetForbidden(value: true, warnOnFail: false);
        }
    }

    private static CommandResult TakeOrderedJob(Pawn pawn, Job job, string note)
    {
        job.playerForced = true;
        return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
            ? CommandResult.Ok(note)
            : CommandResult.Fail($"{pawn.LabelShort} refused the order");
    }

    public CommandResult SetForbidden(CellRectSpec? area, string labelContains, bool forbidden)
    {
        Map map = Map;
        bool hasNeedle = !string.IsNullOrEmpty(labelContains);
        var changed = new List<Thing>();
        // HaulableEver 涵蓋物品與屍體；迷霧裡的東西玩家還看不到，不動。
        foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => CanSetForbidden(t, area, hasNeedle, labelContains, forbidden)).ToList())
        {
            t.SetForbidden(forbidden, warnOnFail: false);
            changed.Add(t);
        }
        if (changed.Count == 0)
        {
            string state = forbidden ? "allowed" : "forbidden";
            return CommandResult.Fail($"no visible item or corpse there is currently {state}" + (hasNeedle ? $" matching \"{labelContains}\"" : string.Empty));
        }
        IEnumerable<string> labels = changed.Take(10).Select(t => t.LabelShort);
        return CommandResult.Ok($"{changed.Count} item(s): {string.Join(", ", labels)}{(changed.Count > 10 ? ", …" : string.Empty)}");
    }

    private static bool CanSetForbidden(Thing t, CellRectSpec? area, bool hasNeedle, string labelContains, bool targetForbidden)
    {
        if (!t.Spawned || t.Fogged()) return false;
        if (area.HasValue && !area.Value.Contains(t.Position.x, t.Position.z)) return false;
        if (t.TryGetComp<CompForbiddable>() == null || t.IsForbidden(Faction.OfPlayer) == targetForbidden) return false;
        if (hasNeedle && !MatchesLabel(t, labelContains)) return false;
        return true;
    }
}
