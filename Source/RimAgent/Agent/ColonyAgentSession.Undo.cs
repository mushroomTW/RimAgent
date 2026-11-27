using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

public sealed partial class ColonyAgentSession
{
    /// <summary>復原上一個有工作設定改動的指令；沒東西可復原回傳 null，呼叫者應在主線程。</summary>
    public string? UndoLast()
    {
        if (!TryBeginExclusiveWork()) throw new InvalidOperationException("Cannot undo while RimAgent is working.");
        try
        {
            UndoFrame? frame = Undo.Pop();
            if (frame == null) return null;

            List<ColonistInfo> colonists = [.. _world.GetColonists()];
            Dictionary<int, string> names = colonists.ToDictionary(c => c.Id, c => c.Name);
            Dictionary<int, ColonistInfo> byId = colonists.ToDictionary(c => c.Id);
            var failures = new List<string>();
            var blockedPriorities = new HashSet<(int, string)>();
            var blockedAssignments = new HashSet<(int, int)>();
            int restored = RestorePriorities(frame, names, byId, blockedPriorities, failures);
            RestoreAssignments(frame, names, byId, blockedAssignments, failures, ref restored);

            bool restoredManualPriorities = false;
            if (frame.OldUseWorkPriorities.HasValue && frame.NewUseWorkPriorities.HasValue
                && frame.OldUseWorkPriorities.Value != frame.NewUseWorkPriorities.Value)
            {
                // Agent 為了 1/2/4 開啟手動優先級：只有目前仍是 Agent 留下的值時才關回去。
                if (blockedPriorities.Count == 0 && _world.UseWorkPriorities == frame.NewUseWorkPriorities.Value)
                {
                    _world.SetUseWorkPriorities(frame.OldUseWorkPriorities.Value);
                    restoredManualPriorities = true;
                }
                else
                {
                    failures.Add("manual work priorities or a work setting changed since RimAgent; left as is.");
                }
            }

            string summary = string.Create(CultureInfo.InvariantCulture, $"Undid \"{frame.Label}\": restored {restored} work setting(s).");
            if (restoredManualPriorities) summary += " Manual work priorities restored.";
            if (failures.Count > 0) summary += " Failures: " + string.Join("; ", failures);
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = summary,
                InstructionId = Transcript.CurrentInstructionId
            });
            return summary;
        }
        finally
        {
            EndExclusiveWork();
        }
    }

    private static string NameOf(Dictionary<int, string> names, int pawnId)
    {
        return names.TryGetValue(pawnId, out string? name) ? name : ("[" + pawnId.ToString(CultureInfo.InvariantCulture) + "]");
    }

    private void RestoreAssignments(UndoFrame frame, Dictionary<int, string> names, Dictionary<int, ColonistInfo> byId,
        HashSet<(int, int)> blockedAssignments, List<string> failures, ref int restored)
    {
        var updatedAssignments = new Dictionary<(int, int), string>();
        for (int i = frame.Assignments.Count - 1; i >= 0; i--)
        {
            UndoTimeAssignment a = frame.Assignments[i];
            var key = (a.PawnId, a.Hour);
            if (blockedAssignments.Contains(key)) continue;
            // 同優先級：玩家手動改過那一小時的作息就不覆寫。
            if (IsAssignmentChangedByPlayer(a, key, updatedAssignments, byId))
            {
                blockedAssignments.Add(key);
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{NameOf(names, a.PawnId)} hour {a.Hour}: changed since RimAgent; not overwritten."));
                continue;
            }
            CommandResult r = _world.SetTimeAssignment(a.PawnId, a.Hour, a.OldAssignmentDefName);
            if (r.Success)
            {
                updatedAssignments[key] = a.OldAssignmentDefName;
                restored++;
            }
            else
            {
                blockedAssignments.Add(key);
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{NameOf(names, a.PawnId)} hour {a.Hour}: {r.Message}"));
            }
        }
    }

    private static bool IsAssignmentChangedByPlayer(UndoTimeAssignment a, (int, int) key,
        Dictionary<(int, int), string> updatedAssignments, Dictionary<int, ColonistInfo> byId)
    {
        if (string.IsNullOrEmpty(a.NewAssignmentDefName)) return false;
        string? current = null;
        if (updatedAssignments.TryGetValue(key, out string? updated))
        {
            current = updated;
        }
        else if (byId.TryGetValue(a.PawnId, out ColonistInfo? pawn) && pawn.Timetable != null && a.Hour >= 0 && a.Hour < pawn.Timetable.Count)
        {
            current = pawn.Timetable[a.Hour];
        }
        return !string.Equals(current, a.NewAssignmentDefName, StringComparison.Ordinal);
    }

    private int RestorePriorities(UndoFrame frame, Dictionary<int, string> names, Dictionary<int, ColonistInfo> byId,
        HashSet<(int, string)> blockedPriorities, List<string> failures)
    {
        int restored = 0;
        for (int i = frame.Priorities.Count - 1; i >= 0; i--)
        {
            UndoWorkPriority p = frame.Priorities[i];
            var key = (p.PawnId, p.WorkTypeDefName);
            if (blockedPriorities.Contains(key)) continue;
            byId.TryGetValue(p.PawnId, out ColonistInfo? pawn);
            WorkPriorityInfo? current = pawn?.WorkPriorities?.FirstOrDefault(w =>
                string.Equals(w.WorkTypeDefName, p.WorkTypeDefName, StringComparison.Ordinal));
            // 復原只覆寫 Agent 真的寫入的那個值；玩家在這之後手動改過就保留玩家的選擇。
            if (p.NewPriority >= 0 && (current == null || current.Priority != p.NewPriority))
            {
                blockedPriorities.Add(key);
                failures.Add($"{NameOf(names, p.PawnId)} {p.WorkTypeDefName}: changed since RimAgent; not overwritten.");
                continue;
            }
            CommandResult r = _world.SetWorkPriority(p.PawnId, p.WorkTypeDefName, p.OldPriority);
            if (r.Success)
            {
                // 同一 pawn、同一工作連續改兩次時，同步更新快照免得下一筆誤判成玩家改過。
                if (current != null) current.Priority = p.OldPriority;
                restored++;
            }
            else
            {
                blockedPriorities.Add(key);
                failures.Add($"{NameOf(names, p.PawnId)} {p.WorkTypeDefName}: {r.Message}");
            }
        }
        return restored;
    }
}
