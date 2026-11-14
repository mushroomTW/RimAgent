using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>工作優先級與時間表。</summary>
public sealed class WorkTools
{
    private sealed class WorkPriorityChange
    {
        public ColonistInfo Colonist;
        public DefInfo Work;
        public int Priority;
        public int Previous;
        /// <summary>回報用的舊值；遊戲沒給這項工作的資料時是 "?"。</summary>
        public string PreviousLabel;

        public string Describe()
        {
            return $"{Colonist.Name}'s {Work.Label} priority {PreviousLabel} -> {Priority}";
        }
    }

    private readonly IGameWorld _world;
    private readonly WorkUndoStack _undo;
    private readonly ConfirmationGate _gate;

    public WorkTools(IGameWorld world, WorkUndoStack undo = null, ConfirmationGate gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _undo = undo;
        _gate = gate;
    }

    [AgentTool("set_work_priorities", ToolCategory.Work, ToolRisk.Safe)]
    [Description("Sets one or more colonists' work priorities in one call and reports each previous and new value. Every assignment is validated first, then all are applied or none.")]
    public string SetWorkPriorities(
        [Description("Semicolon-separated colonist:work=priority, e.g. \"Ali:Doctor=1; Bob:Hauling=2\". Priority 0 disables, 1 highest, 4 lowest.")] string assignments)
    {
        string[] parts = (assignments ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ToolText.Error("assignments is empty. Use colonist:work=priority; colonist:work=priority.");

        if (!TryParseAllAssignments(parts, out List<WorkPriorityChange> changes, out string parseError))
        {
            return ToolText.Error(parseError);
        }

        string blocked = ToolText.Blocked(_gate, "would set " + string.Join("; ", changes.Select(c => c.Describe())) + ".");
        if (blocked != null) return blocked;

        // 1/2/4 可能讓遊戲自動開啟手動優先級；先記下來，復原時才不會留下副作用。
        bool oldUseWorkPriorities = _world.UseWorkPriorities;
        var applied = new List<WorkPriorityChange>();
        var notes = new List<string>();
        string applyError = ApplyWorkPriorityChanges(changes, applied, notes);
        if (applyError != null)
        {
            // 整批回滾時，也把 Agent 為了 1/2/4 打開的手動優先級還原，別留下半套副作用。
            if (_world.UseWorkPriorities != oldUseWorkPriorities) _world.SetUseWorkPriorities(oldUseWorkPriorities);
            return ToolText.Error(applyError);
        }

        if (_undo != null)
        {
            _undo.RecordUseWorkPriorities(oldUseWorkPriorities, _world.UseWorkPriorities);
            foreach (WorkPriorityChange change in applied)
            {
                _undo.RecordPriority(change.Colonist.Id, change.Work.DefName, change.Previous, change.Priority);
            }
        }
        string suffix = notes.Count == 0 ? string.Empty : " " + string.Join(" ", notes);
        return "Set " + string.Join("; ", applied.Select(c => c.Describe())) + "." + suffix;
    }

    private bool TryParseAllAssignments(string[] parts, out List<WorkPriorityChange> changes, out string error)
    {
        changes = [];
        error = null;
        foreach (string raw in parts)
        {
            if (!TryParseAssignment(raw, out WorkPriorityChange change, out string err))
            {
                error = err;
                return false;
            }
            changes.Add(change);
        }
        return true;
    }

    private string ApplyWorkPriorityChanges(List<WorkPriorityChange> changes, List<WorkPriorityChange> applied, List<string> notes)
    {
        foreach (WorkPriorityChange change in changes)
        {
            CommandResult result = _world.SetWorkPriority(change.Colonist.Id, change.Work.DefName, change.Priority);
            if (result.Success && !string.IsNullOrEmpty(result.Message)) notes.Add(result.Message);
            if (!result.Success)
            {
                List<string> rollbackFailures = RollBackWorkPriorities(applied);
                string message = change.Colonist.Name + " " + change.Work.Label + ": " + result.Message;
                if (rollbackFailures.Count > 0) message += " Rollback failures: " + string.Join("; ", rollbackFailures);
                return "No batch changes were kept. " + message;
            }
            applied.Add(change);
        }
        return null;
    }

    /// <summary>
    /// 技能名與工作類型名不同的對照：模型常把技能（Intellectual、Medicine）當成工作類型。
    /// 只在原名對不到任何工作類型時才套用。
    /// </summary>
    private static readonly Dictionary<string, string> SkillToWorkType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Intellectual"] = "Research",
        ["Medicine"] = "Doctor",
        ["Medical"] = "Doctor",
        ["Animals"] = "Handling",
        ["Artistic"] = "Art"
    };

    private bool TryParseAssignment(string raw, out WorkPriorityChange change, out string error)
    {
        change = null;
        error = null;
        string part = raw.Trim();
        int colon = part.IndexOf(':');
        int equals = part.LastIndexOf('=');
        if (colon <= 0 || equals <= colon + 1 || equals == part.Length - 1)
        {
            error = "invalid assignment '" + part + "'. Use colonist:work=priority.";
            return false;
        }
        if (!int.TryParse(part.Substring(equals + 1).Trim(), out int priority) || priority < 0 || priority > 4)
        {
            error = "priority in '" + part + "' is out of range. Use 0 (disabled) or 1 (highest) to 4 (lowest).";
            return false;
        }

        Result<ColonistInfo> pawn = TargetResolver.ResolveColonist(part.Substring(0, colon).Trim(), _world.GetColonists());
        if (!pawn.Success)
        {
            error = pawn.Error;
            return false;
        }
        // 讀不到目前的優先級就記不了原值，/undo 與失敗回滾會寫回錯的數字，寧可不改。
        if (pawn.Value.UnreadableParts.Contains(ColonistInfo.WorkPrioritiesPart))
        {
            error = $"{pawn.Value.Name}'s current work priorities could not be read because another mod threw an error; not changing them.";
            return false;
        }
        string workName = part.Substring(colon + 1, equals - colon - 1).Trim();
        Result<DefInfo> work = DefResolver.Resolve(workName, _world.WorkTypes, "work type");
        if (DefResolver.IsNoMatch(work) && SkillToWorkType.TryGetValue(workName, out string workAlias))
        {
            work = DefResolver.Resolve(workAlias, _world.WorkTypes, "work type");
        }
        if (!work.Success)
        {
            error = work.Error;
            return false;
        }
        WorkPriorityInfo current = pawn.Value.WorkPriorities.FirstOrDefault(w =>
            string.Equals(w.WorkTypeDefName, work.Value.DefName, StringComparison.OrdinalIgnoreCase));
        if (current != null && current.Disabled)
        {
            error = pawn.Value.Name + " is incapable of " + work.Value.Label + " and cannot be assigned to it.";
            return false;
        }
        change = new WorkPriorityChange
        {
            Colonist = pawn.Value,
            Work = work.Value,
            Priority = priority,
            // 舊值要在命令之前抓：轉接層回傳的物件可能就是之後被改動的那一個。
            Previous = current != null ? current.Priority : 3,
            PreviousLabel = current != null ? current.Priority.ToString() : "?"
        };
        return true;
    }

    private List<string> RollBackWorkPriorities(List<WorkPriorityChange> applied)
    {
        var failures = new List<string>();
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            WorkPriorityChange change = applied[i];
            CommandResult rollback = _world.SetWorkPriority(change.Colonist.Id, change.Work.DefName, change.Previous);
            if (!rollback.Success) failures.Add(change.Colonist.Name + " " + change.Work.Label + ": " + rollback.Message);
        }
        return failures;
    }

    [AgentTool("list_work_priorities", ToolCategory.Work, ToolRisk.Query)]
    [Description("Lists work priorities for one colonist or everyone: 0 disabled, 1 highest, 4 lowest, \"incapable\" when the colonist cannot do that work.")]
    public string ListWorkPriorities(
        [Description("Colonist name, nickname or id, or \"all\" for everyone.")] string colonist)
    {
        Result<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonist, _world.GetColonists());
        if (!pawns.Success) return pawns.Error;

        var sb = new StringBuilder();
        foreach (ColonistInfo c in pawns.Value)
        {
            sb.Append(ToolText.Colonist(c)).Append(": ")
              .Append(string.Join(", ", c.WorkPriorities.Select(w => w.Disabled ? $"{w.Label}=incapable" : $"{w.Label}={w.Priority}")))
              .Append(ToolText.Unreadable(c))
              .AppendLine();
        }
        return sb.ToString().Trim();
    }

    [AgentTool("set_schedule", ToolCategory.Work, ToolRisk.Safe)]
    [Description("Sets what colonists do during a range of hours of their daily schedule. The range wraps past midnight: start 22, end 6 covers 22:00-05:59.")]
    public string SetSchedule(
        [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
        [Description("First hour, 0-23 (inclusive).")] int startHour,
        [Description("Hour the range ends, 0-23 (exclusive); must differ from startHour, so a whole day takes two calls.")] int endHour,
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

        Result<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
        if (!pawns.Success) return pawns.Error;

        Result<DefInfo> assign = DefResolver.Resolve(assignment, _world.TimeAssignments, "time assignment");
        if (!assign.Success) return assign.Error;

        var hours = new List<int>();
        for (int h = startHour; h != endHour; h = (h + 1) % 24) hours.Add(h);

        string schedulePreview = $"would set hours {startHour}-{(endHour + 23) % 24} " +
            $"({hours.Count} hours) to {assign.Value.Label} for {ToolText.Names(pawns.Value)}.";
        string blockedSchedule = ToolText.Blocked(_gate, schedulePreview);
        if (blockedSchedule != null) return blockedSchedule;

        var failures = new List<string>();
        ApplyScheduleToColonists(pawns.Value, hours, assign.Value, failures);

        string summary = $"Set hours {startHour}-{(endHour + 23) % 24} ({hours.Count} hours) to {assign.Value.Label} for {ToolText.Names(pawns.Value)}.";
        return failures.Count == 0 ? summary : summary + " Failures: " + string.Join("; ", failures);
    }

    private void ApplyScheduleToColonists(List<ColonistInfo> pawns, List<int> hours, DefInfo assign, List<string> failures)
    {
        foreach (ColonistInfo c in pawns)
        {
            // 舊作息先抓：成功一個小時記一個，復原時才寫得回去。
            IReadOnlyList<string> oldTable = c.Timetable;
            foreach (int hour in hours)
            {
                string oldAssignment = oldTable != null && hour < oldTable.Count ? oldTable[hour] : string.Empty;
                CommandResult r = _world.SetTimeAssignment(c.Id, hour, assign.DefName);
                if (!r.Success)
                {
                    failures.Add($"{c.Name} hour {hour}: {r.Message}");
                    break;
                }
                _undo?.RecordAssignment(c.Id, hour, oldAssignment, assign.DefName);
            }
        }
    }
}
