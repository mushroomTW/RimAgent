using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public CommandResult ScheduleSurgery(SurgeryRequest request)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"ScheduleSurgery({request?.PawnId},{request?.RecipeDefName},{request?.BodyPartLabel})"));
        if (request == null) return CommandResult.Fail("no surgery requested");
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == request.PawnId);
        if (pawn == null) return CommandResult.Fail("colonist not found");
        DefInfo recipe = SurgeryRecipes.FirstOrDefault(d => string.Equals(d.DefName, request.RecipeDefName, StringComparison.OrdinalIgnoreCase));
        if (recipe == null) return CommandResult.Fail($"unknown surgery {request.RecipeDefName}");
        if (LockedSurgeries.Contains(recipe.DefName)) return CommandResult.Fail($"{recipe.Label} needs more research first");
        ColonistHealthInfo health = Health.FirstOrDefault(h => h.PawnId == request.PawnId);
        if (health == null)
        {
            health = new ColonistHealthInfo { PawnId = request.PawnId };
            Health.Add(health);
        }
        string part = request.BodyPartLabel ?? string.Empty;
        var pending = health.PendingSurgeries.ToList();
        if (pending.Exists(b => string.Equals(b.Label, recipe.Label, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Part ?? string.Empty, part, StringComparison.OrdinalIgnoreCase)))
        {
            return CommandResult.Fail($"{recipe.Label} is already scheduled on {pawn.Name}");
        }
        pending.Add(new SurgeryBillInfo { Label = recipe.Label, Part = part });
        health.PendingSurgeries = pending;
        return CommandResult.Ok($"Scheduled {recipe.Label} on {pawn.Name}" + (part.Length > 0 ? $" ({part})" : string.Empty) + ".");
    }

    public CommandResult SetPrisonerInteraction(int pawnId, string modeDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetPrisonerInteraction({pawnId},{modeDefName})"));
        PrisonerInfo pawn = Prisoners.FirstOrDefault(p => p.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("prisoner not found");
        DefInfo mode = PrisonerInteractionModes.FirstOrDefault(d => string.Equals(d.DefName, modeDefName, StringComparison.OrdinalIgnoreCase));
        if (mode == null) return CommandResult.Fail($"unknown prisoner interaction mode {modeDefName}");
        pawn.InteractionMode = mode.DefName;
        pawn.InteractionModeLabel = mode.Label;
        return CommandResult.Ok();
    }

    public CommandResult ReleasePrisoner(int pawnId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"ReleasePrisoner({pawnId})"));
        PrisonerInfo pawn = Prisoners.FirstOrDefault(p => p.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("prisoner not found");
        Prisoners.Remove(pawn);
        return CommandResult.Ok($"{pawn.Name} was released and left the colony.");
    }

    public CommandResult TameAnimal(int pawnId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"TameAnimal({pawnId})"));
        AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
        if (animal == null) return CommandResult.Fail("animal not found");
        if (!animal.Wild) return CommandResult.Fail($"{animal.Name} is already tame");
        if (!TameMarked.Add(pawnId)) return CommandResult.Fail($"{animal.Name} is already marked for taming");
        return CommandResult.Ok($"Marked {animal.Name} for taming. A handler will attempt it when available.");
    }

    public CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetAnimalTraining({pawnId},{trainableDefName},{enable})"));
        AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
        if (animal == null) return CommandResult.Fail("animal not found");
        if (animal.Wild) return CommandResult.Fail($"{animal.Name} is wild; tame it first");
        DefInfo training = AnimalTraining.FirstOrDefault(d => string.Equals(d.DefName, trainableDefName, StringComparison.OrdinalIgnoreCase));
        if (training == null) return CommandResult.Fail($"unknown animal training {trainableDefName}");
        string key = string.Create(CultureInfo.InvariantCulture, $"{pawnId}:{training.DefName}");
        if (enable) TrainingOn.Add(key);
        else TrainingOn.Remove(key);
        return CommandResult.Ok(enable
            ? $"{animal.Name} will now be trained in {training.Label}."
            : $"{animal.Name} will no longer be trained in {training.Label}.");
    }

    public CommandResult SetAnimalArea(int pawnId, string areaLabel)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetAnimalArea({pawnId},{areaLabel})"));
        AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
        if (animal == null) return CommandResult.Fail("animal not found");
        if (animal.Wild) return CommandResult.Fail($"{animal.Name} is wild; tame it first");
        string label = areaLabel ?? string.Empty;
        if (label.Length > 0 && !AnimalAreas.Any(d => string.Equals(d.Label, label, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"unknown animal area {label}");
        animal.Area = label;
        return CommandResult.Ok($"{animal.Name}'s allowed area is now {(label.Length > 0 ? label : "unrestricted")}.");
    }

    public CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName)
    {
        Calls.Add(string.IsNullOrEmpty(plantDefName) ? $"AddZone({kind},{rect})" : $"AddZone({kind},{rect},{plantDefName})");
        bool stockpile = string.Equals(kind, "Stockpile", StringComparison.OrdinalIgnoreCase);
        bool growing = string.Equals(kind, "Growing", StringComparison.OrdinalIgnoreCase);
        if (!stockpile && !growing) return CommandResult.Fail($"unknown zone kind {kind}. Use stockpile or growing.");
        int id = Zones.Count == 0 ? 1 : Zones.Max(z => z.Id) + 1;
        string label = stockpile ? string.Create(CultureInfo.InvariantCulture, $"Stockpile {id}") : string.Create(CultureInfo.InvariantCulture, $"Growing zone {id}");
        Zones.Add(new ZoneInfo { Id = id, Label = label, Kind = stockpile ? "Stockpile" : "Growing", CellCount = rect.CellCount });
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Added {label.ToLowerInvariant()} [{id}] covering {rect.CellCount} cells."));
    }

    public CommandResult DeleteZone(int zoneId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"DeleteZone({zoneId})"));
        ZoneInfo zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no zone has id {zoneId}"));
        Zones.Remove(zone);
        ZoneDisallowed.Remove(zoneId);
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Deleted zone [{zoneId}] {zone.Label}. Stored items stay on the ground."));
    }

    public CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetZoneFilter({zoneId})"));
        ZoneInfo zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no zone has id {zoneId}"));
        if (!string.Equals(zone.Kind, "Stockpile", StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail($"{zone.Label} is a growing zone and has no storage filter");
        var allows = (allowDefNames ?? []).ToList();
        var disallows = (disallowDefNames ?? []).ToList();
        if (allows.Count == 0 && disallows.Count == 0) return CommandResult.Fail("nothing to allow or disallow");
        foreach (string defName in allows.Concat(disallows))
        {
            if (!StorageFilterables.Any(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"unknown stored item {defName}");
        }
        if (!ZoneDisallowed.TryGetValue(zoneId, out HashSet<string> banned))
        {
            banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZoneDisallowed[zoneId] = banned;
        }
        foreach (string defName in allows) banned.Remove(defName);
        foreach (string defName in disallows) banned.Add(defName);
        var parts = new List<string>();
        if (allows.Count > 0) parts.Add("allows " + string.Join(", ", allows.Select(ItemLabel).ToArray()));
        if (disallows.Count > 0) parts.Add("disallows " + string.Join(", ", disallows.Select(ItemLabel).ToArray()));
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Zone [{zoneId}] {zone.Label} now {string.Join("; ", parts.ToArray())}."));
    }

    private string ItemLabel(string defName)
    {
        DefInfo def = StorageFilterables.FirstOrDefault(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase));
        return def != null ? def.Label : defName;
    }

    public CommandResult AddBill(int tableId, string recipeDefName, int count)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"AddBill({tableId},{recipeDefName},{count})"));
        WorkTableInfo table = WorkTables.FirstOrDefault(t => t.Id == tableId);
        if (table == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no work table has id {tableId}"));
        DefInfo recipe = GetTableRecipes(tableId).FirstOrDefault(d => string.Equals(d.DefName, recipeDefName, StringComparison.OrdinalIgnoreCase));
        if (recipe == null) return CommandResult.Fail($"unknown bill recipe {recipeDefName}");
        if (count < 1) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"count must be at least 1, not {count}"));
        if (!TableBills.TryGetValue(tableId, out List<BillInfo> bills))
        {
            bills = [];
            TableBills[tableId] = bills;
        }
        bills.Add(new BillInfo { Index = bills.Count + 1, Label = $"Make {recipe.Label}", Repeat = string.Create(CultureInfo.InvariantCulture, $"{count}x") });
        table.BillCount = bills.Count;
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Added bill [{bills.Count}] Make {recipe.Label} x{count} on {table.Label}."));
    }

    public CommandResult DeleteBill(int tableId, int index)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"DeleteBill({tableId},{index})"));
        WorkTableInfo table = WorkTables.FirstOrDefault(t => t.Id == tableId);
        if (table == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no work table has id {tableId}"));
        if (!TableBills.TryGetValue(tableId, out List<BillInfo> bills) || index < 1 || index > bills.Count)
            return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"bill [{index}] does not exist on {table?.Label ?? "that table"}. List the bills again to get valid numbers."));
        BillInfo removed = bills[index - 1];
        bills.RemoveAt(index - 1);
        for (int i = 0; i < bills.Count; i++) bills[i].Index = i + 1;
        table.BillCount = bills.Count;
        return CommandResult.Ok($"Deleted bill {removed.Label} from {table.Label}. Materials already gathered stay where they are.");
    }
}
