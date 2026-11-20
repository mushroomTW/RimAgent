#nullable disable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public IReadOnlyList<PrisonerInfo> GetPrisoners()
    {
        return Map.mapPawns.PrisonersOfColonySpawned.Select(ToPrisoner).ToList();
    }

    private static PrisonerInfo ToPrisoner(Pawn pawn)
    {
        var triple = pawn.Name as NameTriple;
        string nick = triple?.Nick ?? string.Empty;
        string first = triple?.First ?? pawn.Name?.ToStringShort ?? pawn.LabelShort;
        PrisonerInteractionModeDef mode = pawn.guest?.ExclusiveInteractionMode;
        return new PrisonerInfo
        {
            Id = pawn.thingIDNumber,
            Name = pawn.LabelShort,
            Nickname = !string.IsNullOrEmpty(nick) && nick != first ? nick : string.Empty,
            FullName = pawn.Name?.ToStringFull ?? pawn.LabelShort,
            Resistance = pawn.guest?.Resistance ?? -1f,
            Recruitable = pawn.guest?.Recruitable ?? false,
            InteractionMode = mode?.defName ?? string.Empty,
            InteractionModeLabel = mode?.label ?? string.Empty,
            HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
            Downed = pawn.Downed
        };
    }

    public IReadOnlyList<DefInfo> PrisonerInteractionModes =>
        _prisonerInteractionModes ?? (_prisonerInteractionModes = DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading
            .Select(d => new DefInfo(d.defName, d.label))
            .ToList());

    public CommandResult SetPrisonerInteraction(int pawnId, string modeDefName)
    {
        Pawn pawn = FindPrisoner(pawnId);
        if (pawn == null) return CommandResult.Fail("prisoner not found");
        PrisonerInteractionModeDef mode = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(modeDefName);
        if (mode == null) return CommandResult.Fail($"unknown prisoner interaction mode {modeDefName}");
        string hidden = WhyPrisonerModeHidden(pawn, mode);
        if (hidden != null) return CommandResult.Fail(hidden);
        pawn.guest.SetExclusiveInteraction(mode);
        return CommandResult.Ok();
    }

    /// <summary>
    /// 照 ITab_Pawn_Visitor 內 CanUsePrisonerInteractionMode（區域函式，無法直接呼叫）判斷該模式在介面上是否被隱藏。
    /// 傳回 null 代表可用，否則為拒絕原因。
    /// </summary>
    private static string WhyPrisonerModeHidden(Pawn pawn, PrisonerInteractionModeDef mode)
    {
        string cannot = $"{mode.label} is not available for {pawn.LabelShort}";
        if (!pawn.guest.Recruitable && mode.hideIfNotRecruitable) return $"{cannot}: not recruitable";
        if (pawn.IsWildMan() && !mode.allowOnWildMan) return $"{cannot}: wild men cannot use it";
        if (CheckBiotechPrisonerMode(pawn, mode, cannot) is { } biotechReason) return biotechReason;
        if (!mode.allowInClassicIdeoMode && Find.IdeoManager.classicMode) return $"{cannot}: not allowed in classic ideology mode";
        if (ModsConfig.AnomalyActive && CheckAnomalyPrisonerMode(pawn, mode, cannot) is { } anomalyReason) return anomalyReason;
        return null;
    }

    private static string CheckBiotechPrisonerMode(Pawn pawn, PrisonerInteractionModeDef mode, string cannot)
    {
        if (mode.hideIfNoBloodfeeders && pawn.MapHeld != null
            && !(ModsConfig.BiotechActive && pawn.MapHeld.mapPawns.FreeColonistsAndPrisonersSpawned.Any(p => p.IsBloodfeeder())))
        {
            return $"{cannot}: the colony has no bloodfeeder";
        }
        if (mode.hideOnHemogenicPawns && ModsConfig.BiotechActive && pawn.genes != null && pawn.genes.HasActiveGene(GeneDefOf.Hemogenic))
        {
            return $"{cannot}: hemogenic pawns cannot use it";
        }
        return null;
    }

    private static string CheckAnomalyPrisonerMode(Pawn pawn, PrisonerInteractionModeDef mode, string cannot)
    {
        if (mode.hideIfNotStudiableAsPrisoner
            && !(pawn.TryGetComp<CompStudiable>(out CompStudiable studiable) && studiable.EverStudiable()
                 && pawn.kindDef.studiableAsPrisoner && !pawn.everLostEgo))
        {
            return $"{cannot}: not studiable as a prisoner";
        }
        if (mode.hideIfGrayFleshNotAppeared && !Find.Anomaly.hasSeenGrayFlesh) return $"{cannot}: gray flesh has not appeared yet";
        return null;
    }

    public CommandResult ReleasePrisoner(int pawnId)
    {
        Pawn pawn = FindPrisoner(pawnId);
        if (pawn == null) return CommandResult.Fail("prisoner not found");
        // 釋放後 HomeFaction／狀態會變，先記下原版 ShouldStayOnMapOnRelease 的條件。
        bool staysOnMap = pawn.IsWildMan() || (pawn.HomeFaction != null && pawn.HomeFaction.IsPlayer);
        bool downed = pawn.Downed;
        GenGuest.PrisonerRelease(pawn);
        // 與 JobDriver_ReleasePrisoner 收尾一致：沒被留下等死才加「健康囚犯被釋放」想法，並送任務訊號。
        if (pawn.Spawned && !PawnBanishUtility.WouldBeLeftToDie(pawn, pawn.Map.Tile))
            GenGuest.AddHealthyPrisonerReleasedThoughts(pawn);
        QuestUtility.SendQuestTargetSignals(pawn.questTags, "Released", pawn.Named("SUBJECT"));
        if (staysOnMap)
            return CommandResult.Ok($"{pawn.LabelShort} is no longer a prisoner but stays on the map (they are not leaving the colony).");
        if (downed)
            return CommandResult.Ok($"{pawn.LabelShort} was released but is downed and cannot walk out; they stay on the map until they recover.");
        return CommandResult.Ok($"{pawn.LabelShort} was released and is walking toward the map edge to leave.");
    }

    private static Pawn FindPrisoner(int id)
    {
        return Map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == id);
    }
}
