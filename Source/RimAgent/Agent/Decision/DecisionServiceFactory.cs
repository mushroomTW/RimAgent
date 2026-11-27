using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework;

namespace RimAgent.Agent;

/// <summary>建立決策模型服務並回答「玩家到底有沒有設定決策模型」；供應商、金鑰與端點都在 RimLLM Framework 的設定裡，框架版本太舊或尚未初始化時一律視為「沒有決策模型」。</summary>
public static class DecisionServiceFactory
{
    /// <summary>框架未初始化或版本太舊時回 null（功能不啟用），不擲例外。</summary>
    public static DecisionAvailability? GetAvailability()
    {
        try
        {
            return RimLLMProvider.GetDecisionAvailability();
        }
        catch (MissingMethodException)
        {
            // 框架版本太舊，還沒有決策模型的公開查詢。
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // 框架尚未初始化。
            return null;
        }
    }

    public static bool CanUseDecisionModel()
    {
        return IsEnabledInSettings && CanUseDecisionModel(GetAvailability());
    }

    /// <summary>RimAgent 設定的總開關；設定還沒載入（如單元測試）時視為開啟。</summary>
    private static bool IsEnabledInSettings => RimAgentMod.Settings?.decisionEnabled != false;

    /// <summary>框架沒有啟用決策模型時一律不可用；RimAgent 另外指定供應商也不例外。</summary>
    internal static bool CanUseDecisionModel(DecisionAvailability? availability)
    {
        return availability != null && availability.Enabled && !string.IsNullOrWhiteSpace(availability.Model);
    }

    /// <summary>沒有任何可用的決策供應商時回 null（呼叫端據此走備援路徑）；供應商與模型每次呼叫時從設定讀取，留空代表沿用框架啟用的那一個。</summary>
    public static IDecisionService? Create()
    {
        if (!IsEnabledInSettings) return null;
        if (!CanUseDecisionModel(GetAvailability()))
        {
            return null;
        }
        try
        {
            RimLLMDecisionClient client = RimLLMProvider.CreateDecisionService(ColonyAgentSession.ModId);
            return new JevDecisionService(
                client,
                () => RimAgentMod.Settings?.decisionProvider,
                () => RimAgentMod.Settings?.decisionModel);
        }
        catch (InvalidOperationException)
        {
            // 框架尚未初始化。
            return null;
        }
        catch (MissingMethodException)
        {
            return null;
        }
        catch (TypeLoadException)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        Func<string?>? providerId, CancellationToken cancellationToken = default)
    {
        try
        {
            RimLLMDecisionClient client = RimLLMProvider.CreateDecisionService(ColonyAgentSession.ModId);
            return await client.FetchModelsAsync(providerId?.Invoke() ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 模型清單只是設定頁的下拉選項；取不到就讓玩家手打。
            return [];
        }
    }
}
