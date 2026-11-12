using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>System One 對一次基地狀況觸發的判定。</summary>
public sealed class TriageVerdict
{
    /// <summary>需要主 LLM 現在處理的機率。</summary>
    public double Probability { get; init; }
    /// <summary>判定不必處理：本輪不叫主 LLM，稍後再看。</summary>
    public bool Skip { get; init; }
}

/// <summary>
/// 自主任務篩選：基地狀況（受寒、沒床、露宿、停電、停研、心情低落）觸發時，
/// 先由 System One 判斷要不要叫主 LLM。上一輪已經在處理（床在蓋、研究剛排）就先等，
/// 省下一整輪主 LLM 呼叫。襲擊、火災、急救、精神崩潰、缺糧與定期巡檢不篩，一律照跑。
/// 決策模型不可用、判準檔缺失或呼叫失敗時回傳 null，呼叫端照舊叫主 LLM。
/// </summary>
public sealed class DecisionAutonomousTriage
{
    public const string QuestionId = "needs_action";
    /// <summary>低於此機率才跳過：寧可多叫一次，也不要漏掉真正的問題。</summary>
    public const double SkipBelow = 0.3;
    /// <summary>上一輪回覆只留結尾：完成報告通常在最後。</summary>
    public const int LastRunTailChars = 1200;

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private readonly Func<string> _criteriaProvider;
    private IDecisionService _service;

    public DecisionAutonomousTriage(
        IDecisionService decisionService = null,
        Func<string> criteriaProvider = null,
        Func<IDecisionService> serviceFactory = null)
    {
        _decisionService = decisionService;
        _criteriaProvider = criteriaProvider;
        _serviceFactory = serviceFactory;
    }

    /// <summary>只篩基地狀況；應急事件與定期巡檢一律照跑。</summary>
    public static bool IsTriaged(AutonomousTrigger trigger)
    {
        return trigger is AutonomousTrigger.Cold or AutonomousTrigger.BedShortage or AutonomousTrigger.OutdoorsBed
            or AutonomousTrigger.PowerOutage or AutonomousTrigger.Research or AutonomousTrigger.LowMood;
    }

    /// <summary>評估指定的自主任務觸發是否需要執行或略過。</summary>
    /// <param name="trigger">自主任務觸發類型。</param>
    /// <param name="signals">目前的自主任務信號。</param>
    /// <param name="lastRunReply">上一輪同類任務的回覆；沒有則為 null。</param>
    /// <param name="hoursSinceLastRun">距上一輪結束幾個遊戲小時；沒有上一輪為 null。</param>
    /// <param name="cancellationToken">取消語彙基元。</param>
    /// <returns>分流裁決結果；不需分流或無服務時回傳 null。</returns>
    public async Task<TriageVerdict> EvaluateAsync(
        AutonomousTrigger trigger,
        AutonomousSignals signals,
        string lastRunReply,
        float? hoursSinceLastRun,
        CancellationToken cancellationToken = default)
    {
        if (!IsTriaged(trigger)) return null;
        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null) return null;
        string criteria = _criteriaProvider?.Invoke();
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
        catch (Exception)
        {
            return null;
        }
        if (result == null || !result.TryGet(QuestionId, out DecisionAnswer answer) || answer?.Probability == null) return null;
        double p = answer.Probability.Value;
        return new TriageVerdict { Probability = p, Skip = p < SkipBelow };
    }

    internal static string Describe(AutonomousTrigger trigger, AutonomousSignals signals, string lastRunReply, float? hoursSinceLastRun)
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
        if (reply.Length > LastRunTailChars) reply = "[...]" + reply.Substring(reply.Length - LastRunTailChars);
        sb.Append("<last_run");
        if (hoursSinceLastRun.HasValue)
        {
            sb.Append(" hours_ago=\"").Append(hoursSinceLastRun.Value.ToString("0.#", CultureInfo.InvariantCulture)).Append('"');
        }
        sb.AppendLine(">");
        sb.AppendLine(reply.Replace("<", "\\<"));
        sb.Append("</last_run>");
        return sb.ToString();
    }

    /// <summary>排程器訊號的一行摘要；抉擇代答也拿它當殖民地現況。</summary>
    public static string DescribeSignals(AutonomousSignals s)
    {
        float perColonist = s.ColonistCount > 0 ? s.FoodNutrition / s.ColonistCount : 0f;
        return string.Format(CultureInfo.InvariantCulture,
            "Colony: {0} colonists, food {1:0.#} nutrition ({2:0.#} per colonist), hostiles {3}, downed or bleeding {4}, fires {5}, " +
            "near mental break {6}, low mood {7}, cold {8}, without bed {9}, bed outdoors {10}, power {11}, research {12}.",
            s.ColonistCount, s.FoodNutrition, perColonist, s.HostileCount, s.DownedOrBleedingCount, s.FireCount,
            s.NearMentalBreakCount, s.LowMoodCount, s.ColdColonistCount, s.BedlessCount, s.OutdoorsBedCount,
            s.AnyWorkingPower ? "on" : "off", s.HasActiveResearch ? "running" : "none");
    }

    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }
}
