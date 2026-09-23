using System.Collections.Generic;
using System.Text;

namespace RimAgent.Agent
{
    /// <summary>自主任務指令的語言（跟遊戲介面語言走，而非玩家上一句話）。順序即下表第二維的索引。</summary>
    public enum AutoLanguage
    {
        English,
        Traditional,
        Simplified
    }

    /// <summary>
    /// 自主任務的指令文字：依觸發原因給模型一個明確的當下自主任務，並附上玩家設的長期目標。
    /// 放在程式碼而非語言檔——跟系統提示詞一樣是給模型的，不是給玩家看的 UI；
    /// 三語以觸發×語言的表格存放，不再是三套 switch（單元測試不進遊戲，Translate 在此無效）。
    /// </summary>
    public static class AutonomousPrompts
    {
        // 維度：觸發原因 × 語言（English／Traditional／Simplified）。
        private static readonly Dictionary<AutonomousTrigger, string[]> Tasks = new Dictionary<AutonomousTrigger, string[]>
        {
            { AutonomousTrigger.Raid, new[]
            {
                "Hostiles are on the map. Deploy the defence with tools: draft the colonists who can fight, position them safely and attack the threats. Safe positioning beats chasing. Leave them drafted; a follow-up task fires when the raid ends.",
                "地圖上有敵對威脅。用工具佈署防禦：徵召能作戰的殖民者、走位到安全位置、攻擊威脅。安全走位優於追擊。讓他們保持徵召；襲擊結束時會另起一次任務。",
                "地图上有敌对威胁。用工具部署防御：征召能作战的殖民者、走位到安全位置、攻击威胁。安全走位优于追击。让他们保持征召；袭击结束时会另起一次任务。"
            } },
            { AutonomousTrigger.RaidEnded, new[]
            {
                "The raid has ended. Check threats with tools; when the map is clear, undraft everyone still drafted so they return to food, rest and work.",
                "襲擊已結束。用工具檢查威脅；地圖淨空後，解除所有仍被徵召的殖民者，讓他們回去進食、休息與工作。",
                "袭击已结束。用工具检查威胁；地图净空后，解除所有仍被征召的殖民者，让他们回去进食、休息与工作。"
            } },
            { AutonomousTrigger.EmergencyMedical, new[]
            {
                "A colonist is downed or bleeding. Check their health with tools, make sure their medical care level allows treatment, and take the least disruptive action that gets a doctor to them.",
                "有殖民者倒地或流血。用工具檢查他的健康，確認醫療等級允許治療，並採取最少干擾的行動讓醫生去救治。",
                "有殖民者倒地或流血。用工具检查他的健康，确认医疗等级允许治疗，并采取最少干扰的行动让医生去救治。"
            } },
            { AutonomousTrigger.Fire, new[]
            {
                "Fire is burning on the map. Check threats and the burning area with tools, then use the actions you have to keep colonists out of danger and contain it.",
                "地圖上有火在燒。用工具檢查威脅與起火區域，再用手上的行動讓殖民者遠離危險並控制火勢。",
                "地图上有火在烧。用工具检查威胁与起火区域，再用手上的行动让殖民者远离危险并控制火势。"
            } },
            { AutonomousTrigger.MentalBreak, new[]
            {
                "A colonist is close to a mental break. Check their mood and its causes with tools, then relieve the pressure through schedule, needs, threats or health.",
                "有殖民者接近精神崩潰。用工具查看他的心情與原因，再從作息、需求、威脅或健康著手緩解壓力。",
                "有殖民者接近精神崩溃。用工具查看他的心情与原因，再从作息、需求、威胁或健康着手缓解压力。"
            } },
            { AutonomousTrigger.FoodShortage, new[]
            {
                "Food reserves are low. Check resources with tools, then secure more human-edible nutrition: harvest, hunt, add cooking bills or trade.",
                "糧食儲備不足。用工具查看資源，再補充更多人可食用的營養：收成、打獵、加烹飪帳單或交易。",
                "粮食储备不足。用工具查看资源，再补充更多人可食用的营养：收获、打猎、加烹饪账单或交易。"
            } },
            { AutonomousTrigger.Interval, new[]
            {
                "Routine inspection: check the colony overview, alerts, threats and key resources with tools. Handle anything urgent; when all is well, reply in one sentence.",
                "例行巡檢：用工具查看殖民地概況、警報、威脅與重要資源。有急事就處理；一切正常就用一句話回報。",
                "例行巡检：用工具查看殖民地概况、警报、威胁与重要资源。有急事就处理；一切正常就用一句话回报。"
            } }
        };

        private static readonly string[] GoalHeaders =
        {
            "\n\nThe player's long-term goals:",
            "\n\n玩家設的長期目標：",
            "\n\n玩家设的长期目标："
        };

        private static readonly string[] GoalFooters =
        {
            "\nOnce the task above is handled, spend any remaining steps on these goals.",
            "\n上面的任務處理完後，剩下的步數用來推進這些目標。",
            "\n上面的任务处理完后，剩下的步数用来推进这些目标。"
        };

        public static string Build(AutonomousTrigger trigger, IReadOnlyList<string> goals, AutoLanguage language)
        {
            int lang = LangIndex(language);
            string task;
            string[] texts;
            if (Tasks.TryGetValue(trigger, out texts)) task = texts[lang];
            else task = Tasks[AutonomousTrigger.Interval][lang];
            string goalsText = GoalsText(goals, lang);
            return goalsText.Length == 0 ? task : task + goalsText;
        }

        private static int LangIndex(AutoLanguage language)
        {
            int index = (int)language;
            return index >= 0 && index < GoalHeaders.Length ? index : 0;
        }

        private static string GoalsText(IReadOnlyList<string> goals, int lang)
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
            sb.Append(GoalHeaders[lang]);
            foreach (string line in lines)
            {
                sb.Append('\n').Append(line);
            }
            sb.Append(GoalFooters[lang]);
            return sb.ToString();
        }
    }
}
