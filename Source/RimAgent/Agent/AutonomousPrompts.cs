using System.Collections.Generic;
using System.Text;

namespace RimAgent.Agent
{
    /// <summary>
    /// 自主任務的指令文字：依觸發原因給模型一個明確的當下自主任務，並附上玩家設的長期目標。
    /// 文案住在 AgentPack/Autonomous.en.md（`## 觸發原因` 章節＋目標前後框，只留英文），
    /// 這裡只剩組裝；單元測試不進遊戲，Translate 在此無效，所以走檔案而非語言檔。
    /// 任務找不到（缺檔或缺章節）回傳 null：呼叫端跳過本輪，而不是送一句空話燒 API。
    /// </summary>
    public static class AutonomousPrompts
    {
        public static string Build(AutonomousPack pack, AutonomousTrigger trigger, IReadOnlyList<string> goals)
        {
            if (pack == null) return null;
            string task;
            if (pack.Tasks == null || !pack.Tasks.TryGetValue(trigger.ToString(), out task) || string.IsNullOrWhiteSpace(task))
            {
                if (pack.Tasks == null || !pack.Tasks.TryGetValue(AutonomousTrigger.Interval.ToString(), out task) || string.IsNullOrWhiteSpace(task)) return null;
            }
            string goalsText = GoalsText(goals, pack.GoalsHeader, pack.GoalsFooter);
            return goalsText.Length == 0 ? task : task + goalsText;
        }

        /// <summary>該觸發有無專屬章節（呼叫端藉此判斷是否退回 Interval，退回時警告一次讓拼字錯誤可見）。</summary>
        public static bool HasTask(AutonomousPack pack, AutonomousTrigger trigger)
        {
            if (pack?.Tasks == null) return false;
            string task;
            return pack.Tasks.TryGetValue(trigger.ToString(), out task) && !string.IsNullOrWhiteSpace(task);
        }

        private static string GoalsText(IReadOnlyList<string> goals, string header, string footer)
        {
            if (goals == null) return string.Empty;
            var lines = new List<string>();
            foreach (string g in goals)
            {
                string trimmed = (g ?? string.Empty).Trim();
                if (trimmed.Length > 0) lines.Add("- " + trimmed);
            }
            if (lines.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) sb.Append("\n\n").Append(header.Trim());
            foreach (string line in lines)
            {
                sb.Append('\n').Append(line);
            }
            if (!string.IsNullOrWhiteSpace(footer)) sb.Append('\n').Append(footer.Trim());
            return sb.ToString();
        }
    }
}
