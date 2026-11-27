using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

public sealed class TriageVerdict
{
    public double Probability { get; init; }
    public bool Skip { get; init; }
}

/// <summary>基地狀況觸發時先由 System One 判斷要不要叫主 LLM（上一輪已在處理就先等，省下一整輪呼叫）；襲擊、火災、急救、精神崩潰、缺糧與定期巡檢不篩。決策模型不可用、判準檔缺失或呼叫失敗時回傳 null，呼叫端照舊叫主 LLM。</summary>
public sealed class DecisionAutonomousTriage
{
    public const string QuestionId = "needs_action";
    /// <summary>低於此機率才跳過：寧可多叫一次，也不要漏掉真正的問題。</summary>
    public const double SkipBelow = 0.3;
    /// <summary>上一輪回覆只留結尾：完成報告通常在最後。</summary>
    public const int LastRunTailChars = 1200;

    private readonly IDecisionService? _decisionService;
    private readonly Func<IDecisionService?>? _serviceFactory;
    private readonly Func<string?>? _criteriaProvider;

    public DecisionAutonomousTriage(
        IDecisionService? decisionService = null,
        Func<string?>? criteriaProvider = null,
        Func<IDecisionService?>? serviceFactory = null)
    {
        _decisionService = decisionService;
        _criteriaProvider = criteriaProvider;
        _serviceFactory = serviceFactory;
    }

    public static bool IsTriaged(AutonomousTrigger trigger)
    {
        return trigger == AutonomousTrigger.Alert;
    }

    /// <returns>分流裁決結果；不需分流或無服務時回傳 null。</returns>
    public async Task<TriageVerdict?> EvaluateAsync(
        AutonomousTrigger trigger,
        AutonomousSignals signals,
        string? lastRunReply,
        float? hoursSinceLastRun,
        CancellationToken cancellationToken = default)
    {
        if (!IsTriaged(trigger)) return null;
        IDecisionService? service = _decisionService ?? _serviceFactory?.Invoke();
        if (service == null) return null;
        string? criteria = _criteriaProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(criteria)) return null;

        var plan = DecisionPlan.Of(DecisionQuestion.Noul(QuestionId, criteria.Trim()));
        string state = Describe(trigger, signals, lastRunReply, hoursSinceLastRun);
        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DecisionDiagnostics.Warn(nameof(DecisionAutonomousTriage), ex);
            return null;
        }
        if (result == null || !result.TryGet(QuestionId, out DecisionAnswer? answer) || answer.Probability == null) return null;
        double p = answer.Probability.Value;
        return new TriageVerdict { Probability = p, Skip = p < SkipBelow };
    }

    internal static string Describe(AutonomousTrigger trigger, AutonomousSignals signals, string? lastRunReply, float? hoursSinceLastRun)
    {
        var sb = new StringBuilder();
        sb.Append("Condition: ").AppendLine(trigger.ToString());
        sb.AppendLine(DescribeSignals(signals));
        if (string.IsNullOrWhiteSpace(lastRunReply))
        {
            sb.Append("No earlier run for this condition.");
            return sb.ToString();
        }
        string reply = lastRunReply.Trim();
        if (reply.Length > LastRunTailChars) reply = "[...]" + reply[(reply.Length - LastRunTailChars)..];
        sb.Append("<last_run");
        if (hoursSinceLastRun.HasValue)
        {
            sb.Append(" hours_ago=\"").Append(hoursSinceLastRun.Value.ToString("0.#", CultureInfo.InvariantCulture)).Append('"');
        }
        sb.AppendLine(">");
        sb.AppendLine(reply.Replace("<", "\\<", StringComparison.Ordinal));
        sb.Append("</last_run>");
        return sb.ToString();
    }

    public static string DescribeSignals(AutonomousSignals s)
    {
        float perColonist = s.ColonistCount > 0 ? s.FoodNutrition / s.ColonistCount : 0f;
        return string.Format(CultureInfo.InvariantCulture,
            "Colony: {0} colonists, food {1:0.#} nutrition ({2:0.#} per colonist), hostiles {3}, downed or bleeding {4}, fires {5}, " +
            "near mental break {6}. Alerts: {7}.",
            s.ColonistCount, s.FoodNutrition, perColonist, s.HostileCount, s.DownedOrBleedingCount, s.FireCount,
            s.NearMentalBreakCount, string.IsNullOrEmpty(s.Alerts) ? "none" : s.Alerts);
    }
}
