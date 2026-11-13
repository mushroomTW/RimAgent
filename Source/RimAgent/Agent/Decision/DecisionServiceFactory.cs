using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework;

namespace RimAgent.Agent;

/// <summary>
/// 建立決策模型服務，並回答「玩家到底有沒有設定決策模型」。
///
/// 供應商、金鑰與端點都在 RimLLM Framework 的設定裡；RimAgent 只決定
/// 用哪一個供應商與哪一個模型（都可留空＝沿用框架啟用的那一個）。
/// 框架版本太舊或尚未初始化時一律視為「沒有決策模型」，功能不啟動。
/// </summary>
public static class DecisionServiceFactory
{
    /// <summary>框架未初始化或版本太舊時回 null（功能不啟用），不擲例外。</summary>
    public static DecisionAvailability GetAvailability()
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

    /// <summary>指定供應商可獨立於框架預設供應商啟用。</summary>
    public static bool CanUseDecisionModel(string providerId)
    {
        return IsEnabledInSettings && CanUseDecisionModel(GetAvailability(), providerId);
    }

    /// <summary>RimAgent 設定的總開關；設定還沒載入（如單元測試）時視為開啟。</summary>
    private static bool IsEnabledInSettings => RimAgentMod.Settings?.decisionEnabled != false;

    /// <summary>框架沒有啟用決策模型時一律不可用；RimAgent 另外指定供應商也不例外。</summary>
    internal static bool CanUseDecisionModel(DecisionAvailability availability, string providerId)
    {
        return availability != null && availability.Enabled && !string.IsNullOrWhiteSpace(availability.Model);
    }

    /// <summary>
    /// 建立決策服務；沒有任何可用的決策供應商時回 null（呼叫端據此走備援路徑）。
    /// </summary>
    /// <param name="providerId">決策供應商 id；null 或空字串代表沿用框架啟用的那一個。</param>
    /// <param name="model">模型名稱；null 或空字串代表沿用該供應商設定的模型。</param>
    public static IDecisionService Create(Func<string> providerId = null, Func<string> model = null)
    {
        // 框架沒有決策模型、或玩家在設定裡關掉決策模型時一律回 null，各功能照常走備援路徑。
        if (!IsEnabledInSettings) return null;
        DecisionAvailability availability = GetAvailability();
        string provider = providerId?.Invoke();
        if (!CanUseDecisionModel(availability, provider))
        {
            return null;
        }
        try
        {
            RimLLMDecisionClient client = RimLLMProvider.CreateDecisionService(ColonyAgentSession.ModId);
            return new JevDecisionService(client, providerId, model);
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

    /// <summary>
    /// 取得某供應商可用的決策模型清單；框架不可用或查詢失敗時回空清單。
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        Func<string> providerId, CancellationToken cancellationToken = default)
    {
        try
        {
            RimLLMDecisionClient client = RimLLMProvider.CreateDecisionService(ColonyAgentSession.ModId);
            return await client.FetchModelsAsync(providerId?.Invoke(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 模型清單只是設定頁的下拉選項；取不到就讓玩家手打。
            return Array.Empty<string>();
        }
    }
}
