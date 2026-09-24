using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>生產：工作台與帳單查詢、帳單增刪。材料篩選沿用遊戲預設，不另調。</summary>
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
        [Description("Lists the work tables that take bills (id, cell) with their numbered bills and repeat counts; give a table to show just that one. Bill numbers shift when a bill is deleted, so list again right before delete_bill.")]
        public string ListBills(
            [Description("Work table id or name; empty lists every work table.")] string table)
        {
            if (string.IsNullOrWhiteSpace(table)) return ListAllBills();
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

        private string ListAllBills()
        {
            List<WorkTableInfo> tables = _world.GetWorkTables().ToList();
            if (tables.Count == 0) return "There are no work tables in the colony.";

            var sb = new StringBuilder();
            sb.Append(tables.Count == 1 ? "1 work table:" : tables.Count + " work tables:").AppendLine();
            foreach (WorkTableInfo t in tables)
            {
                sb.Append("[").Append(t.Id).Append("] ").Append(t.Label);
                sb.Append(" at (").Append(t.X).Append(",").Append(t.Z).Append(')');
                List<BillInfo> bills = _world.GetBills(t.Id).ToList();
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
            return sb.ToString().Trim();
        }

        [AgentTool("add_bill", ToolCategory.Production, ToolRisk.Safe)]
        [Description("Adds a production bill to a work table, e.g. make components 5 times. Missing research or facilities are reported.")]
        public string AddBill(
            [Description("Work table id in square brackets from list_bills.")] string table,
            [Description("Recipe name, e.g. \"component\" or \"short bow\".")] string recipe,
            [Description("How many times to make it (at least 1).")] int count)
        {
            ResolveResult<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
            if (!target.Success) return target.Error;

            ResolveResult<DefInfo> def = DefResolver.Resolve(recipe, _world.BillRecipes, "bill recipe");
            if (!def.Success) return def.Error;
            if (count < 1) return ToolText.Error($"count must be at least 1, not {count}.");

            string blocked = ToolText.Blocked(_gate, $"would add bill {def.Value.Label} x{count} on {target.Value.Label} [{target.Value.Id}].");
            if (blocked != null) return blocked;

            CommandResult result = _world.AddBill(target.Value.Id, def.Value.DefName, count);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("delete_bill", ToolCategory.Production, ToolRisk.Confirm)]
        [Description("Deletes one bill from a work table by its number from list_bills. Gathered materials stay where they are.")]
        public string DeleteBill(
            [Description("Work table id in square brackets from list_bills.")] string table,
            [Description("Bill number in square brackets from list_bills.")] int bill)
        {
            ResolveResult<WorkTableInfo> target = ResolveTable(table, _world.GetWorkTables());
            if (!target.Success) return target.Error;
            if (bill < 1) return ToolText.Error($"bill number must be at least 1, not {bill}.");

            string blocked = ToolText.Blocked(_gate, $"would delete bill [{bill}] from {target.Value.Label} [{target.Value.Id}].");
            if (blocked != null) return blocked;

            CommandResult result = _world.DeleteBill(target.Value.Id, bill);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        /// <summary>工作台解析：id 精確命中；名稱精確或唯一包含，撞名即報錯。</summary>
        internal static ResolveResult<WorkTableInfo> ResolveTable(string input, IReadOnlyList<WorkTableInfo> tables)
        {
            return TargetResolver.ResolveEntity(
                input,
                tables,
                t => t.Id,
                t => new[] { t.Label },
                t => $"[{t.Id}] {t.Label}",
                "work table",
                "Error: work table is empty. Give an id from list_bills.");
        }
    }
}
