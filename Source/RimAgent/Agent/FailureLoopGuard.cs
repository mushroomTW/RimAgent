using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Tools;

namespace RimAgent.Agent;

/// <summary>
/// 一句指令裡，主 Agent 的同一個工具一再回同樣的錯誤時插手。參照 TokenEconomizer 的 loop guard：
/// 同樣的失敗達 <see cref="Threshold"/> 次時請判斷模型（System One）確認是否卡住（每種失敗只問一次，判斷要等一趟網路來回），
/// 信心達 <see cref="JudgeConfidence"/> 才在工具結果後附一次轉向提示（每句最多一次）；判斷模型不可用時直接轉向。轉向後仍卡住只提醒玩家，不替玩家停止。
/// 與 <see cref="ConfirmationGate"/> 擋「同一組引數重送」互補：這裡看的是換了引數、錯誤卻一樣。
/// </summary>
public sealed class FailureLoopGuard
{
    public const int Threshold = 3;
    public const double JudgeConfidence = 0.7;
    private const int RecentFailures = 6;

    private readonly Func<string, CancellationToken, Task<double?>>? _judge;
    private readonly Action<string> _notifyPlayer;
    private readonly System.Threading.Lock _lock = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<string> _recent = [];
    private string _instruction = string.Empty;
    private string? _redirectedKey;
    private int _redirectedAt;
    private bool _notified;

    public FailureLoopGuard(Func<string, CancellationToken, Task<double?>>? judge, Action<string> notifyPlayer)
    {
        _judge = judge;
        _notifyPlayer = notifyPlayer ?? throw new ArgumentNullException(nameof(notifyPlayer));
    }

    public void Reset(string instruction)
    {
        lock (_lock)
        {
            _counts.Clear();
            _recent.Clear();
            _instruction = instruction ?? string.Empty;
            _redirectedKey = null;
            _redirectedAt = 0;
            _notified = false;
        }
    }

    public async Task<string?> ObserveAsync(string tool, string? result, CancellationToken cancellationToken)
    {
        if (result == null || !result.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal)) return null;
        string key = tool + "\n" + ToolUsageStats.Signature(result);
        int count;
        string state;
        lock (_lock)
        {
            _counts[key] = count = _counts.GetValueOrDefault(key) + 1;
            _recent.Add(tool + " -> " + ToolUsageStats.Signature(result));
            if (_recent.Count > RecentFailures) _recent.RemoveAt(0);
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
            state = "Instruction: " + _instruction + "\nRecent failed tool calls, oldest first:\n" + string.Join("\n", _recent);
        }

        double? stuck = _judge == null ? null : await _judge(state, cancellationToken).ConfigureAwait(false);
        if (stuck.HasValue && stuck.Value < JudgeConfidence) return null;
        lock (_lock)
        {
            if (_redirectedKey != null) return null;
            _redirectedKey = key;
            _redirectedAt = count;
        }
        return "\n\n<loop_guard>\n" + tool + " has now failed " + count + " times in this instruction with the same error. " +
               "Repeating it will not help. Change approach: check your assumptions with an observation tool, use a different tool, " +
               "or stop and tell the player what is blocking you.\n</loop_guard>";
    }
}
