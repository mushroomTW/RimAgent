using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Tools;

namespace RimAgent.Agent
{
    /// <summary>
    /// 命令閘門實作，擁有者是 <see cref="ColonyAgentSession"/>。
    /// 依 <see cref="AgentMode"/> 與目前工具的 <see cref="ToolRisk"/> 決定放行或擋下：
    /// 確認模式擋 Confirm 級工具（回傳預覽），玩家按確認後放行一次（同一句重送）；
    /// 自動模式由 <see cref="LoggingAIFunction"/> 先強制預覽、問過分類器再強制執行。
    /// 工具的風險由 <see cref="LoggingAIFunction"/> 在每次呼叫前經 <see cref="BeginInvocation"/> 告知；
    /// 工具串行執行（框架禁止並行），所以這裡用單一欄位即可。
    /// </summary>
    public sealed class ConfirmationGate : ICommandGate
    {
        private sealed class PendingApproval
        {
            public Func<CancellationToken, Task<object>> ExecuteAsync;
        }

        private readonly Func<AgentMode> _mode;
        private readonly object _gate = new object();
        private readonly List<string> _blocked = new List<string>();
        private readonly List<PendingApproval> _pending = new List<PendingApproval>();
        private int _bypass;
        // 不在工具呼叫期間（直接建構工具的測試）時以最保守的 Confirm 看待。
        private ToolRisk _currentRisk = ToolRisk.Confirm;
        // 自動模式兩段式：false＝強制預覽（不記入 blocked）、true＝強制執行、null＝照模式判斷。
        private bool? _forced;
        // -1 代表本輪不限制；自主排程器會在送出前設成當日剩餘工具額度。
        private int _remainingToolCalls = -1;

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

        public bool AllowCommands()
        {
            if (_forced.HasValue) return _forced.Value;
            if (IsBypassing) return true;
            switch (_mode())
            {
                case AgentMode.Ask:
                    return false;
                case AgentMode.Confirm:
                    return _currentRisk != ToolRisk.Confirm;
                default:
                    return true;
            }
        }

        public void NoteBlocked(string preview)
        {
            // 強制預覽那一段只是為了拿預覽文字給分類器看，還沒決定要不要擋，先不記。
            if (_forced == false) return;
            lock (_gate)
            {
                _blocked.Add(preview ?? string.Empty);
            }
        }

        /// <summary>封存預覽過的固定工具呼叫；核准時不會重送自然語句給模型。</summary>
        internal void RegisterPendingApproval(Func<CancellationToken, Task<object>> executeAsync)
        {
            if (executeAsync == null) return;
            lock (_gate)
            {
                _pending.Add(new PendingApproval { ExecuteAsync = executeAsync });
            }
        }

        internal async Task<int> ApprovePendingAsync(CancellationToken cancellationToken)
        {
            PendingApproval[] approvals;
            lock (_gate)
            {
                approvals = _pending.ToArray();
                _pending.Clear();
                _blocked.Clear();
            }

            int executed = 0;
            foreach (PendingApproval approval in approvals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await approval.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                executed++;
            }
            return executed;
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

        /// <summary>放行接下來的一次執行（確認鍵用），跑完後 session 會關掉。</summary>
        public void BypassOnce()
        {
            Volatile.Write(ref _bypass, 1);
        }

        public void ResetBypass()
        {
            Volatile.Write(ref _bypass, 0);
        }

        internal bool IsBypassing => Volatile.Read(ref _bypass) == 1;

        /// <summary>這次工具呼叫是否要先問分類器：自動模式、是命令、且不在確認後的放行重送中。</summary>
        internal bool NeedsClassification(ToolRisk risk)
        {
            return _mode() == AgentMode.Auto && risk != ToolRisk.Query && !IsBypassing;
        }

        /// <summary>工具呼叫開始：記下這次工具的靜態風險。</summary>
        internal void BeginInvocation(ToolRisk risk)
        {
            _currentRisk = risk;
            _forced = null;
        }

        /// <summary>工具呼叫結束：回到保守預設。</summary>
        internal void EndInvocation()
        {
            _currentRisk = ToolRisk.Confirm;
            _forced = null;
        }

        internal void ForcePreview()
        {
            _forced = false;
        }

        internal void ForceExecute()
        {
            _forced = true;
        }

        internal void ClearForce()
        {
            _forced = null;
        }
    }
}
