#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

public sealed class ProductionTools
{
    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;

    public ProductionTools(IGameWorld world, ConfirmationGate gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
    }

    [AgentTool("list_bills", ToolCategory.Production, ToolRisk.Query)]
    [Description("Lists the work tables that take bills (id, cell) with their numbered bills and repeat counts; give a table to show just that one. Also lists the recipe defNames each table can make now (shortened when listing every table). Bill numbers shift when a bill is deleted, so list again right before delete_bill.")]
    public string ListBills(
        [Description("Work table id or name; empty lists every work table.")] string table = "")
    {
        if (string.IsNullOrWhiteSpace(table)) return ListAllBills();
        Result<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
        if (!target.Success) return target.Error;

        List<BillInfo> bills = [.. _world.GetBills(target.Value.Id)];
        if (bills.Count == 0) return $"{target.Value.Label} has no bills.{Recipes(target.Value.Id, MaxRecipesOneTable)}";

        var sb = new StringBuilder();
        sb.Append("Bills on ").Append(target.Value.Label).Append(':').AppendLine();
        foreach (BillInfo b in bills)
        {
            sb.Append('[').Append(b.Index).Append("] ").Append(b.Label);
            sb.Append(" (").Append(b.Repeat).Append(')').AppendLine();
        }
        return sb.ToString().Trim() + Recipes(target.Value.Id, MaxRecipesOneTable);
    }

    private const int MaxRecipesOneTable = 80;
    private const int MaxRecipesPerTable = 12;

    private string Recipes(int tableId, int max)
    {
        List<DefInfo> recipes = [.. _world.GetTableRecipes(tableId).Where(r => r.Note.Length == 0)];
        if (recipes.Count == 0) return string.Empty;
        string more = recipes.Count > max ? string.Create(CultureInfo.InvariantCulture, $" and {recipes.Count - max} more (list_bills with this table shows all)") : string.Empty;
        return "\nrecipes for add_bill: " + string.Join(", ", recipes.Take(max).Select(r => $"{r.DefName} ({r.Label})")) + more;
    }

    private string ListAllBills()
    {
        List<WorkTableInfo> tables = [.. _world.GetWorkTables()];
        if (tables.Count == 0) return "There are no work tables in the colony.";

        var sb = new StringBuilder();
        sb.Append(tables.Count == 1 ? "1 work table:" : tables.Count.ToString(CultureInfo.InvariantCulture) + " work tables:").AppendLine();
        // 相同的工作台只列一次配方。
        var tablesByRecipes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (WorkTableInfo t in tables)
        {
            string recipes = Recipes(t.Id, MaxRecipesPerTable);
            if (recipes.Length > 0)
            {
                if (!tablesByRecipes.TryGetValue(recipes, out List<int> ids)) tablesByRecipes[recipes] = ids = [];
                ids.Add(t.Id);
            }
            sb.Append('[').Append(t.Id).Append("] ").Append(t.Label);
            sb.Append(" at (").Append(t.X).Append(',').Append(t.Z).Append(')');
            List<BillInfo> bills = [.. _world.GetBills(t.Id)];
            if (bills.Count == 0)
            {
                sb.Append(", no bills").AppendLine();
                continue;
            }
            sb.Append(':').AppendLine();
            foreach (BillInfo b in bills)
            {
                sb.Append("  [").Append(b.Index).Append("] ").Append(b.Label).Append(" (").Append(b.Repeat).Append(')').AppendLine();
            }
        }
        foreach (KeyValuePair<string, List<int>> group in tablesByRecipes)
        {
            sb.Append(string.Join(", ", group.Value.Select(id => string.Create(CultureInfo.InvariantCulture, $"[{id}]")))).Append(' ').AppendLine(group.Key.TrimStart('\n'));
        }
        return sb.ToString().Trim();
    }

    [AgentTool("add_bill", ToolCategory.Production, ToolRisk.Safe)]
    [Description("Adds a production bill to a work table, e.g. make components 5 times. Missing research or facilities are reported.")]
    public string AddBill(
        [Description("Work table id in square brackets from list_bills.")] string table,
        [Description("Recipe name, e.g. \"component\" or \"short bow\".")] string recipe,
        [Description("How many times to make it (at least 1).")] int count)
    {
        Result<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
        if (!target.Success) return target.Error;

        Result<DefInfo> def = DefResolver.Resolve(recipe, _world.GetTableRecipes(target.Value.Id), "bill recipe");
        if (!def.Success) return def.Error + string.Create(CultureInfo.InvariantCulture, $" Only the recipes of {target.Value.Label} [{target.Value.Id}] were searched.");
        if (count < 1) return ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"count must be at least 1, not {count}."));

        string blocked = ToolText.Blocked(_gate, string.Create(CultureInfo.InvariantCulture, $"would add bill {def.Value.Label} x{count} on {target.Value.Label} [{target.Value.Id}]."));
        if (blocked != null) return blocked;

        CommandResult result = _world.AddBill(target.Value.Id, def.Value.DefName, count);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message;
    }

    [AgentTool("delete_bill", ToolCategory.Production, ToolRisk.Confirm)]
    [Description("Deletes one bill from a work table by its number from list_bills. Numbers shift down after every delete: when deleting several, go from the highest number to the lowest or pass expectedLabel. Gathered materials stay where they are.")]
    public string DeleteBill(
        [Description("Work table id in square brackets from list_bills.")] string table,
        [Description("Bill number in square brackets from list_bills.")] int bill,
        [Description("Optional safety check: the bill's label from list_bills; nothing is deleted if that number now points to a different bill.")] string expectedLabel = "")
    {
        Result<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
        if (!target.Success) return target.Error;
        if (bill < 1) return ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"bill number must be at least 1, not {bill}."));

        string expected = (expectedLabel ?? string.Empty).Trim();
        if (expected.Length > 0)
        {
            List<BillInfo> current = [.. _world.GetBills(target.Value.Id)];
            BillInfo atNumber = current.FirstOrDefault(b => b.Index == bill);
            if (atNumber != null && !atNumber.Label.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                string now = string.Join(", ", current.Select(b => string.Create(CultureInfo.InvariantCulture, $"[{b.Index}] {b.Label}")));
                return ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"bill [{bill}] is now \"{atNumber.Label}\", not \"{expected}\"; numbers shift after each delete. Current bills: {now}."));
            }
        }

        string blocked = ToolText.Blocked(_gate, string.Create(CultureInfo.InvariantCulture, $"would delete bill [{bill}] from {target.Value.Label} [{target.Value.Id}]."));
        if (blocked != null) return blocked;

        CommandResult result = _world.DeleteBill(target.Value.Id, bill);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message;
    }

    internal static Result<WorkTableInfo> ResolveTable(string input, IReadOnlyList<WorkTableInfo> tables)
    {
        return TargetResolver.ResolveEntity(
            input,
            tables,
            t => t.Id,
            t => [t.Label],
            t => string.Create(CultureInfo.InvariantCulture, $"[{t.Id}] {t.Label}"),
            "work table",
            "Error: work table is empty. Give an id from list_bills.");
    }
}
