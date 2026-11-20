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

/// <summary>一句指令分流後的結果：要用哪個思考強度。模型一律沿用 Agent 視窗選的那一個。</summary>
public sealed class DecisionRoute
{
    public string CategoryId { get; init; } = string.Empty;
    public ReasoningLevel Reasoning { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool IsFallback => string.IsNullOrEmpty(CategoryId);
}

/// <summary>
/// 思考強度分流器：每一句指令由決策模型進行 choice 分類，
/// 再用該類別對應的思考強度執行。
/// </summary>
public sealed class DecisionReasoningRouter
{
    public const string QuestionId = "category";
    public const int UserTextHeadChars = 600;
    public const int ReplyHeadChars = 300;
    public const int ReplyTailChars = 300;
    public const int MaxHistoryTurns = 3;

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly Func<RimAgentSettings> _settingsProvider;
    private readonly Func<IReadOnlyList<ChatMessage>> _historyProvider;
    private IDecisionService _service;

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

    /// <summary>解析指令該分流至哪個類別與思考強度。</summary>
    #pragma warning disable VSTHRD202 // 保留 async：讀設定與建題目的例外要放進 Task，不在呼叫當下同步拋出。
    public async Task<DecisionRoute> ResolveAsync(
        string request,
        IReadOnlyList<DecisionRouteCategory> categories,
        CancellationToken ct = default)
    {
        RimAgentSettings settings = _settingsProvider?.Invoke();
        // 設定頁可在模型呼叫期間改類別；先複製一份，await 回來後只使用這份快照。
        List<DecisionRouteCategory> activeCategories = SnapshotCategories(categories ?? settings?.decisionCategories);
        ReasoningLevel fallbackReasoning = settings?.decisionFallbackReasoning ?? ReasoningLevel.Auto;
        float minConfidence = settings != null ? settings.decisionRoutingMinConfidence : 0.6f;

        if (activeCategories.Count == 0)
            return FallbackRoute(fallbackReasoning, "No categories configured.");

        var options = BuildCategoryOptions(activeCategories);
        if (options.Count == 0)
            return FallbackRoute(fallbackReasoning, "No valid category options available.");

        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null)
            return FallbackRoute(fallbackReasoning, "Decision model service is unavailable or not configured.");

        var question = DecisionQuestion.Choice(QuestionId, "Classify the instruction into the most appropriate category.", options);
        var plan = DecisionPlan.Of(question);
        string state = Describe(request, _historyProvider?.Invoke());

        return await EvaluateAndRoute(service, state, plan, fallbackReasoning, minConfidence, activeCategories, ct);
    }
    #pragma warning restore VSTHRD202

    private static async Task<DecisionRoute> EvaluateAndRoute(IDecisionService service, string state, DecisionPlan plan, ReasoningLevel fallbackReasoning, float minConfidence, List<DecisionRouteCategory> activeCategories, CancellationToken ct)
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

        if (result == null || !result.TryGet(QuestionId, out DecisionAnswer answer) || string.IsNullOrWhiteSpace(answer?.Value))
        {
            return FallbackRoute(fallbackReasoning, "Decision model returned no category answer.");
        }

        double confidence = answer.Confidence ?? answer.Probability ?? 0d;
        if (confidence < minConfidence)
        {
            return FallbackRoute(fallbackReasoning,
                string.Create(CultureInfo.InvariantCulture, $"Decision confidence {confidence:F2} is below threshold {minConfidence:F2}."));
        }

        string selectedId = answer.Value.Trim();
        DecisionRouteCategory matched = FindMatchedCategory(activeCategories, selectedId);
        if (matched == null)
        {
            return FallbackRoute(fallbackReasoning, $"Selected category '{selectedId}' not found in categories.");
        }

        return new DecisionRoute
        {
            CategoryId = matched.Id,
            Reasoning = ResolveReasoningLevel(matched, fallbackReasoning),
            Confidence = confidence,
            Reason = string.Create(CultureInfo.InvariantCulture, $"Routed to category '{matched.Id}' with confidence {confidence:F2}.")
        };
    }

    private static Dictionary<string, string> BuildCategoryOptions(List<DecisionRouteCategory> categories)
    {
        // 與 FindMatchedCategory 一致用 OrdinalIgnoreCase；重複或僅大小寫不同的 id 以先出現者為準，避免 ToDictionary 擲例外。
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DecisionRouteCategory cat in categories)
        {
            if (string.IsNullOrWhiteSpace(cat.Id) || string.IsNullOrWhiteSpace(cat.Description)) continue;
            options.TryAdd(cat.Id.Trim(), cat.Description.Trim());
        }
        return options;
    }

    private static DecisionRouteCategory FindMatchedCategory(List<DecisionRouteCategory> categories, string selectedId)
    {
        return categories.FirstOrDefault(c =>
            string.Equals(c.Id?.Trim(), selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private static ReasoningLevel ResolveReasoningLevel(DecisionRouteCategory matched, ReasoningLevel fallbackReasoning)
    {
        return matched.Reasoning != ReasoningLevel.Auto ? matched.Reasoning : fallbackReasoning;
    }

    private static DecisionRoute FallbackRoute(ReasoningLevel fallbackReasoning, string reason)
    {
        return new DecisionRoute
        {
            CategoryId = string.Empty,
            Reasoning = fallbackReasoning,
            Confidence = 0,
            Reason = reason
        };
    }

    private static List<DecisionRouteCategory> SnapshotCategories(IReadOnlyList<DecisionRouteCategory> source)
    {
        var copy = new List<DecisionRouteCategory>();
        if (source == null) return copy;
        foreach (DecisionRouteCategory category in source)
        {
            if (category == null) continue;
            copy.Add(new DecisionRouteCategory(
                category.Id,
                category.Name,
                category.Description,
                category.Reasoning));
        }
        return copy;
    }

    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }

    /// <summary>
    /// 組裝給決策模型的 state：包含當前指令，以及最多前 3 輪對話的精簡片段。
    /// 使用者訊息取頭 600 字、回答取頭 300 尾 300 字。
    /// </summary>
    public static string Describe(string request, IEnumerable<ChatMessage> history = null)
    {
        var sb = new StringBuilder();
        AppendRecentHistory(sb, history);
        sb.Append("<instruction>\n").Append(Escape(request?.Trim() ?? string.Empty)).Append("\n</instruction>");
        return sb.ToString();
    }

    /// <summary>把最近的對話片段寫入 sb：最多取前幾輪的 User/Assistant 訊息，並包在 <recent_history> 標籤內。</summary>
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

    /// <summary>把一條對話訊息的摘要寫入 sb：使用者訊息取頭 600 字、回答取頭 300 尾 300 字。</summary>
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
