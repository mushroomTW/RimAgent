using System;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>呼叫端只描述題目與判準，實際的端點、金鑰與供應商由框架決定。</summary>
public interface IDecisionService
{
    /// <summary>同一個 state 的各題平行評估、互不影響。</summary>
    Task<DecisionResult> EvaluateAsync(string state, DecisionPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>決策模型不可用（供應商未啟用、缺金鑰、逾時或回應不合法）；呼叫端據此走既有的備援路徑，而不是讓整句指令失敗。</summary>
public sealed class DecisionUnavailableException : Exception
{
    public DecisionUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>「設定面」的失敗（供應商未啟用、缺金鑰、供應商 id 不存在）重試也沒用，呼叫端可以整段放棄；逾時、連線失敗、額度用盡則是暫時性的。</summary>
    public bool IsConfigurationError { get; init; }
}
