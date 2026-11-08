using System.Collections.Generic;
using System.Text;

namespace RimAgent.Agent;

/// <summary>
/// System One 自己動手做過的事，留給主 LLM 下一句指令開頭一次讀完，免得它對殖民地的認知跟實際脫節。
/// 只收改變世界的操作；分流、預載工具包、自主任務篩選這類只影響輸入的判斷不放進來。
/// 遊戲主線程寫入、會話送指令時讀取，所以上鎖。
/// </summary>
public sealed class DecisionReports
{
    /// <summary>最多留幾則；太久沒有指令時只留最新的。</summary>
    public const int MaxPending = 20;

    private readonly object _gate = new();
    private readonly List<string> _pending = [];

    public void Add(string report)
    {
        if (string.IsNullOrWhiteSpace(report)) return;
        lock (_gate)
        {
            _pending.Add(report.Trim());
            if (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }
    }

    /// <summary>取走全部待送的回報；沒有則回傳 null。</summary>
    public string TakeAll()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (string report in _pending) sb.Append("- ").AppendLine(report);
            _pending.Clear();
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>把回報接在指令前面送給主 LLM；沒有回報時原樣回傳。</summary>
    public static string Prepend(string reports, string text)
    {
        if (string.IsNullOrEmpty(reports)) return text;
        return "<system_one_actions>\nSince your last instruction, the fast decision model (System One) took these actions on its own. " +
               "They are already done in the game; take them into account.\n" + reports + "\n</system_one_actions>\n\n" + text;
    }
}
