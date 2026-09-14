using System;
using System.Collections.Generic;
using System.IO;

namespace RimAgent.Agent
{
    /// <summary>一個技能包（Skills/ 下的一個資料夾，以 SKILL.md 為本體）。</summary>
    public sealed class AgentSkill
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }

    /// <summary>出貨的 agent 擴充包內容：內建行為準則＋技能包（玩家常駐指令另存存檔，不在這裡）。</summary>
    public sealed class AgentPack
    {
        public string Conduct { get; set; } = string.Empty;
        public IReadOnlyList<AgentSkill> Skills { get; set; } = Array.Empty<AgentSkill>();
    }

    /// <summary>
    /// 讀 AgentPack/ 資料夾（AGENTS.md＋Skills/*/SKILL.md）。純檔案邏輯，不碰遊戲 API，
    /// rootDir 可注入，方便測試。缺檔、壞檔一律視為空，不丟例外。
    /// </summary>
    public static class AgentPackLoader
    {
        /// <summary>單個技能讀進提示詞的上限（字數），超過截斷並註明。</summary>
        public const int MaxSkillChars = 3000;

        public static AgentPack Load(string rootDir)
        {
            var pack = new AgentPack();
            if (string.IsNullOrEmpty(rootDir)) return pack;
            try
            {
                string conductPath = Path.Combine(rootDir, "AGENTS.md");
                if (File.Exists(conductPath))
                {
                    pack.Conduct = (File.ReadAllText(conductPath) ?? string.Empty).Trim();
                }

                string skillsDir = Path.Combine(rootDir, "Skills");
                if (!Directory.Exists(skillsDir)) return pack;
                var skills = new List<AgentSkill>();
                string[] dirs = Directory.GetDirectories(skillsDir);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (string dir in dirs)
                {
                    string skillPath = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillPath)) continue;
                    string text = (File.ReadAllText(skillPath) ?? string.Empty).Trim();
                    if (text.Length == 0) continue;
                    skills.Add(ParseSkill(Path.GetFileName(dir) ?? dir, text));
                }
                pack.Skills = skills;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 檔案被鎖或無權限：當作沒有擴充包，遊戲照跑。
            }
            return pack;
        }

        internal static AgentSkill ParseSkill(string folderName, string text)
        {
            string name = folderName;
            string description = string.Empty;
            string body = text;
            if (text.StartsWith("---"))
            {
                int end = text.IndexOf("---", 3, StringComparison.Ordinal);
                if (end > 3)
                {
                    string front = text.Substring(3, end - 3);
                    body = text.Substring(end + 3).Trim();
                    foreach (string rawLine in front.Split(new[] { '\n' }))
                    {
                        // 單字元 TrimEnd(char) 是 Mono 專屬多載，這裡只用 Trim()（本就清掉 \r）。
                        string line = rawLine.Trim();
                        if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                        {
                            string value = line.Substring(5).Trim();
                            if (value.Length > 0) name = value;
                        }
                        else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                        {
                            description = line.Substring(12).Trim();
                        }
                    }
                }
            }
            if (description.Length == 0)
            {
                description = FirstLine(body);
            }
            if (body.Length > MaxSkillChars)
            {
                body = body.Substring(0, MaxSkillChars) + "\n[skill truncated]";
            }
            return new AgentSkill { Name = name, Description = description, Body = body };
        }

        private static string FirstLine(string text)
        {
            foreach (string rawLine in text.Split(new[] { '\n' }))
            {
                string line = rawLine.Trim().TrimStart(new[] { '#', ' ', '\t' });
                if (line.Length > 0) return line;
            }
            return string.Empty;
        }
    }
}
