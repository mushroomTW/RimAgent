using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using RimAgent.Agent;

namespace RimAgent.Tools
{
    /// <summary>
    /// 技能全文按需讀取：系統提示詞只帶技能索引（名稱＋描述），模型覺得相關時再用此工具讀全文，
    /// 免得每輪工具迴圈都重送數千字的技能內容。
    /// </summary>
    public sealed class SkillTools
    {
        private readonly Func<IReadOnlyList<AgentSkill>> _skills;

        public SkillTools(Func<IReadOnlyList<AgentSkill>> skills)
        {
            _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        }

        [AgentTool("read_skill", ToolCategory.Observe, ToolRisk.Query)]
        [Description("Reads the full text of one available skill by name. The system prompt lists only skill names and descriptions; call this when a skill looks relevant to the current instruction.")]
        public string ReadSkill([Description("Skill name exactly as listed in the skills index.")] string skill)
        {
            IReadOnlyList<AgentSkill> skills = _skills() ?? Array.Empty<AgentSkill>();
            string wanted = (skill ?? string.Empty).Trim();
            AgentSkill match = skills.FirstOrDefault(s =>
                s != null && string.Equals((s.Name ?? string.Empty).Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                string names = string.Join(", ", skills
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                    .Select(s => "'" + s.Name.Trim() + "'"));
                return ToolText.Error(string.IsNullOrWhiteSpace(names)
                    ? "no skills are installed."
                    : "unknown skill '" + wanted + "'. Available: " + names + ".");
            }
            if (string.IsNullOrWhiteSpace(match.Body))
                return ToolText.Error("skill '" + match.Name.Trim() + "' is empty.");
            return "## " + match.Name.Trim() + "\n" + match.Body.Trim();
        }
    }
}
