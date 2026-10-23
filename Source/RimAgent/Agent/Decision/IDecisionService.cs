using System;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>
/// 決策模型服務。實作把「要問什麼」與「怎麼送出去」分開：
/// 呼叫端只描述題目與判準，實際的端點、金鑰與供應商由框架決定。
/// </summary>
public interface IDecisionService
{
    /// <summary>
    /// 以 <paramref name="state"/>（要判斷的文字或結構化資料）執行一組題目。
    /// 同一個 state 的各題平行評估、互不影響。
    /// </summary>
    Task<DecisionResult> EvaluateAsync(string state, DecisionPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// 決策模型不可用：供應商未啟用、缺金鑰、逾時或回應不合法。
/// 呼叫端據此走既有的備援路徑，而不是讓整句指令失敗。
/// </summary>
public sealed class DecisionUnavailableException : Exception
{
    public DecisionUnavailableException(string message, Exception innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// 這次失敗是「設定面」的（供應商未啟用、缺金鑰、供應商 id 不存在）而不是暫時性的
    /// （逾時、連線失敗、額度用盡）。設定面的問題重試也沒用，呼叫端可以整段放棄。
    /// </summary>
    public bool IsConfigurationError { get; init; }
}
