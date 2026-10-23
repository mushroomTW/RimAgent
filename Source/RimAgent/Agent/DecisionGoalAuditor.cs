using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>目標審計結果，包含是否完成與決策理由摘要。</summary>
public sealed class GoalAuditResult
{
    public bool AllDone { get; init; }     // all_done >= 0.5
    public bool Remaining { get; init; }   // remaining >= 0.5
    public bool NeedsUser { get; init; }   // needs_user >= 0.5
    public string Reason { get; init; } = string.Empty;
    public double AllDoneProbability { get; init; }
    public double RemainingProbability { get; init; }
    public double NeedsUserProbability { get; init; }
    /// <summary>真正完成：全部做完、沒有 agent 自己留下的待辦，也沒有在等玩家回答。</summary>
    public bool Completed => AllDone && !Remaining && !NeedsUser;
}

/// <summary>
/// 使用 TypeSafe System One / Jev 決策模型的目標審計器。
/// 在 Agent 宣稱完成目標時進行獨立審計，客觀評估是否真正滿足要求、是否有剩餘步驟或等待玩家。
/// </summary>
public sealed class DecisionGoalAuditor
{
    public const double AllDoneThreshold = 0.5;
    public const double RemainingThreshold = 0.5;
    public const double NeedsUserThreshold = 0.5;
    /// <summary>回覆開頭保留的字數：說明「做了什麼」的前文。</summary>
    public const int ReplyHeadChars = 2000;
    /// <summary>回覆結尾保留的字數：完成報告通常在最後。</summary>
    public const int ReplyTailChars = 8000;

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly Func<string> _criteriaProvider;
    private IDecisionService _service;
    private string _parsedCriteria;
    private DecisionPlan _parsedPlan;

    public DecisionGoalAuditor(
        IDecisionService decisionService = null,
        Func<string> criteriaProvider = null,
        Func<IDecisionService> serviceFactory = null)
    {
        _decisionService = decisionService;
        _criteriaProvider = criteriaProvider;
        _serviceFactory = serviceFactory;
    }

    public async Task<GoalAuditResult> AuditAsync(
        string objective,
        string reply,
        CancellationToken cancellationToken = default,
        string toolEvidence = null)
    {
        if (string.IsNullOrWhiteSpace(objective)) return null;

        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null) return null;

        string criteria = _criteriaProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(criteria)) return null;

        DecisionPlan plan = ResolvePlan(criteria);
        if (plan.IsEmpty) return null;

        string state = Describe(objective, reply, toolEvidence);
        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (result == null ||
            !result.TryGet("all_done", out DecisionAnswer allDoneAnswer) || allDoneAnswer?.Probability == null ||
            !result.TryGet("remaining", out DecisionAnswer remainingAnswer) || remainingAnswer?.Probability == null ||
            !result.TryGet("needs_user", out DecisionAnswer needsUserAnswer) || needsUserAnswer?.Probability == null)
        {
            return null;
        }

        double pAllDone = allDoneAnswer.Probability.Value;
        double pRemaining = remainingAnswer.Probability.Value;
        double pNeedsUser = needsUserAnswer.Probability.Value;

        bool allDone = pAllDone >= AllDoneThreshold;
        bool remaining = pRemaining >= RemainingThreshold;
        bool needsUser = pNeedsUser >= NeedsUserThreshold;

        string reason = BuildReason(allDone, remaining, needsUser, pAllDone, pRemaining, pNeedsUser);

        return new GoalAuditResult
        {
            AllDone = allDone,
            Remaining = remaining,
            NeedsUser = needsUser,
            AllDoneProbability = pAllDone,
            RemainingProbability = pRemaining,
            NeedsUserProbability = pNeedsUser,
            Reason = reason
        };
    }

    private static string BuildReason(bool allDone, bool remaining, bool needsUser, double pAllDone, double pRemaining, double pNeedsUser)
    {
        if (needsUser)
        {
            return $"Decision audit indicated player input is required (needs_user: {pNeedsUser:P0}).";
        }
        if (remaining)
        {
            return $"Decision audit found unfinished follow-up steps mentioned by the assistant (remaining: {pRemaining:P0}).";
        }
        if (!allDone)
        {
            return $"Decision audit found goal requirements not yet verified or complete (all_done: {pAllDone:P0}).";
        }
        return $"Decision audit verified that all requirements are complete (all_done: {pAllDone:P0}).";
    }

    public static string Describe(string objective, string reply, string toolEvidence = null)
    {
        string trimmedReply = (reply ?? string.Empty).Trim();
        // 頭尾都留：開頭說明了做了什麼，結尾是完成報告，中間是過程。
        if (trimmedReply.Length > ReplyHeadChars + ReplyTailChars)
        {
            trimmedReply = trimmedReply.Substring(0, ReplyHeadChars).TrimEnd()
                + "\n[...]\n"
                + trimmedReply.Substring(trimmedReply.Length - ReplyTailChars).TrimStart();
        }
        string state = Section("objective", objective) + Section("agent_reply", trimmedReply);
        if (!string.IsNullOrWhiteSpace(toolEvidence))
        {
            state += Section("tool_evidence", toolEvidence);
        }
        return state;
    }

    private static string Section(string tag, string value)
    {
        return "<" + tag + ">\n" + Escape(value ?? string.Empty) + "\n</" + tag + ">\n";
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("<", "\\<");
    }

    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }

    private DecisionPlan ResolvePlan(string criteria)
    {
        if (_parsedPlan == null || !string.Equals(_parsedCriteria, criteria, StringComparison.Ordinal))
        {
            _parsedCriteria = criteria;
            _parsedPlan = ParsePlan(criteria);
        }
        return _parsedPlan;
    }

    public static DecisionPlan ParsePlan(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return DecisionPlan.Of();

        string allDoneInstructions = "Have all requirements of the player's goal been fully satisfied and verified complete?";
        string allDoneWhenTrue = "All requirements of the goal are met, confirmed by tools or observation, and nothing requested by the player remains unfinished.";
        string allDoneWhenFalse = "Some requirements remain incomplete, unattempted, unverified, failed, or partially done.";

        string remainingInstructions = "Did the agent state or imply that further steps, remaining tasks, or follow-ups are needed?";
        string remainingWhenTrue = "The agent explicitly noted unfinished steps, remaining tasks, or future work that still needs doing.";
        string remainingWhenFalse = "The agent did not indicate any remaining work or pending steps.";

        string needsUserInstructions = "Is the agent waiting for the player to make a choice, provide input, or resolve an external blocker?";
        string needsUserWhenTrue = "The agent asked the player a question, requested a decision, or paused for player intervention.";
        string needsUserWhenFalse = "The agent is not waiting for player guidance or input to proceed.";

        string[] lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentSection = null;
        var sectionLines = new List<string>();

        void ProcessSection(string section, List<string> slines)
        {
            if (string.IsNullOrEmpty(section)) return;
            string key = section.Trim().ToLowerInvariant();
            if (key == "all_done")
            {
                ParseNoulSection(slines, ref allDoneInstructions, ref allDoneWhenTrue, ref allDoneWhenFalse);
            }
            else if (key == "remaining")
            {
                ParseNoulSection(slines, ref remainingInstructions, ref remainingWhenTrue, ref remainingWhenFalse);
            }
            else if (key == "needs_user")
            {
                ParseNoulSection(slines, ref needsUserInstructions, ref needsUserWhenTrue, ref needsUserWhenFalse);
            }
        }

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                ProcessSection(currentSection, sectionLines);
                currentSection = trimmed.Substring(3).Trim();
                sectionLines.Clear();
            }
            else if (currentSection != null)
            {
                sectionLines.Add(line);
            }
        }
        ProcessSection(currentSection, sectionLines);

        var qAllDone = DecisionQuestion.Noul("all_done", allDoneInstructions, allDoneWhenTrue, allDoneWhenFalse);
        var qRemaining = DecisionQuestion.Noul("remaining", remainingInstructions, remainingWhenTrue, remainingWhenFalse);
        var qNeedsUser = DecisionQuestion.Noul("needs_user", needsUserInstructions, needsUserWhenTrue, needsUserWhenFalse);

        return DecisionPlan.Of(qAllDone, qRemaining, qNeedsUser);
    }

    private static void ParseNoulSection(List<string> lines, ref string instructions, ref string whenTrue, ref string whenFalse)
    {
        var instLines = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("true:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("true：", StringComparison.OrdinalIgnoreCase))
            {
                whenTrue = trimmed.Substring(5).Trim();
            }
            else if (trimmed.StartsWith("false:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("false：", StringComparison.OrdinalIgnoreCase))
            {
                whenFalse = trimmed.Substring(6).Trim();
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                instLines.Add(trimmed);
            }
        }
        if (instLines.Count > 0)
        {
            instructions = string.Join("\n", instLines);
        }
    }
}
