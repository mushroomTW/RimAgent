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

    /// <summary>出貨的 agent 擴充包內容：前情提要、玩家自留地、內建行為準則、Ask 註記、分類器提示詞、自主任務與技能包。</summary>
    public sealed class AgentPack
    {
        /// <summary>前情提要（Briefing.md）：每次請求的第一層。缺檔時呼叫端用內建 fallback 兜底。</summary>
        public string Preamble { get; set; } = string.Empty;
        /// <summary>玩家自留地（AGENTS.md）：出貨是空的，玩家想交代 agent 的事寫這裡，擺在前情提要之後。</summary>
        public string PlayerNotes { get; set; } = string.Empty;
        /// <summary>常駐行為準則（Conduct.md）。</summary>
        public string Conduct { get; set; } = string.Empty;
        /// <summary>詢問模式附加註記（AskMode.md）。</summary>
        public string AskModeNote { get; set; } = string.Empty;
        /// <summary>自動模式風險分類器的系統提示詞（RiskClassifier.md）。缺檔時分類器 fail-closed。</summary>
        public string ClassifierPrompt { get; set; } = string.Empty;
        /// <summary>自主任務文案（Autonomous.en.md，英文）。缺檔時該輪自主任務跳過。</summary>
        public AutonomousPack Autonomous { get; set; } = new AutonomousPack();
        public IReadOnlyList<AgentSkill> Skills { get; set; } = Array.Empty<AgentSkill>();
    }

    /// <summary>一個語言的自主任務文案：共用通則＋觸發原因→指令文字。</summary>
    public sealed class AutonomousPack
    {
        /// <summary>
        /// 第一個 <c>## </c> 章節之前的通則，會接在每個任務前面。
        /// 寫作時要假設模型看得到：共用規則（要產出、不要只回報）放這裡比每章節重複寫好。
        /// </summary>
        public string Preamble { get; set; } = string.Empty;
        public IReadOnlyDictionary<string, string> Tasks { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 讀 AgentPack/ 資料夾（Prompts/ 下的內建提示詞、根目錄的 AGENTS.md、Skills/*/SKILL.md）。
    /// 純檔案邏輯，不碰遊戲 API，
    /// rootDir 可注入，方便測試。缺檔、壞檔一律視為空，不丟例外。
    /// </summary>
    public static class AgentPackLoader
    {
        /// <summary>單個技能讀進提示詞的上限（字數），超過截斷並註明。</summary>
        public const int MaxSkillChars = 3000;

        /// <summary>自主任務檔名（只留英文）。</summary>
        internal const string AutonomousFileName = "Autonomous.en.md";
        private const string PromptsFolderName = "Prompts";

        public static AgentPack Load(string rootDir)
        {
            var pack = new AgentPack();
            if (string.IsNullOrEmpty(rootDir)) return pack;
            try
            {
                string promptsDir = Path.Combine(rootDir, PromptsFolderName);
                pack.Preamble = ReadFile(promptsDir, "Briefing.md");
                pack.PlayerNotes = ReadFile(rootDir, "AGENTS.md");
                pack.Conduct = ReadFile(promptsDir, "Conduct.md");
                pack.AskModeNote = ReadFile(promptsDir, "AskMode.md");
                pack.ClassifierPrompt = ReadFile(promptsDir, "RiskClassifier.md");
                LoadAutonomous(pack, promptsDir);

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

        private static string ReadFile(string rootDir, string fileName)
        {
            string path = Path.Combine(rootDir, fileName);
            if (!File.Exists(path)) return string.Empty;
            return (File.ReadAllText(path) ?? string.Empty).Trim();
        }

        private static void LoadAutonomous(AgentPack pack, string promptsDir)
        {
            string text = ReadFile(promptsDir, AutonomousFileName);
            if (text.Length == 0) return;
            string preamble;
            IDictionary<string, string> sections = ParseSections(text, out preamble);
            var lang = new AutonomousPack { Preamble = preamble };
            var tasks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> section in sections)
            {
                if (section.Value.Length > 0)
                {
                    tasks[section.Key] = section.Value;
                }
            }
            lang.Tasks = tasks;
            pack.Autonomous = lang;
        }

        /// <summary>
        /// 切 `## 名稱` 章節；第一個標題之前的是共用通則（<paramref name="preamble"/>），
        /// 不是章節但要送給模型，所以另外回傳而不是丟掉。
        /// 同名重複以後者為準。key 嚴格比對，寫錯的章節名會被任務查詢略過。
        /// 每行 Trim 後重組：縮排會被壓平，提示詞檔避免用縮排語法（清單、code block）。
        /// </summary>
        internal static IDictionary<string, string> ParseSections(string text, out string preamble)
        {
            var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            var intro = new System.Text.StringBuilder();
            string current = null;
            var body = new System.Text.StringBuilder();
            foreach (string line in Lines(text))
            {
                // Lines 已 Trim（含 \r），標題行即整行比對。
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    FlushSection(sections, ref current, body);
                    current = line.Substring(3).Trim();
                    continue;
                }
                // 註解留給編輯提示詞的人看，不該餵給模型。
                if (line.StartsWith("<!--", StringComparison.Ordinal)) continue;
                if (current == null)
                {
                    if (intro.Length > 0) intro.Append('\n');
                    intro.Append(line);
                    continue;
                }
                if (body.Length > 0) body.Append('\n');
                body.Append(line);
            }
            FlushSection(sections, ref current, body);
            preamble = intro.ToString();
            return sections;
        }

        private static void FlushSection(IDictionary<string, string> sections, ref string current, System.Text.StringBuilder body)
        {
            if (current == null) return;
            if (current.Length > 0 && body.Length > 0)
            {
                sections[current] = body.ToString().Trim();
            }
            current = null;
            body.Length = 0;
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
                    ParseFrontmatter(front, ref name, ref description);
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

        private static void ParseFrontmatter(string front, ref string name, ref string description)
        {
            foreach (string line in Lines(front))
            {
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

        /// <summary>兩處行迴圈共用的列舉器：切行＋去空白（含 \r）集中一處，S3878 pragma 只留一個。</summary>
        private static IEnumerable<string> Lines(string text)
        {
#pragma warning disable S3878 // net472 mscorlib requires char[] overload at runtime to prevent MissingMethodException
            foreach (string rawLine in (text ?? string.Empty).Split(new[] { '\n' }))
#pragma warning restore S3878
            {
                // 單字元 TrimEnd(char) 是 Mono 專屬多載，這裡只用 Trim()（本就清掉 \r）。
                yield return rawLine.Trim();
            }
        }

        private static string FirstLine(string text)
        {
            foreach (string line in Lines(text))
            {
                string stripped = line.TrimStart(new[] { '#', ' ', '\t' });
                if (stripped.Length > 0) return stripped;
            }
            return string.Empty;
        }
    }
}
