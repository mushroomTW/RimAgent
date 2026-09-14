using System.Collections.Generic;
using System.Text;

namespace RimAgent.Agent
{
    /// <summary>自主任務指令的語言（跟遊戲介面語言走，而非玩家上一句話）。</summary>
    public enum AutoLanguage
    {
        English,
        Traditional,
        Simplified
    }

    /// <summary>
    /// 自主任務的指令文字：依觸發原因給模型一個明確的當下自主任務，並附上玩家設的長期目標。
    /// 放在程式碼而非語言檔——跟系統提示詞一樣是給模型的，不是給玩家看的 UI。
    /// </summary>
    public static class AutonomousPrompts
    {
        public static string Build(AutonomousTrigger trigger, IReadOnlyList<string> goals, AutoLanguage language)
        {
            string task;
            switch (language)
            {
                case AutoLanguage.Traditional:
                    task = TaskTraditional(trigger);
                    break;
                case AutoLanguage.Simplified:
                    task = TaskSimplified(trigger);
                    break;
                default:
                    task = TaskEnglish(trigger);
                    break;
            }
            string goalsText = GoalsText(goals, language);
            return goalsText.Length == 0 ? task : task + goalsText;
        }

        private static string GoalsText(IReadOnlyList<string> goals, AutoLanguage language)
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
            switch (language)
            {
                case AutoLanguage.Traditional:
                    sb.Append("\n\n玩家設的長期目標：");
                    break;
                case AutoLanguage.Simplified:
                    sb.Append("\n\n玩家设的长期目标：");
                    break;
                default:
                    sb.Append("\n\nThe player's long-term goals:");
                    break;
            }
            foreach (string line in lines)
            {
                sb.Append('\n').Append(line);
            }
            switch (language)
            {
                case AutoLanguage.Traditional:
                    sb.Append("\n處理完當下自主任務後，若有餘力就順手推進這些目標。");
                    break;
                case AutoLanguage.Simplified:
                    sb.Append("\n处理完当下自主任务后，若有余力就顺手推进这些目标。");
                    break;
                default:
                    sb.Append("\nAfter the urgent task, work toward these goals if you have steps left.");
                    break;
            }
            return sb.ToString();
        }

        private static string TaskEnglish(AutonomousTrigger trigger)
        {
            switch (trigger)
            {
                case AutonomousTrigger.Raid:
                    return "Hostiles are on the map. Deploy the defense with tools: draft colonists, position them safely and attack the threats, then undraft everyone when it is over. Favor safe positioning over chasing.";
                case AutonomousTrigger.MentalBreak:
                    return "A colonist is close to a mental break. Check moods and the causes with tools, and relieve the pressure (schedule, needs, threats, health).";
                case AutonomousTrigger.FoodShortage:
                    return "Food reserves are low. Check resources with tools and secure more human-edible nutrition (harvest, hunt, trade, production bills).";
                default:
                    return "Routine inspection: check the colony overview, active threats and key resources with tools. Handle anything urgent; if all is well, reply briefly.";
            }
        }

        private static string TaskTraditional(AutonomousTrigger trigger)
        {
            switch (trigger)
            {
                case AutonomousTrigger.Raid:
                    return "地圖上有敵對威脅。用工具佈署防禦：徵召殖民者、走位到安全位置、攻擊威脅，結束後記得全體解除徵召。以安全走位為優先，不要追擊。";
                case AutonomousTrigger.MentalBreak:
                    return "有殖民者接近精神崩潰。用工具查看心情與原因，並緩解壓力（作息、需求、威脅、健康）。";
                case AutonomousTrigger.FoodShortage:
                    return "糧食儲備不足。用工具查看資源，並補充更多人可食用的營養（收成、打獵、交易、生產帳單）。";
                default:
                    return "例行巡檢：用工具查看殖民地概況、威脅與重要資源。有急事就處理；一切正常就簡短回報。";
            }
        }

        private static string TaskSimplified(AutonomousTrigger trigger)
        {
            switch (trigger)
            {
                case AutonomousTrigger.Raid:
                    return "地图上有敌对威胁。用工具部署防御：征召殖民者、走位到安全位置、攻击威胁，结束后记得全体解除征召。以安全走位为优先，不要追击。";
                case AutonomousTrigger.MentalBreak:
                    return "有殖民者接近精神崩溃。用工具查看心情与原因，并缓解压力（作息、需求、威胁、健康）。";
                case AutonomousTrigger.FoodShortage:
                    return "粮食储备不足。用工具查看资源，并补充更多人可食用的营养（收获、打猎、交易、生产账单）。";
                default:
                    return "例行巡检：用工具查看殖民地概况、威胁与重要资源。有急事就处理；一切正常就简短回报。";
            }
        }
    }
}
