using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

/// <summary>
/// 自動模式下的預執行風險檢查介面。
/// 實作包含以一般 LLM 呼叫解析的 <see cref="RiskClassifier"/>，
/// 以及由 TypeSafe System One 決策模型判定的 <see cref="DecisionRiskChecker"/>。
/// </summary>
public interface IRiskChecker
{
    Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken);
}
