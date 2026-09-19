using System.Collections.Generic;
using System.Text;

namespace RimAgent.Agent
{
    /// <summary>
    /// 每句指令的系統提示詞組裝：核心（硬編碼協議）→內建準則→技能。
    /// 純字串邏輯；任一層為空就跳過，不留空標題。
    /// </summary>
    public static class AgentPromptComposer
    {
        /// <summary>技能區的總量上限（字數），超過就省略剩下的並註明。</summary>
        public const int MaxSkillsChars = 8000;

        public static string Compose(string core, string conduct, IReadOnlyList<AgentSkill> skills)
        {
            var sb = new StringBuilder(core ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(conduct))
            {
                sb.Append("\n\nStanding conduct:\n").Append(conduct.Trim());
            }
            AppendSkills(sb, skills);
            return sb.ToString();
        }

        private static void AppendSkills(StringBuilder sb, IReadOnlyList<AgentSkill> skills)
        {
            if (skills == null || skills.Count == 0) return;
            var section = new StringBuilder();
            int omitted = 0;
            foreach (AgentSkill skill in skills)
            {
                if (skill == null || string.IsNullOrWhiteSpace(skill.Body)) continue;
                string header = "## " + skill.Name.Trim();
                if (!string.IsNullOrWhiteSpace(skill.Description))
                {
                    header += " - " + skill.Description.Trim();
                }
                string entry = header + "\n" + skill.Body.Trim() + "\n";
                if (section.Length + entry.Length > MaxSkillsChars)
                {
                    omitted++;
                    continue;
                }
                section.Append(entry);
            }
            if (section.Length == 0) return;
            // 無參數 TrimEnd() 只有 Mono 有；這裡只清行尾空白。
#pragma warning disable S3878 // net472 mscorlib requires char[] overload at runtime to prevent MissingMethodException
            sb.Append("\n\nAvailable skills:\n").Append(section.ToString().TrimEnd(new[] { '\r', '\n', ' ', '\t' }));
#pragma warning restore S3878
            if (omitted > 0) sb.Append("\n[").Append(omitted).Append(" further skill(s) omitted: over budget]");
        }
    }
}
