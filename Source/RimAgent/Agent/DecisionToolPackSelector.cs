using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Tools;

namespace RimAgent.Agent;

/// <summary>
/// 每句指令先由 System One 判斷要用到哪些工具包，主 LLM 一開始就拿到那些工具，
/// 省下一步 load_toolpack。每個包一道是非題，一次請求平行評估。
/// 判錯也補得回來：沒預載的包主 LLM 仍可自己載入。
/// 決策模型不可用或呼叫失敗時回傳空清單。
/// </summary>
internal sealed class DecisionToolPackSelector
{
    /// <summary>達到此機率才預載。</summary>
    public const double LoadThreshold = 0.5;

    private readonly IDecisionService _decisionService;
    private readonly Func<IDecisionService> _serviceFactory;
    private IDecisionService _service;

    public DecisionToolPackSelector(IDecisionService decisionService = null, Func<IDecisionService> serviceFactory = null)
    {
        _decisionService = decisionService;
        _serviceFactory = serviceFactory;
    }

    /// <summary>利用決策模型依目前對話狀態選取需要的工具包。</summary>
    /// <param name="state">指令與最近幾輪對話（<see cref="DecisionReasoningRouter.Describe"/>）。</param>
    /// <param name="candidates">目前可用但尚未載入的包。</param>
    /// <param name="cancellationToken">取消語彙基元。</param>
    /// <returns>應載入的工具包 ID 清單。</returns>
    public async Task<IReadOnlyList<string>> SelectAsync(
        string state,
        IReadOnlyList<ToolPacks.Pack> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0 || string.IsNullOrWhiteSpace(state)) return Array.Empty<string>();
        IDecisionService service = _decisionService ?? ResolveService();
        if (service == null) return Array.Empty<string>();

        DecisionQuestion[] questions = [.. candidates.Select(p => DecisionQuestion.Noul(
            p.Id,
            "Will carrying out the instruction need tools from the \"" + p.Id + "\" tool pack: " + p.Summary + "?",
            "The instruction clearly involves what this pack covers.",
            "The instruction does not involve what this pack covers, or only needs to read general colony state."))];

        DecisionResult result;
        try
        {
            result = await service.EvaluateAsync(state, DecisionPlan.Of(questions), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var selected = new List<string>();
        foreach (ToolPacks.Pack pack in candidates)
        {
            if (result != null && result.TryGet(pack.Id, out DecisionAnswer answer)
                && (answer?.Probability ?? 0d) >= LoadThreshold)
            {
                selected.Add(pack.Id);
            }
        }
        return selected;
    }

    private IDecisionService ResolveService()
    {
        if (_service == null) _service = _serviceFactory?.Invoke();
        return _service;
    }
}
