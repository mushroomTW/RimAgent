using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

public partial class Window_Agent
{
    private void RunSlash(ColonyAgentSession session, SlashCommand command, string args)
    {
        if (command == SlashCommands.Stop)
        {
            StopRun();
            return;
        }
        if (command == SlashCommands.Goal)
        {
            RunGoal(session, args);
            return;
        }
        if (session.IsBusy)
        {
            Notice(session, "RimAgent.Slash.Busy".Translate());
            return;
        }
        if (command == SlashCommands.Clear)
        {
            session.Clear();
            _scrollToBottom = true;
        }
        else if (command == SlashCommands.Compact)
        {
            Compact(session);
        }
    }

    private static void RunGoal(ColonyAgentSession session, string args)
    {
        RimAgentGameComponent component = RimAgentGameComponent.Instance;
        if (component == null) return;
        IReadOnlyList<string> goals = component.Goals;
        switch (SlashCommands.ParseGoal(args, out int index, out string text))
        {
            case GoalAction.Add:
                HandleGoalAdd(session, component, goals, text);
                break;
            case GoalAction.Pause:
                HandleGoalPause(session, component, goals);
                break;
            case GoalAction.Resume:
                HandleGoalResume(session, component, goals);
                break;
            case GoalAction.Edit:
                HandleGoalEdit(session, component, goals, index, text);
                break;
            case GoalAction.Clear:
                HandleGoalClear(session, component, goals);
                break;
            case GoalAction.Show:
                HandleGoalShow(session, goals, index);
                break;
            case GoalAction.Remove:
                HandleGoalRemove(session, component, goals, index);
                break;
            default:
                Notice(session, GoalListText(component));
                break;
        }
    }

    private static void HandleGoalAdd(ColonyAgentSession session, RimAgentGameComponent component, IReadOnlyList<string> goals, string text)
    {
        int newIndex = goals.Count + 1;
        if (text.Length > RimAgentGameComponent.MaxGoalChars)
            Notice(session, "RimAgent.Goal.TooLong".Translate(RimAgentGameComponent.MaxGoalChars));
        else if (component.AddGoal(text)) Notice(session, "RimAgent.Goal.Added".Translate(newIndex, text));
        else Notice(session, "RimAgent.Goal.Full".Translate());
    }

    private static void HandleGoalPause(ColonyAgentSession session, RimAgentGameComponent component, IReadOnlyList<string> goals)
    {
        if (goals.Count == 0) { Notice(session, GoalEmptyTranslationKey.Translate()); return; }
        component.PauseGoal();
        if (session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
        Notice(session, "RimAgent.Goal.Paused".Translate());
    }

    private static void HandleGoalResume(ColonyAgentSession session, RimAgentGameComponent component, IReadOnlyList<string> goals)
    {
        if (goals.Count == 0) { Notice(session, GoalEmptyTranslationKey.Translate()); return; }
        component.ResumeGoal();
        Notice(session, "RimAgent.Goal.Resumed".Translate());
    }

    private static void HandleGoalEdit(ColonyAgentSession session, RimAgentGameComponent component, IReadOnlyList<string> goals, int index, string text)
    {
        if (index < 1 || index > goals.Count || text.Length == 0)
        {
            Notice(session, "RimAgent.Goal.EditUsage".Translate());
        }
        else if (text.Length > RimAgentGameComponent.MaxGoalChars)
        {
            Notice(session, "RimAgent.Goal.TooLong".Translate(RimAgentGameComponent.MaxGoalChars));
        }
        else if (component.EditGoalAt(index - 1, text))
        {
            if (index == 1 && session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
            Notice(session, "RimAgent.Goal.Edited".Translate(index, text));
        }
    }

    private static void HandleGoalClear(ColonyAgentSession session, RimAgentGameComponent component, IReadOnlyList<string> goals)
    {
        if (goals.Count == 0 && string.IsNullOrEmpty(component.LastCompletedGoal))
        { Notice(session, GoalEmptyTranslationKey.Translate()); return; }
        component.ClearGoals();
        if (session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
        Notice(session, "RimAgent.Goal.Cleared".Translate());
    }

    private static void HandleGoalShow(ColonyAgentSession session, IReadOnlyList<string> goals, int index)
    {
        Notice(session, index >= 1 && index <= goals.Count
            ? "RimAgent.Goal.Show".Translate(index, goals[index - 1]).ToString()
            : "RimAgent.Goal.ShowUsage".Translate().ToString());
    }

    private static void HandleGoalRemove(ColonyAgentSession session, RimAgentGameComponent component, IReadOnlyList<string> goals, int index)
    {
        if (index >= 1 && index <= goals.Count)
        {
            string removed = goals[index - 1];
            component.RemoveGoalAt(index - 1);
            if (index == 1 && session.HasPendingGoalConfirmation) session.DiscardPendingConfirmation();
            Notice(session, "RimAgent.Goal.Removed".Translate(index, removed));
        }
        else
        {
            Notice(session, "RimAgent.Goal.NoSuch".Translate(index));
        }
    }

    private static string GetGoalStateTranslationKey(RimAgentGameComponent component)
    {
        return component.CurrentGoalPauseReason switch
        {
            GoalPauseReason.Blocked => "RimAgent.Goal.StateBlocked",
            GoalPauseReason.TurnLimit => "RimAgent.Goal.StateTurnLimit",
            GoalPauseReason.NoProgress => "RimAgent.Goal.StateNoProgress",
            GoalPauseReason.Error => "RimAgent.Goal.StateError",
            _ => component.GoalPaused ? "RimAgent.Goal.StatePaused" : "RimAgent.Goal.StateActive",
        };
    }

    private static string GoalListText(RimAgentGameComponent component)
    {
        IReadOnlyList<string> goals = component.Goals;
        if (goals.Count == 0)
        {
            string empty = GoalEmptyTranslationKey.Translate();
            return string.IsNullOrEmpty(component.LastCompletedGoal) ? empty
                : empty + "\n" + "RimAgent.Goal.LastCompleted".Translate(component.LastCompletedGoal).ToString();
        }
        var sb = new System.Text.StringBuilder("RimAgent.Goal.ListHeader".Translate());
        for (int i = 0; i < goals.Count; i++)
        {
            string title = goals[i].Replace('\r', ' ').Replace('\n', ' ');
            if (title.Length > 180) title = title[..180] + "…";
            sb.Append('\n').Append(i + 1).Append(". ").Append(title);
            if (i == 0)
            {
                string state = GetGoalStateTranslationKey(component);
                sb.Append(" (").Append(state.Translate()).Append(')');
            }
        }
        sb.Append('\n').Append("RimAgent.Goal.Turns".Translate(component.GoalTurns, GoalProgress.MaxTurns,
            component.GoalBlockedTurns, component.GoalIdleTurns));
        if (!string.IsNullOrWhiteSpace(component.GoalLastProgress))
        {
            string progress = component.GoalLastProgress.Replace('\r', ' ').Replace('\n', ' ');
            if (progress.Length > 240) progress = progress[..240] + "…";
            sb.Append('\n').Append("RimAgent.Goal.Progress".Translate(progress));
        }
        return sb.ToString();
    }

    private static void Notice(ColonyAgentSession session, string text)
    {
        session.Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = text,
            InstructionId = session.Transcript.CurrentInstructionId
        });
    }

    private void DrawConfirmBar(Rect rect, ColonyAgentSession session)
    {
        const float WideButtonWidth = 100f;
        // 「不再問」只在這批確實記得住時出現（MCP 工具永遠要問），免得按了沒作用。
        bool canRemember = session.ConfirmGate.CanRememberPending;
        var confirmRect = new Rect(rect.x, rect.y, WideButtonWidth, rect.height);
        var alwaysRect = new Rect(confirmRect.xMax + 6f, rect.y, canRemember ? WideButtonWidth : 0f, rect.height);
        var discardRect = new Rect((canRemember ? alwaysRect.xMax : confirmRect.xMax) + 6f, rect.y, WideButtonWidth, rect.height);
        var noteRect = new Rect(discardRect.xMax + 8f, rect.y, Mathf.Max(0f, rect.xMax - discardRect.xMax - 8f), rect.height);

        if (DrawUnifiedButton(confirmRect, "RimAgent.Confirm".Translate(), PauseColor, PauseColor, enabled: true))
        {
            Confirm(session, rememberForSession: false);
        }
        if (canRemember && DrawUnifiedButton(alwaysRect, "RimAgent.ConfirmAlways".Translate(), PauseColor, PauseColor, enabled: true))
        {
            Confirm(session, rememberForSession: true);
        }
        if (DrawUnifiedButton(discardRect, "RimAgent.ConfirmDiscard".Translate(), EmergencyColor, EmergencyColor, enabled: true))
        {
            session.DiscardPendingConfirmation();
        }

        Text.Font = GameFont.Tiny;
        GUI.color = NoticeColor;
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(noteRect, "RimAgent.ConfirmNote".Translate());
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
    }

    private void Confirm(ColonyAgentSession session, bool rememberForSession)
    {
        Observe(session, session.ConfirmAsync(rememberForSession, BeginRunCts().Token));
    }

    private void Compact(ColonyAgentSession session)
    {
        Observe(session, session.CompactAsync(BeginRunCts().Token));
    }
}
