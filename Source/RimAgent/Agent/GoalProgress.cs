using System;
using System.Text.RegularExpressions;

namespace RimAgent.Agent
{
    public enum GoalTurnStatus
    {
        Continue,
        Complete,
        Blocked
    }

    public readonly struct GoalTurnDecision
    {
        public readonly int Turns;
        public readonly int BlockedTurns;
        public readonly bool Complete;
        public readonly bool Pause;

        public GoalTurnDecision(int turns, int blockedTurns, bool complete, bool pause)
        {
            Turns = turns;
            BlockedTurns = blockedTurns;
            Complete = complete;
            Pause = pause;
        }
    }

    /// <summary>每輪重述完整目標，並以明確結尾標記回報進度，避免壓縮後遺失目標。</summary>
    public static class GoalProgress
    {
        public const int MaxTurns = 60;
        public const int MaxConsecutiveBlocks = 3;
        private static readonly Regex StatusMarker = new Regex(
            @"^[ \t]*\[\[GOAL:(COMPLETE|BLOCKED|CONTINUE)\]\][ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

        public static string BuildPrompt(string objective, string confirmationReport = null)
        {
            string confirmed = string.IsNullOrWhiteSpace(confirmationReport) ? string.Empty
                : "\n\nThe player responded to a confirmation from the previous turn. These are its actual execution results "
                  + "(the earlier preview in conversation history is now stale):\n<confirmation_result>\n"
                  + confirmationReport + "\n</confirmation_result>";
            return "Continue working toward this player goal:\n<goal>\n" + (objective ?? string.Empty)
                + "\n</goal>" + confirmed
                + "\n\nThe goal persists across turns. Inspect the current colony state, use the available tools, "
                + "and take concrete steps toward the full objective. Do not reduce the goal to a smaller task or repeat work already done. "
                + "Before declaring completion, verify the actual result against every part of the goal using at least one tool in this turn. "
                + "If a command needs player confirmation, stop this turn and wait for it. "
                + "If progress is impossible without new information or a changed game state, explain the blocker. "
                + "End your final reply with exactly one status marker on its own line: "
                + "[[GOAL:COMPLETE]] only when verified, [[GOAL:BLOCKED]] when blocked, or [[GOAL:CONTINUE]] otherwise.";
        }

        public static GoalTurnStatus ParseStatus(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return GoalTurnStatus.Continue;
            MatchCollection matches = StatusMarker.Matches(reply);
            if (matches.Count == 0) return GoalTurnStatus.Continue;
            string value = matches[matches.Count - 1].Groups[1].Value.ToUpperInvariant();
            if (value == "COMPLETE") return GoalTurnStatus.Complete;
            return value == "BLOCKED" ? GoalTurnStatus.Blocked : GoalTurnStatus.Continue;
        }

        public static string VisibleReply(string reply)
        {
            return StatusMarker.Replace(reply ?? string.Empty, string.Empty).Trim();
        }

        public static GoalTurnDecision AfterTurn(int previousTurns, int previousBlocks, string reply,
            bool waitingConfirmation, bool hasToolEvidence)
        {
            if (waitingConfirmation) return new GoalTurnDecision(previousTurns, previousBlocks, false, false);
            int turns = previousTurns + 1;
            GoalTurnStatus status = ParseStatus(reply);
            if (reply != null && status == GoalTurnStatus.Complete && hasToolEvidence)
            {
                return new GoalTurnDecision(turns, 0, true, false);
            }
            int blocks = reply == null || status == GoalTurnStatus.Blocked ? previousBlocks + 1 : 0;
            return new GoalTurnDecision(turns, blocks, false,
                blocks >= MaxConsecutiveBlocks || turns >= MaxTurns);
        }
    }
}
