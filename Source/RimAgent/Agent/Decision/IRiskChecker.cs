using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Agent;

public interface IRiskChecker
{
    Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken);
}
