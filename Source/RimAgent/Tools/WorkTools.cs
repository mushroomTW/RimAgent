using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>工作優先級與時間表。</summary>
    public sealed class WorkTools
    {
        private readonly IGameWorld _world;

        public WorkTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("set_work_priority", ToolCategory.Work)]
        [Description("Sets one colonist's priority for one work type. Priority 1 is highest, 4 is lowest, 0 disables that work for the colonist. Reports the previous and new value.")]
        public string SetWorkPriority(
            [Description("Colonist name, nickname or id.")] string colonist,
            [Description("Work type name or defName, e.g. Doctor, Cooking, Mining, Growing, Construction, Hauling, Cleaning, Research.")] string workType,
            [Description("0 (disabled), or 1 (highest) to 4 (lowest).")] int priority)
        {
            if (priority < 0 || priority > 4)
            {
                return ToolText.Error($"priority {priority} is out of range. Use 0 (disabled) or 1 (highest) to 4 (lowest).");
            }

            ResolveResult<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
            if (!pawn.Success) return pawn.Error;

            ResolveResult<DefInfo> work = DefResolver.Resolve(workType, _world.WorkTypes, "work type");
            if (!work.Success) return work.Error;

            WorkPriorityInfo current = pawn.Value.WorkPriorities.FirstOrDefault(w =>
                string.Equals(w.WorkTypeDefName, work.Value.DefName, StringComparison.OrdinalIgnoreCase));
            if (current != null && current.Disabled)
            {
                return ToolText.Error($"{pawn.Value.Name} is incapable of {work.Value.Label} and cannot be assigned to it.");
            }

            // 舊值要在命令之前抓：轉接層回傳的物件可能就是之後被改動的那一個。
            string previous = current != null ? current.Priority.ToString() : "?";
            CommandResult result = _world.SetWorkPriority(pawn.Value.Id, work.Value.DefName, priority);
            if (!result.Success) return ToolText.Error(result.Message);

            string suffix = string.IsNullOrEmpty(result.Message) ? string.Empty : " " + result.Message;
            return $"Set {pawn.Value.Name}'s {work.Value.Label} priority {previous} -> {priority}.{suffix}";
        }

        [AgentTool("list_work_priorities", ToolCategory.Work)]
        [Description("Lists work priorities for one colonist, or for everyone. 0 means the work is disabled for that colonist; 1 is highest, 4 lowest.")]
        public string ListWorkPriorities(
            [Description("Colonist name, nickname or id, or \"all\" for everyone.")] string colonist)
        {
            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonist, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            var sb = new StringBuilder();
            foreach (ColonistInfo c in pawns.Value)
            {
                sb.Append(ToolText.Colonist(c)).Append(": ")
                  .Append(string.Join(", ", c.WorkPriorities.Select(w => w.Disabled ? $"{w.Label}=incapable" : $"{w.Label}={w.Priority}")))
                  .AppendLine();
            }
            return sb.ToString().Trim();
        }

        [AgentTool("set_schedule", ToolCategory.Work)]
        [Description("Sets what colonists do during a range of hours in their daily schedule. Hours wrap around midnight, e.g. start 22 end 6 covers 22:00 to 05:59.")]
        public string SetSchedule(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("First hour of the range, 0-23 (inclusive).")] int startHour,
            [Description("Hour the range ends, 0-23 (exclusive). Must differ from startHour.")] int endHour,
            [Description("Assignment: Anything, Work, Joy, Sleep or Meditate.")] string assignment)
        {
            if (startHour < 0 || startHour > 23 || endHour < 0 || endHour > 23)
            {
                return ToolText.Error("hours must be between 0 and 23.");
            }
            if (startHour == endHour)
            {
                return ToolText.Error("startHour and endHour are the same; the range would be empty. Use 0 and 23 plus a second call to cover a full day.");
            }

            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            ResolveResult<DefInfo> assign = DefResolver.Resolve(assignment, _world.TimeAssignments, "time assignment");
            if (!assign.Success) return assign.Error;

            var hours = new List<int>();
            for (int h = startHour; h != endHour; h = (h + 1) % 24) hours.Add(h);

            var failures = new List<string>();
            foreach (ColonistInfo c in pawns.Value)
            {
                foreach (int hour in hours)
                {
                    CommandResult r = _world.SetTimeAssignment(c.Id, hour, assign.Value.DefName);
                    if (!r.Success)
                    {
                        failures.Add($"{c.Name} hour {hour}: {r.Message}");
                        break;
                    }
                }
            }

            string summary = $"Set hours {startHour}-{(endHour + 23) % 24} ({hours.Count} hours) to {assign.Value.Label} for {ToolText.Names(pawns.Value)}.";
            return failures.Count == 0 ? summary : summary + " Failures: " + string.Join("; ", failures);
        }
    }
}
