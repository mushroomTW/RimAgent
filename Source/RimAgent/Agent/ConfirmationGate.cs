using System;
using System.Collections.Generic;
using System.Threading;
using RimAgent.Tools;

namespace RimAgent.Agent
{
    /// <summary>
    /// 「需確認」模式的閘門實作：拥有者是 <see cref="ColonyAgentSession"/>。
    /// 設定頁打開開關時擋下 Work／Build／Combat 命令（工具回傳預覽），
    /// 玩家按確認後放行一次（同一句指令重送，模型會重新下相同的工具呼叫）。
    /// </summary>
    public sealed class ConfirmationGate : ICommandGate
    {
        private readonly Func<bool> _required;
        private readonly object _gate = new object();
        private readonly List<string> _blocked = new List<string>();
        private int _bypass;

        public ConfirmationGate(Func<bool> required)
        {
            _required = required ?? (() => false);
        }

        public bool AllowCommands(ToolCategory category)
        {
            // 只有 Work／Build／Combat 受「需確認」管制，其他類別（含查詢）永遠放行。
            if (category != ToolCategory.Work && category != ToolCategory.Build && category != ToolCategory.Combat)
                return true;
            if (Volatile.Read(ref _bypass) == 1) return true;
            return !_required();
        }

        public void NoteBlocked(string preview)
        {
            lock (_gate)
            {
                _blocked.Add(preview ?? string.Empty);
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
    }
}
