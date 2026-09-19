using System;
using System.Collections.Generic;

namespace RimAgent.UI
{
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

    /// <summary>目標指令的三種形式：<c>/goal</c> 列出、<c>/goal &lt;文字&gt;</c> 新增、<c>/goal remove &lt;n&gt;</c> 刪除。</summary>
    public enum GoalAction
    {
        List,
        Add,
        Remove
    }

    /// <summary>
    /// 斜線指令的純字串邏輯：前綴篩選（給彈出選單）與送出時的解析。不碰 Verse，可單元測試。
    /// </summary>
    public static class SlashCommands
    {
        public static readonly SlashCommand Goal = new SlashCommand("goal", true, "RimAgent.Slash.Goal");
        public static readonly SlashCommand Undo = new SlashCommand("undo", false, "RimAgent.Slash.Undo");
        public static readonly SlashCommand Clear = new SlashCommand("clear", false, "RimAgent.Slash.Clear");
        public static readonly SlashCommand Stop = new SlashCommand("stop", false, "RimAgent.Slash.Stop");

        public static readonly IReadOnlyList<SlashCommand> All = new[] { Goal, Undo, Clear, Stop };

        /// <summary>還在打指令名稱（以 / 開頭且尚無空白）：此時顯示彈出選單。</summary>
        public static bool IsTypingName(string input)
        {
            return input != null && input.Length > 0 && input[0] == '/' && input.IndexOf(' ') < 0;
        }

        /// <summary>依目前輸入的前綴篩選指令；非指令狀態回傳空清單。</summary>
        public static List<SlashCommand> Match(string input)
        {
            var matches = new List<SlashCommand>();
            if (!IsTypingName(input)) return matches;
            string prefix = input.Substring(1);
            foreach (SlashCommand command in All)
            {
                if (command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) matches.Add(command);
            }
            return matches;
        }

        /// <summary>
        /// 解析送出的文字。不是斜線指令回傳 false；是斜線但名稱未知時 <paramref name="command"/> 為 null 且回傳 true。
        /// </summary>
        public static bool TryParse(string input, out SlashCommand command, out string args)
        {
            command = null;
            args = string.Empty;
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0 || text[0] != '/') return false;
            int space = text.IndexOf(' ');
            string name = space < 0 ? text.Substring(1) : text.Substring(1, space - 1);
            args = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
            foreach (SlashCommand candidate in All)
            {
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    command = candidate;
                    break;
                }
            }
            return true;
        }

        /// <summary>解析 /goal 的參數；Remove 時 <paramref name="index"/> 為 1 起算的序號（解析失敗為 0）。</summary>
        public static GoalAction ParseGoal(string args, out int index, out string text)
        {
            index = 0;
            text = (args ?? string.Empty).Trim();
            if (text.Length == 0) return GoalAction.List;
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
}
