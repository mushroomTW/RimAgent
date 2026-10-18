using System;
using System.Collections.Generic;
using System.Linq;

namespace RimAgent.UI;

/// <summary>輸入列的斜線指令定義：名稱、是否接參數、說明的翻譯鍵。</summary>
public sealed class SlashCommand
{
    public string Name { get; }
    public bool TakesArgs { get; }
    public string DescriptionKey { get; }

    public SlashCommand(string name, bool takesArgs, string descriptionKey)
    {
        Name = name;
        TakesArgs = takesArgs;
        DescriptionKey = descriptionKey;
    }
}

/// <summary>目標指令：列出、新增、修改、刪除、清空、暫停與恢復。</summary>
public enum GoalAction
{
    List,
    Add,
    Remove,
    Edit,
    Show,
    Clear,
    Pause,
    Resume
}

/// <summary>
/// 斜線指令的純字串邏輯：前綴篩選（給彈出選單）與送出時的解析。不碰 Verse，可單元測試。
/// </summary>
public static class SlashCommands
{
    public static readonly SlashCommand Goal = new("goal", true, "RimAgent.Slash.Goal");
    public static readonly SlashCommand Undo = new("undo", false, "RimAgent.Slash.Undo");
    public static readonly SlashCommand Clear = new("clear", false, "RimAgent.Slash.Clear");
    public static readonly SlashCommand Compact = new("compact", false, "RimAgent.Slash.Compact");
    public static readonly SlashCommand Stop = new("stop", false, "RimAgent.Slash.Stop");

    public static readonly IReadOnlyList<SlashCommand> All = new[] { Goal, Undo, Clear, Compact, Stop };

    /// <summary>還在打指令名稱（以 / 開頭且尚無空白）：此時顯示彈出選單。</summary>
    public static bool IsTypingName(string input)
    {
        return input != null && input.Length > 0 && input[0] == '/' && input.IndexOf(' ') < 0;
    }

    /// <summary>依目前輸入的前綴篩選指令；非指令狀態回傳空清單。</summary>
    public static List<SlashCommand> Match(string input)
    {
        if (!IsTypingName(input)) return [];
        string prefix = input.Substring(1);
        return [.. All.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// 解析送出的文字。不是斜線指令回傳 false；是斜線但名稱未知時 <paramref name="command"/> 為 null 且回傳 true。
    /// </summary>
    public static bool TryParse(string input, out SlashCommand command, out string args)
    {
        args = string.Empty;
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text[0] != '/')
        {
            command = null;
            return false;
        }
        int space = text.IndexOf(' ');
        string name = space < 0 ? text.Substring(1) : text.Substring(1, space - 1);
        args = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
        command = All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>解析 /goal 的參數；Remove 時 <paramref name="index"/> 為 1 起算的序號（解析失敗為 0）。</summary>
    public static GoalAction ParseGoal(string args, out int index, out string text)
    {
        index = 0;
        text = (args ?? string.Empty).Trim();
        if (text.Length == 0) return GoalAction.List;
        if (string.Equals(text, "pause", StringComparison.OrdinalIgnoreCase)) return GoalAction.Pause;
        if (string.Equals(text, "resume", StringComparison.OrdinalIgnoreCase)) return GoalAction.Resume;
        if (string.Equals(text, "clear", StringComparison.OrdinalIgnoreCase)) return GoalAction.Clear;
        if (string.Equals(text, "show", StringComparison.OrdinalIgnoreCase)) return GoalAction.Show;
        if (text.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(text.Substring(5).Trim(), out index);
            return GoalAction.Show;
        }
        if (string.Equals(text, "edit", StringComparison.OrdinalIgnoreCase)) return GoalAction.Edit;
        if (text.StartsWith("edit ", StringComparison.OrdinalIgnoreCase))
        {
            string rest = text.Substring(5).Trim();
            int separator = rest.IndexOf(' ');
            int.TryParse(separator < 0 ? rest : rest.Substring(0, separator), out index);
            text = separator < 0 ? string.Empty : rest.Substring(separator + 1).Trim();
            return GoalAction.Edit;
        }
        // 「remove」沒帶序號也算刪除（index 留 0，UI 回報沒有這個序號），不能誤加成目標。
        if (string.Equals(text, "remove", StringComparison.OrdinalIgnoreCase)) return GoalAction.Remove;
        if (text.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
        {
            int.TryParse(text.Substring(7).Trim(), out index);
            return GoalAction.Remove;
        }
        return GoalAction.Add;
    }
}
