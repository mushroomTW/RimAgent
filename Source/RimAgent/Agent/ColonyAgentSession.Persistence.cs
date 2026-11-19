using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

public sealed partial class ColonyAgentSession
{
    /// <summary>
    /// 供 GameComponent 在存檔時保存 AF 的模型歷史與工具呼叫內容。
    /// 讀檔後還沒送過指令時 AF session 尚未重建，原樣帶回讀進來的內容；
    /// 其餘情況用上一句指令結束時備妥的快照（忙碌中存檔也拿得到上一句為止的歷史）。
    /// </summary>
    public string SerializeForSave()
    {
        if (_session == null) return _serializedSession;
        string snapshot = Volatile.Read(ref _saveSnapshot);
        if (snapshot != null || IsBusy) return snapshot;
        // 快照失敗過（見 PrepareSaveSnapshot）才走到這裡：存檔當下同步序列化一次。
        try
        {
            return SerializeSession(_session);
        }
        catch (Exception ex)
        {
            // 存檔不能因框架升級或供應商資料不相容而失敗；保留 transcript 作為兜底。
            Verse.Log.Warning("RimAgent: failed to serialize agent session for save: " + ex.Message);
            return null;
        }
    }

    public void RestoreSerializedSession(string serialized)
    {
        if (IsBusy) throw new InvalidOperationException("Cannot restore while RimAgent is working.");
        _serializedSession = string.IsNullOrWhiteSpace(serialized) ? null : serialized;
        // 讀進來的內容先當快照：讀檔後第一句還在跑時存檔，也不會漏掉舊歷史。
        _saveSnapshot = _serializedSession;
        _session = null;
        _registry.Outputs.Clear();
        UnloadToolPacks();
    }

    private string SerializeSession(AgentSession session)
    {
        JsonElement document = _agent.SerializeSessionAsync(session, null, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        return document.GetRawText();
    }

    /// <summary>
    /// 指令或 /compact 結束、仍持有忙碌旗標時呼叫（通常在背景續行上，不佔主線程）：
    /// 裁掉超量的舊歷史、丟掉壓縮器的分組快取，再把 session 序列化成存檔快照。
    /// 分組快取裡是另一份完整歷史，存進檔案只會讓存檔翻倍；下次請求時壓縮器會從歷史重建。
    /// </summary>
    private void PrepareSaveSnapshot()
    {
        AgentSession session = _session;
        if (session == null) return;
        try
        {
            TrimStoredHistory(session);
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
            foreach (string key in _compaction.StateKeys) session.StateBag.TryRemoveValue(key);
#pragma warning restore MAAI001
            Volatile.Write(ref _saveSnapshot, SerializeSession(session));
        }
        catch (Exception ex)
        {
            // 快照失敗不影響這句指令；清掉舊快照，存檔時改走同步序列化。
            // 裁歷史失敗時同步序列化可能照樣成功，所以這裡自己記一次，免得存量上限失效卻沒人知道。
            Volatile.Write(ref _saveSnapshot, null);
            if (_snapshotWarningShown) return;
            _snapshotWarningShown = true;
            Verse.Log.Warning("RimAgent: failed to prepare agent session save snapshot: " + ex.Message);
        }
    }

    /// <summary>
    /// 自動壓縮開啟時的存量上限：請求端的折疊只縮送出的內容（且摘要仍含工具輸出全文），
    /// session 內存的歷史不會變少，會隨遊戲無限成長並拖慢存讀檔。
    /// 超過門檻兩倍時，從最舊的完整一輪往後裁到門檻以內；只在玩家訊息處下刀，
    /// 工具呼叫與結果不會被拆開，也不會留下開頭不是玩家訊息的歷史（部分供應商會拒收）。
    /// 最近一輪本身就超過門檻時只留最近一輪。
    /// 不用 <c>InMemoryChatHistoryProviderOptions.ChatReducer</c>：MEAI 現成的 <c>MessageCountingChatReducer</c>
    /// 會丟掉函式呼叫與結果、也不保證從玩家訊息開頭；reducer 又是建構 agent 時固定的，
    /// 無法隨「自動壓縮」設定即時開關。所以在存檔快照前自己裁。
    /// </summary>
    private void TrimStoredHistory(AgentSession session)
    {
        if (!AutoCompactEnabled) return;
        if (_agent.ChatHistoryProvider is not InMemoryChatHistoryProvider provider) return;
        List<ChatMessage> messages = provider.GetMessages(session);
        int threshold = StoredHistoryMessageLimit;
        if (messages.Count <= threshold * 2) return;

        int start = -1;
        for (int i = 1; i < messages.Count; i++)
        {
            if (messages[i].Role != ChatRole.User) continue;
            start = i;
            if (messages.Count - i <= threshold) break;
        }
        if (start <= 0) return;
        provider.SetMessages(session, messages.GetRange(start, messages.Count - start));
    }

    private async Task<AgentSession> RestoreOrCreateSession(CancellationToken cancellationToken)
    {
        string serialized = _serializedSession;
        _serializedSession = null;
        if (!string.IsNullOrWhiteSpace(serialized))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(serialized);
                return await _agent.DeserializeSessionAsync(document.RootElement, null, cancellationToken);
            }
            catch (Exception ex)
            {
                // 舊版存檔或 Agent Framework 升級後無法還原時，安全地退回新會話。
                Verse.Log.Warning("RimAgent: failed to restore agent session, starting fresh: " + ex.Message);
            }
        }
        return await _agent.CreateSessionAsync(cancellationToken);
    }
}
