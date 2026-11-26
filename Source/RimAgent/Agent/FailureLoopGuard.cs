using System;
using System.Collections.Generic;
using System.Globalization;
using RimAgent.Tools;

namespace RimAgent.Agent;

/// <summary>
/// 一句指令裡，主 Agent 的同一個工具一再回同樣的錯誤時插手。參照 TokenEconomizer 的 loop guard：
/// 同樣的失敗達 <see cref="Threshold"/> 次就在工具結果後附一次轉向提示（每句最多一次）；轉向後仍卡住只提醒玩家，不替玩家停止。
/// 與 <see cref="ConfirmationGate"/> 擋「同一組引數重送」互補：這裡看的是換了引數、錯誤卻一樣。
/// </summary>
public sealed class FailureLoopGuard
{
    public const int Threshold = 3;

    private readonly Action<string> _notifyPlayer;
    private readonly System.Threading.Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private string? _redirectedKey;
    private int _redirectedAt;
    private bool _notified;

    public FailureLoopGuard(Action<string> notifyPlayer)
    {
        _notifyPlayer = notifyPlayer ?? throw new ArgumentNullException(nameof(notifyPlayer));
    }

    public void Reset()
    {
        lock (_lock)
        {
            _counts.Clear();
            _redirectedKey = null;
            _redirectedAt = 0;
            _notified = false;
        }
    }

    public string? Observe(string tool, string? result)
    {
        if (result == null || !result.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal)) return null;
        string key = tool + "\n" + ToolUsageStats.Signature(result);
        lock (_lock)
        {
            int count = _counts[key] = _counts.GetValueOrDefault(key) + 1;
            if (_redirectedKey != null)
            {
                if (!_notified && key == _redirectedKey && count >= _redirectedAt + Threshold)
                {
                    _notified = true;
                    _notifyPlayer("RimAgent keeps getting the same error from " + tool + " even after being told to change approach. Press Stop if it does not recover.");
                }
                return null;
            }
            if (count != Threshold) return null;
            _redirectedKey = key;
            _redirectedAt = count;
            return "\n\n<loop_guard>\n" + tool + " has now failed " + count.ToString(CultureInfo.InvariantCulture) + " times in this instruction with the same error. " +
                   "Repeating it will not help. Change approach: check your assumptions with an observation tool, use a different tool, " +
                   "or stop and tell the player what is blocking you.\n</loop_guard>";
        }
    }
}
