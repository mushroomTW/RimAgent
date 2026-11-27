using System;
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
    public IReadOnlyList<ColonistHealthInfo> GetHealth()
    {
        return [.. Map.mapPawns.FreeColonistsSpawned.Select(ToHealth)];
    }

    private static ColonistHealthInfo ToHealth(Pawn pawn)
    {
        var conditions = new List<HealthConditionInfo>();
        foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
        {
            // 模組 hediff 的標籤、流血率等可能丟例外：退回 defName，不讓整份健康報告失敗，也不藏掉這個狀況。
            HealthConditionInfo condition;
            try
            {
                condition = ToCondition(hediff);
            }
            catch (Exception ex)
            {
                WarnModError(pawn, "health condition " + hediff.def?.defName, ex);
                condition = FallbackCondition(hediff);
            }
            conditions.Add(condition);
        }
        List<SurgeryBillInfo> surgeries = [.. pawn.BillStack.Bills.OfType<Bill_Medical>().Select(b => new SurgeryBillInfo
        {
            Label = b.LabelCap,
            Part = b.Part != null ? b.Part.LabelCap : string.Empty,
            Suspended = b.suspended
        })];
        return new ColonistHealthInfo { PawnId = pawn.thingIDNumber, Conditions = conditions, PendingSurgeries = surgeries };
    }

    private static HealthConditionInfo ToCondition(Hediff hediff)
    {
        bool tended = false, needsTending = false;
        if (hediff.def.tendable)
        {
            HediffComp_TendDuration? tend = (hediff as HediffWithComps)?.TryGetComp<HediffComp_TendDuration>();
            tended = tend != null && tend.IsTended;
            needsTending = !tended && hediff.TendableNow(ignoreTimer: false);
        }
        return new HealthConditionInfo
        {
            Label = hediff.LabelCap,
            Part = hediff.Part != null ? hediff.Part.LabelCap : string.Empty,
            Severity = hediff.Severity,
            Kind = ResolveConditionKind(hediff),
            Bleeding = hediff.BleedRate > 0f,
            Tended = tended,
            NeedsTending = needsTending
        };
    }

    /// <summary>
    /// 讀不到完整資料的狀況。標籤壞了不代表沒在流血：流血與待處置各自再讀一次，
    /// 再讀不到就往「需要處理」報，寧可多治一次也不讓殖民者流血至死。
    /// </summary>
    private static HealthConditionInfo FallbackCondition(Hediff hediff)
    {
        bool bleeding, needsTending;
        try
        {
            bleeding = hediff.BleedRate > 0f;
        }
        catch (Exception)
        {
            bleeding = hediff.def?.injuryProps?.bleedRate > 0f;
        }
        try
        {
            needsTending = hediff.TendableNow(ignoreTimer: false);
        }
        catch (Exception)
        {
            needsTending = hediff.def?.tendable == true;
        }
        return new HealthConditionInfo
        {
            Label = hediff.def?.defName ?? "unknown condition",
            Kind = ResolveConditionKind(hediff),
            Bleeding = bleeding,
            NeedsTending = needsTending
        };
    }

    private static string ResolveConditionKind(Hediff hediff)
    {
        if (hediff is Hediff_MissingPart) return HealthConditionInfo.MissingKind;
        if (hediff is Hediff_Injury) return HealthConditionInfo.InjuryKind;
        return HealthConditionInfo.ConditionKind;
    }

    public IReadOnlyList<DefInfo> MedicalCareLevels =>
        _medicalCareLevels ?? (_medicalCareLevels = [.. Enum.GetValues<MedicalCareCategory>().Select(c => new DefInfo(c.ToString(), MedicalCareUtility.GetLabel(c)))]);

    public CommandResult SetMedicalCare(int pawnId, string careDefName)
    {
        Pawn? pawn = FindColonist(pawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        if (pawn.playerSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no medical care settings");
        object parsed;
        try
        {
            parsed = Enum.Parse<MedicalCareCategory>(careDefName, ignoreCase: true);
        }
        catch (ArgumentException)
        {
            return CommandResult.Fail($"unknown medical care level {careDefName}");
        }
        if (!Enum.IsDefined(typeof(MedicalCareCategory), parsed)) return CommandResult.Fail($"unknown medical care level {careDefName}");
        pawn.playerSettings.medCare = (MedicalCareCategory)parsed;
        return CommandResult.Ok();
    }

    public IReadOnlyList<DefInfo> SurgeryRecipes =>
        _surgeryRecipes ?? (_surgeryRecipes = [.. DefDatabase<RecipeDef>.AllDefsListForReading
            .Where(r => r.workerClass != null && typeof(Recipe_Surgery).IsAssignableFrom(r.workerClass))
            .Select(r => new DefInfo(r.defName, r.label))]);

    public CommandResult ScheduleSurgery(SurgeryRequest request)
    {
        Pawn? pawn = FindColonist(request.PawnId);
        if (pawn == null) return CommandResult.Fail(ColonistNotFound);
        RecipeDef? recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(request.RecipeDefName);
        if (recipe == null || recipe.workerClass == null || !typeof(Recipe_Surgery).IsAssignableFrom(recipe.workerClass))
            return CommandResult.Fail($"unknown surgery {request.RecipeDefName}");
        CommandResult? researchFail = CheckSurgeryResearch(recipe);
        if (researchFail != null) return researchFail;
        CommandResult? partFail = ResolveSurgeryTargetPart(recipe, pawn, request.BodyPartLabel, out BodyPartRecord? part);
        if (partFail != null) return partFail;
        if (!recipe.AvailableOnNow(pawn, part))
            return CommandResult.Fail($"{recipe.label} is not available on {pawn.LabelShort} right now");
        if (pawn.BillStack.Bills.OfType<Bill_Medical>().Any(b => b.recipe == recipe && b.Part == part))
            return CommandResult.Fail($"{recipe.label} is already scheduled on {pawn.LabelShort}");
        // 與原版 HealthCardUtility.CreateSurgeryBill 一致（含知識進度與 Part 指派）；不送訊息，避免彈出「無人具備技能」對話框卡住流程，警告改寫進結果。
        HealthCardUtility.CreateSurgeryBill(pawn, recipe, part, uniqueIngredients: null, sendMessages: false);
        string detail = $"Scheduled {recipe.label} on {pawn.LabelShort}";
        if (part != null) detail += $" ({part.LabelCap})";
        detail += ".";
        if (pawn.Faction != null && !pawn.Faction.Hidden && !pawn.Faction.HostileTo(Faction.OfPlayer) && recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer))
            detail += $" Warning: this operation will anger {pawn.HomeFaction?.Name ?? "their faction"}.";
        if (!Map.mapPawns.PawnsInFaction(Faction.OfPlayer).Any(p => (p.IsFreeColonist || p.IsColonyMechPlayerControlled) && recipe.PawnSatisfiesSkillRequirements(p)))
            detail += " Warning: no colonist has the skill required for this surgery.";
        return CommandResult.Ok(detail);
    }

    private static CommandResult? CheckSurgeryResearch(RecipeDef recipe)
    {
        if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished)
            return CommandResult.Fail($"{recipe.label} needs research {recipe.researchPrerequisite.label} first");
        if (recipe.researchPrerequisites != null)
        {
            foreach (ResearchProjectDef prerequisite in recipe.researchPrerequisites)
            {
                if (prerequisite != null && !prerequisite.IsFinished)
                    return CommandResult.Fail($"{recipe.label} needs research {prerequisite.label} first");
            }
        }
        return null;
    }

    private static CommandResult? ResolveSurgeryTargetPart(RecipeDef recipe, Pawn pawn, string? wantLabel, out BodyPartRecord? part)
    {
        part = null;
        if (!recipe.targetsBodyPart) return null;
        List<BodyPartRecord> options = [.. recipe.Worker.GetPartsToApplyOn(pawn, recipe)];
        if (options.Count == 0) return CommandResult.Fail($"{recipe.label} does not apply to {pawn.LabelShort}");
        string want = (wantLabel ?? string.Empty).Trim();
        // 截肢、摘除器官這類手術不能替玩家挑部位：沒指定就列出候選要模型問清楚。
        if (want.Length == 0)
        {
            return CommandResult.Fail($"{recipe.label} needs a body part. Ask the player which one: " +
                                      string.Join(", ", options.Select(p => p.LabelCap).Distinct(StringComparer.Ordinal).ToArray()));
        }
        part = options.FirstOrDefault(p => MatchesBodyPart(p, want));
        if (part == null) return CommandResult.Fail($"{pawn.LabelShort} has no body part matching \"{want}\" for {recipe.label}");
        return null;
    }

    private static bool MatchesBodyPart(BodyPartRecord part, string want)
    {
        return string.Equals(part.LabelCap, want, StringComparison.OrdinalIgnoreCase)
            || string.Equals(part.Label, want, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(part.customLabel) && string.Equals(part.customLabel, want, StringComparison.OrdinalIgnoreCase))
            || (part.def != null && string.Equals(part.def.label, want, StringComparison.OrdinalIgnoreCase));
    }
}
