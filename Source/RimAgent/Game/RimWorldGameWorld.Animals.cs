using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public IReadOnlyList<AnimalInfo> GetAnimals()
    {
        return Map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && !p.Fogged()).Select(ToAnimal).ToList();
    }

    private static AnimalInfo ToAnimal(Pawn pawn)
    {
        return new AnimalInfo
        {
            Id = pawn.thingIDNumber,
            Name = pawn.LabelShort,
            SpeciesDefName = pawn.def.defName,
            Species = pawn.def.label,
            Wild = pawn.Faction != Faction.OfPlayer,
            Area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label ?? string.Empty,
            HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
            Downed = pawn.Downed
        };
    }

    public IReadOnlyList<DefInfo> AnimalTraining =>
        _animalTraining ?? (_animalTraining = DefDatabase<TrainableDef>.AllDefsListForReading
            .Select(d => new DefInfo(d.defName, d.label))
            .ToList());

    /// <summary>區域是每張地圖的執行期資料（玩家隨時新增、切圖就換），不快取。</summary>
    [SuppressMessage("Major Code Smell", "S2365:Properties should not copy collections", Justification = "Implements IGameWorld property contract border reflects dynamic game state")]
    public IReadOnlyList<DefInfo> AnimalAreas =>
        Map.areaManager.AllAreas
            .Where(a => a.AssignableAsAllowed())
            .Select(a => new DefInfo(a.Label, a.Label))
            .ToList();

    public CommandResult TameAnimal(int pawnId)
    {
        Map map = Map;
        Pawn pawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == pawnId && p.RaceProps.Animal && !p.Fogged());
        if (pawn == null) return CommandResult.Fail("animal not found");
        if (pawn.Dead) return CommandResult.Fail($"{pawn.LabelShort} is dead");
        if (pawn.Faction == Faction.OfPlayer) return CommandResult.Fail($"{pawn.LabelShort} is already tame");
        // 照 Designator_Tame.CanDesignateThing。
        if (!TameUtility.CanTame(pawn)) return CommandResult.Fail($"{pawn.LabelShort} cannot be tamed");
        if (pawn.health.hediffSet.HasHediff(HediffDefOf.Scaria)) return CommandResult.Fail($"{pawn.LabelShort} has scaria and cannot be tamed");
        if (map.designationManager.DesignationOn(pawn, DesignationDefOf.Tame) != null)
            return CommandResult.Fail($"{pawn.LabelShort} is already marked for taming");
        // 照 Designator_Tame.DesignateThing：先清掉該動物身上其他指派（例如狩獵）。
        map.designationManager.RemoveAllDesignationsOn(pawn);
        map.designationManager.AddDesignation(new Designation(pawn, DesignationDefOf.Tame));
        return CommandResult.Ok($"Marked {pawn.LabelShort} for taming. A handler will attempt it when available.");
    }

    public CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable)
    {
        Pawn pawn = FindColonyAnimal(pawnId);
        if (pawn == null) return CommandResult.Fail("colony animal not found");
        TrainableDef training = DefDatabase<TrainableDef>.GetNamedSilentFail(trainableDefName);
        if (training == null) return CommandResult.Fail($"unknown animal training {trainableDefName}");
        if (pawn.training == null) return CommandResult.Fail($"{pawn.LabelShort} cannot be trained");
        if (enable)
        {
            AcceptanceReport canTrain = pawn.training.CanAssignToTrain(training);
            if (!canTrain.Accepted)
            {
                return CommandResult.Fail(string.IsNullOrEmpty(canTrain.Reason)
                    ? $"{pawn.LabelShort} cannot learn {training.label}"
                    : $"{pawn.LabelShort} cannot learn {training.label}: {canTrain.Reason}");
            }
        }
        pawn.training.SetWantedRecursive(training, enable);
        return CommandResult.Ok(enable
            ? $"{pawn.LabelShort} will now be trained in {training.label}."
            : $"{pawn.LabelShort} will no longer be trained in {training.label}.");
    }

    public CommandResult SetAnimalArea(int pawnId, string areaLabel)
    {
        Pawn pawn = FindColonyAnimal(pawnId);
        if (pawn == null) return CommandResult.Fail("colony animal not found");
        if (pawn.playerSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no area settings");
        string label = (areaLabel ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
            return CommandResult.Ok($"{pawn.LabelShort}'s allowed area is now unrestricted.");
        }
        Area area = Map.areaManager.AllAreas.FirstOrDefault(a => a.AssignableAsAllowed() && string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase));
        if (area == null) return CommandResult.Fail($"unknown animal area {label}");
        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
        return CommandResult.Ok($"{pawn.LabelShort}'s allowed area is now {area.Label}.");
    }

    private static Pawn FindColonyAnimal(int id)
    {
        return Map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id && p.RaceProps.Animal && p.Faction == Faction.OfPlayer);
    }

    /// <summary>
    /// 無派系的動物。用 RaceProps.Animal 而非「不是類人」，無派系的實體、模組機械生物不算可狩獵；
    /// 與原版 Designator_Hunt（AnimalOrWildMan）一致，非血肉野獸原版也不能狩獵。
    /// </summary>
    private static bool IsWildAnimal(Pawn pawn)
    {
        return pawn.RaceProps.Animal && pawn.Faction == null;
    }
}
