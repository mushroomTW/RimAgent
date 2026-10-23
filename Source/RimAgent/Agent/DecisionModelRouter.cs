using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent;

/// <summary>一句指令分流後的結果：要用哪個模型、哪個思考強度。</summary>
public sealed class DecisionRoute
{
    public string CategoryId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;      // 已解析；空 = 用備援
    public ReasoningLevel Reasoning { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool IsFallback => string.IsNullOrEmpty(CategoryId);
}

/// <summary>
/// 決策模型分流器：每一句指令由決策模型進行 choice 分類，
/// 再用該類別對應的模型與思考強度執行。
/// </summary>
public sealed class DecisionModelRouter
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

    public DecisionModelRouter(
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

    /// <summary>解析指令該分流至哪個類別、模型與思考強度。</summary>
    public async Task<DecisionRoute> ResolveAsync(
        string request,
        IReadOnlyList<DecisionRouteCategory> categories,
        CancellationToken ct = default)
    {
        RimAgentSettings settings = _settingsProvider?.Invoke();
        // 設定頁可在模型呼叫期間改類別；先複製一份，await 回來後只使用這份快照。
        List<DecisionRouteCategory> activeCategories = SnapshotCategories(categories ?? settings?.decisionCategories);
        string fallbackModel = ResolveFallbackModel(settings);
        ReasoningLevel fallbackReasoning = settings?.decisionFallbackReasoning ?? ReasoningLevel.Auto;
        float minConfidence = settings != null ? settings.decisionRoutingMinConfidence : 0.6f;

        if (activeCategories.Count == 0)
        {
            return FallbackRoute(fallbackModel, fallbackReasoning, "No categories configured.");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DecisionRouteCategory cat in activeCategories)
        {
            if (!string.IsNullOrWhiteSpace(cat.id) && !string.IsNullOrWhiteSpace(cat.description))
            {
                options[cat.id.Trim()] = cat.description.Trim();
            }
        }

        if (options.Count == 0)
        {
            return FallbackRoute(fallbackModel, fallbackReasoning, "No valid category options available.");
        }

        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null)
        {
            return FallbackRoute(fallbackModel, fallbackReasoning, "Decision model service is unavailable or not configured.");
        }

        var question = DecisionQuestion.Choice(QuestionId, "Classify the instruction into the most appropriate category.", options);
        var plan = DecisionPlan.Of(question);
        string state = Describe(request, _historyProvider?.Invoke());

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
            return FallbackRoute(fallbackModel, fallbackReasoning, "Decision model evaluation failed: " + ex.Message);
        }

        if (result == null || !result.TryGet(QuestionId, out DecisionAnswer answer) || string.IsNullOrWhiteSpace(answer?.Value))
        {
            return FallbackRoute(fallbackModel, fallbackReasoning, "Decision model returned no category answer.");
        }

        double confidence = answer.Confidence ?? answer.Probability ?? 0d;
        if (confidence < minConfidence)
        {
            return FallbackRoute(fallbackModel, fallbackReasoning,
                $"Decision confidence {confidence:F2} is below threshold {minConfidence:F2}.");
        }

        string selectedId = answer.Value.Trim();
        DecisionRouteCategory matched = activeCategories.FirstOrDefault(c =>
            string.Equals(c.id?.Trim(), selectedId, StringComparison.OrdinalIgnoreCase));

        if (matched == null)
        {
            return FallbackRoute(fallbackModel, fallbackReasoning, $"Selected category '{selectedId}' not found in categories.");
        }

        string resolvedModel = ResolveCategoryModel(matched.model, fallbackModel);

        ReasoningLevel resolvedReasoning = matched.reasoning != ReasoningLevel.Auto
            ? matched.reasoning
            : fallbackReasoning;

        return new DecisionRoute
        {
            CategoryId = matched.id,
            Model = resolvedModel,
            Reasoning = resolvedReasoning,
            Confidence = confidence,
            Reason = $"Routed to category '{matched.id}' with confidence {confidence:F2}."
        };
    }

    private static DecisionRoute FallbackRoute(string fallbackModel, ReasoningLevel fallbackReasoning, string reason)
    {
        return new DecisionRoute
        {
            CategoryId = string.Empty,
            Model = fallbackModel,
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
                category.id,
                category.name,
                category.description,
                category.model,
                category.reasoning));
        }
        return copy;
    }

    /// <summary>
    /// 備援模型的槽值解析：「跟隨 Agent」在分流模式下沒有意義（Agent 當前模型就是分流器本身），
    /// 一律當成空字串，交給框架備援鏈決定。
    /// </summary>
    private static string ResolveFallbackModel(RimAgentSettings settings)
    {
        string value = (settings?.decisionFallbackModel ?? string.Empty).Trim();
        if (string.Equals(value, RimAgentSettings.FollowAgentModel, StringComparison.Ordinal)
            || string.Equals(value, RimAgentSettings.AutoRoutingModel, StringComparison.Ordinal))
        {
            return string.Empty;
        }
        return value;
    }

    /// <summary>
    /// 類別的模型槽值解析：空白或「跟隨 Agent」都用備援模型。
    /// 「跟隨 Agent」在分流模式下指向分流器自己，送給框架會是無效的模型名。
    /// </summary>
    private static string ResolveCategoryModel(string slot, string fallbackModel)
    {
        string value = (slot ?? string.Empty).Trim();
        if (value.Length == 0
            || string.Equals(value, RimAgentSettings.FollowAgentModel, StringComparison.Ordinal)
            || string.Equals(value, RimAgentSettings.AutoRoutingModel, StringComparison.Ordinal))
        {
            return fallbackModel ?? string.Empty;
        }
        return value;
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
        if (history != null)
        {
            var relevant = history
                .Where(m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant) && !string.IsNullOrWhiteSpace(m.Text))
                .ToList();
            int take = Math.Min(relevant.Count, MaxHistoryTurns * 2);
            if (take > 0)
            {
                var slice = relevant.GetRange(relevant.Count - take, take);
                sb.AppendLine("<recent_history>");
                foreach (ChatMessage msg in slice)
                {
                    string text = msg.Text?.Trim() ?? string.Empty;
                    if (msg.Role == ChatRole.User)
                    {
                        if (text.Length > UserTextHeadChars) text = text.Substring(0, UserTextHeadChars) + "…";
                        sb.AppendLine($"user: {Escape(text)}");
                    }
                    else if (msg.Role == ChatRole.Assistant)
                    {
                        if (text.Length > ReplyHeadChars + ReplyTailChars)
                        {
                            text = text.Substring(0, ReplyHeadChars).TrimEnd() + "\n[...]\n" + text.Substring(text.Length - ReplyTailChars).TrimStart();
                        }
                        sb.AppendLine($"assistant: {Escape(text)}");
                    }
                }
                sb.AppendLine("</recent_history>");
            }
        }
        sb.Append("<instruction>\n").Append(Escape(request?.Trim() ?? string.Empty)).Append("\n</instruction>");
        return sb.ToString();
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("<", "\\<");
    }
}
