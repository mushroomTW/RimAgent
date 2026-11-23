#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent;

/// <summary>一句指令的思考強度：決策模型直接挑選；信心不足或不可用時用備援強度。模型一律沿用 Agent 視窗選的那一個。</summary>
public sealed class DecisionRoute
{
    public ReasoningLevel Reasoning { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool IsFallback { get; init; }
}

public sealed class DecisionReasoningRouter
{
    public const string QuestionId = "reasoning";
    public const int UserTextHeadChars = 600;
    public const int ReplyHeadChars = 300;
    public const int ReplyTailChars = 300;
    public const int MaxHistoryTurns = 3;

    private static readonly Dictionary<string, string> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["off"] = "No deliberation: a direct lookup or a reply to small talk.",
        ["low"] = "Light: a routine order or a question answered by one or two observations.",
        ["medium"] = "Moderate: several steps, such as planning a build or reassigning work.",
        ["high"] = "Deep: risky or far-reaching decisions, combat, or long multi-step plans."
    };

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly Func<RimAgentSettings> _settingsProvider;
    private readonly Func<IReadOnlyList<ChatMessage>> _historyProvider;
    private IDecisionService _service;
    private DecisionRoute _last;

    public const int ShortFollowUpChars = 8;

    public DecisionReasoningRouter(
        IDecisionService decisionService = null,
        Func<IDecisionService> serviceFactory = null,
        Func<RimAgentSettings> settingsProvider = null,
        Func<IReadOnlyList<ChatMessage>> historyProvider = null)
    {
        _decisionService = decisionService;
        _serviceFactory = serviceFactory;
        _settingsProvider = settingsProvider ?? (() => RimAgentMod.Settings);
        _historyProvider = historyProvider;
    }

    #pragma warning disable VSTHRD202 // 保留 async：讀設定與建題目的例外要放進 Task，不在呼叫當下同步拋出。
    public async Task<DecisionRoute> ResolveAsync(string request, CancellationToken ct = default)
    {
        RimAgentSettings settings = _settingsProvider?.Invoke();
        ReasoningLevel fallbackReasoning = settings?.decisionFallbackReasoning ?? ReasoningLevel.Auto;
        float minConfidence = settings != null ? settings.decisionRoutingMinConfidence : 0.6f;

        DecisionRoute last = _last;
        if (last != null && IsShortFollowUp(request))
        {
            return new DecisionRoute
            {
                Reasoning = last.Reasoning,
                Confidence = last.Confidence,
                Reason = "Short follow-up; reused the previous reasoning level."
            };
        }

        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null)
            return FallbackRoute(fallbackReasoning, "Decision model service is unavailable or not configured.");

        var question = DecisionQuestion.Choice(QuestionId,
            "How much reasoning effort does the colony agent need to carry out this instruction well?", Levels);
        string state = Describe(request, _historyProvider?.Invoke());

        DecisionRoute route = await EvaluateAndRoute(service, state, DecisionPlan.Of(question), fallbackReasoning, minConfidence, ct);
        _last = route.IsFallback ? null : route;
        return route;
    }
    #pragma warning restore VSTHRD202

    /// <summary>「好」「繼續」這類短句是在延續上一句，難度跟上一句一樣，不值得多問一次決策模型。</summary>
    /// <summary>清空對話後，短句不再是上一句的後續。</summary>
    internal void Reset() => _last = null;

    internal static bool IsShortFollowUp(string request)
    {
        string text = request?.Trim() ?? string.Empty;
        return text.Length > 0 && text.Length <= ShortFollowUpChars;
    }

    private static async Task<DecisionRoute> EvaluateAndRoute(IDecisionService service, string state, DecisionPlan plan, ReasoningLevel fallbackReasoning, float minConfidence, CancellationToken ct)
    {
        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, plan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FallbackRoute(fallbackReasoning, "Decision model evaluation failed: " + ex.Message);
        }

        if (result == null || !result.TryGet(QuestionId, out DecisionAnswer answer) || !TryParseLevel(answer?.Value, out ReasoningLevel level))
        {
            return FallbackRoute(fallbackReasoning, "Decision model returned no reasoning level.");
        }

        double confidence = answer.Confidence ?? answer.Probability ?? 0d;
        if (confidence < minConfidence)
        {
            return FallbackRoute(fallbackReasoning,
                string.Create(CultureInfo.InvariantCulture, $"Decision confidence {confidence:F2} is below threshold {minConfidence:F2}."));
        }

        return new DecisionRoute
        {
            Reasoning = level,
            Confidence = confidence,
            Reason = string.Create(CultureInfo.InvariantCulture, $"Decision model chose '{level}' with confidence {confidence:F2}.")
        };
    }

    private static bool TryParseLevel(string value, out ReasoningLevel level)
    {
        level = ReasoningLevel.Auto;
        string key = value?.Trim();
        return !string.IsNullOrEmpty(key) && Levels.ContainsKey(key) && Enum.TryParse(key, ignoreCase: true, out level);
    }

    private static DecisionRoute FallbackRoute(ReasoningLevel fallbackReasoning, string reason)
    {
        return new DecisionRoute
        {
            Reasoning = fallbackReasoning,
            Confidence = 0,
            Reason = reason,
            IsFallback = true
        };
    }

    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }

    public static string Describe(string request, IEnumerable<ChatMessage> history = null)
    {
        var sb = new StringBuilder(DescribeHistory(history));
        sb.Append("<instruction>\n").Append(Escape(request?.Trim() ?? string.Empty)).Append("\n</instruction>");
        return sb.ToString();
    }

    /// <summary>最近幾回合對話（玩家原話與 Agent 回覆截頭尾）；思考強度分流與風險審查共用。</summary>
    public static string DescribeHistory(IEnumerable<ChatMessage> history)
    {
        var sb = new StringBuilder();
        AppendRecentHistory(sb, history);
        return sb.ToString();
    }

    private static void AppendRecentHistory(StringBuilder sb, IEnumerable<ChatMessage> history)
    {
        if (history == null) return;
        var relevant = history
            .Where(m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant) && !string.IsNullOrWhiteSpace(m.Text))
            .ToList();
        int take = Math.Min(relevant.Count, MaxHistoryTurns * 2);
        if (take > 0)
        {
            var slice = relevant.GetRange(relevant.Count - take, take);
            sb.AppendLine("<recent_history>");
            foreach (ChatMessage msg in slice) AppendMessage(sb, msg);
            sb.AppendLine("</recent_history>");
        }
    }

    private static void AppendMessage(StringBuilder sb, ChatMessage msg)
    {
        string text = msg.Text?.Trim() ?? string.Empty;
        if (msg.Role == ChatRole.User)
        {
            if (text.Length > UserTextHeadChars) text = text[..UserTextHeadChars] + "…";
            sb.AppendLine(CultureInfo.InvariantCulture, $"user: {Escape(text)}");
        }
        else if (msg.Role == ChatRole.Assistant)
        {
            if (text.Length > ReplyHeadChars + ReplyTailChars)
            {
                text = text.Substring(0, ReplyHeadChars).TrimEnd() + "\n[...]\n" + text.Substring(text.Length - ReplyTailChars).TrimStart();
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"assistant: {Escape(text)}");
        }
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("<", "\\<", StringComparison.Ordinal);
    }
}
