using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Tools;

namespace RimAgent.Agent
{
    /// <summary>
    /// 命令閘門實作，擁有者是 <see cref="ColonyAgentSession"/>。
    /// 依 <see cref="AgentMode"/> 與目前工具的 <see cref="ToolRisk"/> 決定放行或擋下：
    /// 確認模式擋 Confirm 級工具（回傳預覽並捕捉原呼叫，玩家按確認後直接執行原呼叫）；
    /// 自動模式由 <see cref="LoggingAIFunction"/> 先強制預覽、問過分類器再決定執行或拒絕。
    /// 工具的風險由 <see cref="LoggingAIFunction"/> 在每次呼叫前經 <see cref="BeginInvocation"/> 告知；
    /// 同一輪的多個查詢可並行執行，每條非同步流程各有一份呼叫狀態（AsyncLocal），
    /// 共用狀態（待確認、額度、查詢快取）各自上鎖。
    /// </summary>
    public sealed class ConfirmationGate
    {
        private sealed class PendingApproval
        {
            public string ToolName;
            public Func<CancellationToken, Task<object>> ExecuteAsync;
        }

        private sealed class InvocationState
        {
            public string ToolName;
            public ToolRisk Risk;
            public bool? Forced;
        }

        private readonly Func<AgentMode> _mode;
        private readonly object _gate = new object();
        private readonly List<string> _blocked = new List<string>();
        private readonly List<PendingApproval> _pending = new List<PendingApproval>();
        // 玩家按下「不再問」的工具；只活在這場遊戲的記憶體裡，不進存檔。
        private readonly HashSet<string> _sessionApproved = new HashSet<string>(StringComparer.Ordinal);
        // 不在工具呼叫期間（直接建構工具的測試）時以最保守的 Confirm 看待。
        private readonly AsyncLocal<InvocationState> _invocation = new AsyncLocal<InvocationState>();
        // 自動模式兩段式：false＝強制預覽（不記入 blocked）、true＝強制執行、null＝照模式判斷。
        private int _lastApprovalExecutedCount;
        // -1 代表本輪不限制；自主排程器會在送出前設成當日剩餘工具額度。
        private int _remainingToolCalls = -1;
        /// <summary>同一句指令內的查詢結果快取：同樣的唯讀查詢只碰一次世界，之後直接回快取。</summary>
        private readonly InstructionCache<string> _queryCache = new InstructionCache<string>();

        public ConfirmationGate(Func<AgentMode> mode)
        {
            _mode = mode ?? (() => AgentMode.Autonomous);
        }

        public AgentMode Mode => _mode();

        public bool HasPendingApprovals
        {
            get
            {
                lock (_gate) return _pending.Count > 0;
            }
        }

        internal int PendingApprovalCount
        {
            get { lock (_gate) return _pending.Count; }
        }

        internal int LastApprovalExecutedCount
        {
            get { lock (_gate) return _lastApprovalExecutedCount; }
        }

        public bool AllowCommands()
        {
            InvocationState invocation = _invocation.Value;
            if (invocation?.Forced.HasValue == true) return invocation.Forced.Value;
            switch (_mode())
            {
                case AgentMode.Ask:
                    return false;
                case AgentMode.Confirm:
                    return invocation?.Risk == ToolRisk.Query || IsApprovedForSession(invocation?.ToolName);
                default:
                    return true;
            }
        }

        public void NoteBlocked(string preview)
        {
            // 強制預覽那一段只是為了拿預覽文字給分類器看，還沒決定要不要擋，先不記。
            if (_invocation.Value?.Forced == false) return;
            lock (_gate)
            {
                _blocked.Add(preview ?? string.Empty);
            }
        }

        /// <summary>封存預覽過的固定工具呼叫；核准時不會重送自然語句給模型。</summary>
        internal void RegisterPendingApproval(string toolName, Func<CancellationToken, Task<object>> executeAsync)
        {
            if (executeAsync == null) return;
            lock (_gate)
            {
                _pending.Add(new PendingApproval { ToolName = toolName, ExecuteAsync = executeAsync });
            }
        }

        /// <param name="rememberForSession">把這批工具記成「本局不再問」；MCP 工具不記（見 <see cref="CanRememberForSession"/>）。</param>
        internal async Task<int> ApprovePendingAsync(CancellationToken cancellationToken, bool rememberForSession = false)
        {
            lock (_gate) _lastApprovalExecutedCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingApproval approval;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _blocked.Clear();
                        return _lastApprovalExecutedCount;
                    }
                    // 開始執行前移除目前項目；失敗時無法保證它沒有部分生效，因此不可自動重試。
                    approval = _pending[0];
                    _pending.RemoveAt(0);
                }
                await approval.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate) _lastApprovalExecutedCount++;
                // 執行成功才記：中途失敗或取消時，沒真的跑過的工具不該被永久放行。
                if (rememberForSession && CanRememberForSession(approval.ToolName))
                {
                    lock (_gate) _sessionApproved.Add(approval.ToolName);
                }
            }
        }

        /// <summary>這批待確認裡有沒有可以記成「本局不再問」的工具（沒有就不必顯示那顆按鍵）。</summary>
        public bool CanRememberPending
        {
            get
            {
                lock (_gate) return _pending.Any(p => CanRememberForSession(p.ToolName));
            }
        }

        /// <summary>
        /// MCP 以使用者權限執行本機程式，每次的 server／tool／args 都可能不同，
        /// 記住「工具名稱」等於對之後所有外部呼叫放行，因此永遠不記。
        /// YOLO 黑名單上的重大不可逆操作同理：那份清單的意思是「絕不免確認執行」，
        /// 一顆按鍵不該把它整場遊戲關掉。
        /// </summary>
        private static bool CanRememberForSession(string toolName)
        {
            return !string.IsNullOrEmpty(toolName)
                && !string.Equals(toolName, "mcp_call_tool", StringComparison.Ordinal)
                && !AgentToolRegistry.IsYoloForbidden(toolName);
        }

        internal bool IsApprovedForSession(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            lock (_gate) return _sessionApproved.Contains(toolName);
        }

        /// <summary>清掉所有「本局不再問」（對話重來時一併歸零）。</summary>
        public void ClearSessionApprovals()
        {
            lock (_gate) _sessionApproved.Clear();
        }

        internal void DiscardPendingApprovals()
        {
            lock (_gate)
            {
                _pending.Clear();
                _blocked.Clear();
            }
        }

        internal void BeginToolBudget(int? maximumCalls)
        {
            lock (_gate)
            {
                _remainingToolCalls = maximumCalls.HasValue ? Math.Max(0, maximumCalls.Value) : -1;
                // 新的一句指令：查詢快取與分類器快取（由呼叫端另行清空）都不跨指令。
                _queryCache.Clear();
            }
        }

        internal void EndToolBudget()
        {
            lock (_gate)
            {
                _remainingToolCalls = -1;
            }
        }

        /// <summary>在工具碰觸世界前原子扣除一次額度；無預算限制的玩家指令不計入自主配額。</summary>
        internal bool TryConsumeToolBudget(out bool consumed)
        {
            lock (_gate)
            {
                if (_remainingToolCalls < 0)
                {
                    consumed = false;
                    return true;
                }
                if (_remainingToolCalls == 0)
                {
                    consumed = false;
                    return false;
                }
                _remainingToolCalls--;
                consumed = true;
                return true;
            }
        }

        /// <summary>拿走並清空本輪被擋下的預覽（送完一句指令時呼叫一次）。</summary>
        public IReadOnlyList<string> TakeBlocked()
        {
            lock (_gate)
            {
                if (_blocked.Count == 0) return Array.Empty<string>();
                var taken = _blocked.ToArray();
                _blocked.Clear();
                return taken;
            }
        }

        /// <summary>同一句內重複的唯讀查詢是否已有快取；有則呼叫端直接回傳，不碰世界也不扣額度。</summary>
        internal bool TryGetCachedQuery(string toolName, string arguments, out string result)
        {
            return _queryCache.TryGet(QueryCacheKey(toolName, arguments), out result);
        }

        /// <summary>
        /// 記下一次成功的查詢結果：錯誤與確認預覽不記（世界可能在同句後續命令中改變，
        /// 下一次重查才看得到最新狀態）；超過上限就不再記，避免無限成長。
        /// </summary>
        internal void StoreCachedQuery(string toolName, string arguments, string result)
        {
            if (string.IsNullOrEmpty(result)) return;
            _queryCache.Store(QueryCacheKey(toolName, arguments), result);
        }

        /// <summary>
        /// 同一句內執行了命令（成功、失敗或部分生效皆可能改變世界）：丟掉已記的查詢結果，
        /// 之後重查才看得到最新狀態。預覽不碰世界，不必失效。
        /// </summary>
        /// <summary>對話重來或新局時清空查詢快取（平時由每句的額度計數一併清空）。</summary>
        internal void InvalidateQueryCache()
        {
            _queryCache.Clear();
        }

        private static string QueryCacheKey(string toolName, string arguments)
        {
            return (toolName ?? string.Empty) + "\n" + (arguments ?? string.Empty);
        }

        /// <summary>特定高敏感工具（如本機 MCP 工具）是否需要強制手動確認：僅在確認模式下需強制手動確認。</summary>
        internal bool RequiresExplicitConfirmation(string toolName)
        {
            return string.Equals(toolName, "mcp_call_tool", StringComparison.Ordinal)
                && _mode() == AgentMode.Confirm;
        }

        /// <summary>這次工具呼叫是否要先問分類器：自動模式且是命令，且玩家沒說過這個工具不必再問。</summary>
        internal bool NeedsClassification(ToolRisk risk)
        {
            return _mode() == AgentMode.Auto
                && risk != ToolRisk.Query;
        }

        /// <summary>工具呼叫開始：記下這次工具的名稱與靜態風險。</summary>
        internal void BeginInvocation(string toolName, ToolRisk risk)
        {
            _invocation.Value = new InvocationState { ToolName = toolName, Risk = risk };
        }

        /// <summary>工具呼叫結束：回到保守預設。</summary>
        internal void EndInvocation()
        {
            _invocation.Value = null;
        }

        internal void ForcePreview()
        {
            if (_invocation.Value != null) _invocation.Value.Forced = false;
        }

        internal void ForceExecute()
        {
            if (_invocation.Value != null) _invocation.Value.Forced = true;
        }

        internal void ClearForce()
        {
            if (_invocation.Value != null) _invocation.Value.Forced = null;
        }
    }
}
