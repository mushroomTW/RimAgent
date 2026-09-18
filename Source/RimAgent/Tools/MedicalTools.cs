using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>健康查詢、醫療等級調整與手術排程。</summary>
    public sealed class MedicalTools
    {
        /// <summary>單一殖民者最多列出的異常數，超過則截斷並註明（省 token）。</summary>
        internal const int MaxConditionsListed = 30;

        private readonly IGameWorld _world;
        private readonly ICommandGate _gate;

        public MedicalTools(IGameWorld world) : this(world, null)
        {
        }

        public MedicalTools(IGameWorld world, ICommandGate gate)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("set_medical_care", ToolCategory.Medical, ToolRisk.Safe)]
        [Description("Sets one colonist's medical care level: how good the medicine doctors may use on them. Higher care consumes better medicine. Reports the previous and new value.")]
        public string SetMedicalCare(
            [Description("Colonist name, nickname or id.")] string colonist,
            [Description("Care level: NoCare, NoMeds, HerbalOrWorse, NormalOrWorse or Best.")] string level)
        {
            ResolveResult<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
            if (!pawn.Success) return pawn.Error;

            ResolveResult<DefInfo> care = DefResolver.Resolve(level, _world.MedicalCareLevels, "medical care level");
            if (!care.Success) return care.Error;

            // 舊值要在命令之前抓：轉接層回傳的物件可能就是之後被改動的那一個。
            string previous = CareLabel(pawn.Value.MedicalCare);
            string blocked = ToolText.Blocked(_gate, $"would set {pawn.Value.Name}'s medical care {previous} -> {care.Value.Label}.");
            if (blocked != null) return blocked;

            CommandResult result = _world.SetMedicalCare(pawn.Value.Id, care.Value.DefName);
            if (!result.Success) return ToolText.Error(result.Message);

            return $"Set {pawn.Value.Name}'s medical care {previous} -> {care.Value.Label}.";
        }

        [AgentTool("get_health", ToolCategory.Medical, ToolRisk.Query)]
        [Description("Shows colonist health: injuries, diseases, bleeding, missing body parts and pending operations, each with tending status. Use this before scheduling surgery.")]
        public string GetHealth(
            [Description("Colonist names or ids separated by commas, or \"all\" for everyone.")] string colonists)
        {
            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            Dictionary<int, ColonistHealthInfo> health = _world.GetHealth().ToDictionary(h => h.PawnId);
            if (pawns.Value.All(c => !HasProblems(c, health)))
            {
                return pawns.Value.Count == 1
                    ? $"{pawns.Value[0].Name} is healthy, nothing needs attention."
                    : $"All {pawns.Value.Count} colonists are healthy, nothing needs attention.";
            }

            var sb = new StringBuilder();
            foreach (ColonistInfo c in pawns.Value)
            {
                sb.Append(ToolText.Colonist(c)).Append(" (health ").Append(ToolText.Pct(c.HealthPercent)).Append(')');
                if (c.Downed) sb.Append(" DOWNED");
                sb.AppendLine();
                if (!HasProblems(c, health))
                {
                    sb.Append("  healthy.").AppendLine();
                    continue;
                }
                ColonistHealthInfo h = health[c.Id];
                foreach (HealthConditionInfo cond in h.Conditions.Take(MaxConditionsListed))
                {
                    sb.Append("  - ").Append(cond.Label);
                    if (!string.IsNullOrEmpty(cond.Part)) sb.Append(" (").Append(cond.Part).Append(')');
                    if (!string.Equals(cond.Kind, HealthConditionInfo.MissingKind, StringComparison.Ordinal))
                    {
                        sb.Append(", severity ").Append(ToolText.Num(cond.Severity));
                    }
                    if (cond.Bleeding) sb.Append(", bleeding");
                    if (cond.Tended) sb.Append(", tended");
                    else if (cond.NeedsTending) sb.Append(", needs tending");
                    sb.AppendLine();
                }
                if (h.Conditions.Count > MaxConditionsListed)
                {
                    sb.Append("  ... and ").Append(h.Conditions.Count - MaxConditionsListed).Append(" more.").AppendLine();
                }
                foreach (SurgeryBillInfo bill in h.PendingSurgeries)
                {
                    sb.Append("  - pending operation: ").Append(bill.Label);
                    if (!string.IsNullOrEmpty(bill.Part)) sb.Append(" (").Append(bill.Part).Append(')');
                    if (bill.Suspended) sb.Append(" [suspended]");
                    sb.AppendLine();
                }
            }
            return sb.ToString().Trim();
        }

        [AgentTool("schedule_surgery", ToolCategory.Medical, ToolRisk.Confirm)]
        [Description("Schedules an operation on a colonist: adds a medical bill to their bill stack for a doctor to carry out. Use get_health first to see injuries and pick the surgery. Reports the scheduled operation and body part, or the reason it cannot be scheduled (unknown surgery, missing research, unsuitable part, already scheduled).")]
        public string ScheduleSurgery(
            [Description("Colonist name, nickname or id.")] string colonist,
            [Description("Surgery name, e.g. \"remove body part\".")] string surgery,
            [Description("Body part, e.g. \"left lung\". Required for surgeries that target a part (the tool lists the candidates if you leave it empty); ignored by surgeries that need no part.")] string part)
        {
            ResolveResult<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
            if (!pawn.Success) return pawn.Error;

            ResolveResult<DefInfo> recipe = DefResolver.Resolve(surgery, _world.SurgeryRecipes, "surgery");
            if (!recipe.Success) return recipe.Error;

            var request = new SurgeryRequest
            {
                PawnId = pawn.Value.Id,
                RecipeDefName = recipe.Value.DefName,
                BodyPartLabel = (part ?? string.Empty).Trim()
            };
            string where = request.BodyPartLabel.Length > 0 ? $" ({request.BodyPartLabel})" : string.Empty;
            string blocked = ToolText.Blocked(_gate, $"would schedule {recipe.Value.Label} on {pawn.Value.Name}{where}.");
            if (blocked != null) return blocked;

            CommandResult result = _world.ScheduleSurgery(request);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        /// <summary>沒有健康資料（世界沒回傳）視為健康，不炸。</summary>
        private static bool HasProblems(ColonistInfo c, Dictionary<int, ColonistHealthInfo> health)
        {
            return health.TryGetValue(c.Id, out ColonistHealthInfo h)
                && (h.Conditions.Count > 0 || h.PendingSurgeries.Count > 0);
        }

        private string CareLabel(string defName)
        {
            foreach (DefInfo d in _world.MedicalCareLevels)
            {
                if (string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase)) return d.Label;
            }
            return defName;
        }
    }
}
