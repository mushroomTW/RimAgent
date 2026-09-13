using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>生產：工作台查詢、帳單增刪。材料篩選沿用遊戲預設，不另調。</summary>
    public sealed class ProductionTools
    {
        private readonly IGameWorld _world;

        public ProductionTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("list_worktables", ToolCategory.Production)]
        [Description("Lists the work tables that can take bills, with their ids, positions and bill counts. Use this before listing, adding or deleting bills.")]
        public string ListWorktables()
        {
            List<WorkTableInfo> tables = _world.GetWorkTables().ToList();
            if (tables.Count == 0) return "There are no work tables in the colony.";

            var sb = new StringBuilder();
            sb.Append(tables.Count == 1 ? "1 work table:" : tables.Count + " work tables:").AppendLine();
            foreach (WorkTableInfo t in tables)
            {
                sb.Append("[").Append(t.Id).Append("] ").Append(t.Label);
                sb.Append(" at (").Append(t.X).Append(",").Append(t.Z).Append(')');
                sb.Append(", ").Append(t.BillCount).Append(t.BillCount == 1 ? " bill" : " bills").AppendLine();
            }
            return sb.ToString().Trim();
        }

        [AgentTool("list_bills", ToolCategory.Production)]
        [Description("Lists the production bills on one work table with their repeat counts. Bill numbers shift when bills are deleted, so list again right before deleting.")]
        public string ListBills(
            [Description("Work table id in square brackets from list_worktables.")] string table)
        {
            ResolveResult<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
            if (!target.Success) return target.Error;

            List<BillInfo> bills = _world.GetBills(target.Value.Id).ToList();
            if (bills.Count == 0) return $"{target.Value.Label} has no bills.";

            var sb = new StringBuilder();
            sb.Append("Bills on ").Append(target.Value.Label).Append(':').AppendLine();
            foreach (BillInfo b in bills)
            {
                sb.Append('[').Append(b.Index).Append("] ").Append(b.Label);
                sb.Append(" (").Append(b.Repeat).Append(')').AppendLine();
            }
            return sb.ToString().Trim();
        }

        [AgentTool("add_bill", ToolCategory.Production)]
        [Description("Adds a production bill to a work table, e.g. make components 5 times. The table must be able to make the recipe; missing research or facilities are reported, not bypassed.")]
        public string AddBill(
            [Description("Work table id in square brackets from list_worktables.")] string table,
            [Description("Recipe name, e.g. \"component\" or \"short bow\".")] string recipe,
            [Description("How many times to make it (at least 1).")] int count)
        {
            ResolveResult<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
            if (!target.Success) return target.Error;

            ResolveResult<DefInfo> def = DefResolver.Resolve(recipe, _world.BillRecipes, "bill recipe");
            if (!def.Success) return def.Error;
            if (count < 1) return ToolText.Error($"count must be at least 1, not {count}.");

            CommandResult result = _world.AddBill(target.Value.Id, def.Value.DefName, count);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("delete_bill", ToolCategory.Production)]
        [Description("Deletes one bill from a work table by its number from list_bills. Materials already gathered stay where they are.")]
        public string DeleteBill(
            [Description("Work table id in square brackets from list_worktables.")] string table,
            [Description("Bill number in square brackets from list_bills.")] int bill)
        {
            ResolveResult<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
            if (!target.Success) return target.Error;
            if (bill < 1) return ToolText.Error($"bill number must be at least 1, not {bill}.");

            CommandResult result = _world.DeleteBill(target.Value.Id, bill);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        /// <summary>工作台解析：id 精確命中；名稱精確或唯一包含，撞名即報錯。</summary>
        internal static ResolveResult<WorkTableInfo> ResolveTable(string input, IReadOnlyList<WorkTableInfo> tables)
        {
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return ResolveResult<WorkTableInfo>.Fail("Error: work table is empty. Give an id from list_worktables.");
            }

            if (int.TryParse(needle, out int id))
            {
                WorkTableInfo byId = tables.FirstOrDefault(t => t.Id == id);
                return byId != null
                    ? ResolveResult<WorkTableInfo>.Ok(byId)
                    : ResolveResult<WorkTableInfo>.Fail($"Error: no work table has id {id}. {ListAll(tables)}");
            }

            List<WorkTableInfo> exact = tables.Where(t => string.Equals(t.Label, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return ResolveResult<WorkTableInfo>.Ok(exact[0]);
            if (exact.Count > 1) return Ambiguous(needle, exact);

            List<WorkTableInfo> contains = tables.Where(t => t.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (contains.Count == 1) return ResolveResult<WorkTableInfo>.Ok(contains[0]);
            if (contains.Count > 1) return Ambiguous(needle, contains);

            return ResolveResult<WorkTableInfo>.Fail($"Error: no work table matches \"{needle}\". {ListAll(tables)}");
        }

        private static ResolveResult<WorkTableInfo> Ambiguous(string needle, List<WorkTableInfo> matches)
        {
            return ResolveResult<WorkTableInfo>.Fail(
                $"Error: \"{needle}\" matches several work tables: {string.Join(", ", matches.Select(Describe))}. Use the id.");
        }

        private static string Describe(WorkTableInfo t)
        {
            return $"[{t.Id}] {t.Label}";
        }

        private static string ListAll(IReadOnlyList<WorkTableInfo> tables)
        {
            return tables.Count == 0
                ? "There are no work tables."
                : "Work tables: " + string.Join(", ", tables.Select(Describe)) + ".";
        }
    }
}
