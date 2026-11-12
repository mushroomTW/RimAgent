using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimAgent.Tools;

namespace RimAgent.Agent;

/// <summary>一個技能包（Skills/ 下的一個資料夾，以 SKILL.md 為本體）。</summary>
public sealed class AgentSkill
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// 一個子代理（Agents/ 下的一個資料夾，以 AGENT.md 為本體）：主 Agent 用 delegate_task 把任務交給它，
/// 它以獨立上下文、自己的工具類別與模型槽跑完，只把報告交回主 Agent。
/// </summary>
public sealed class AgentSubagent
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>子代理可用的工具類別；仍要與玩家權限取交集，且 <see cref="ToolCategory.Agents"/> 永遠不給（只委派一層）。</summary>
    public IReadOnlyList<ToolCategory> Categories { get; set; } = new[] { ToolCategory.Observe };
    /// <summary>只拿查詢工具；唯讀子代理的多個任務可並行執行。</summary>
    public bool ReadOnly { get; set; }
    /// <summary>思考強度；null 則用設定頁的子代理預設。</summary>
    public ReasoningLevel? Reasoning { get; set; }
    /// <summary>子代理的系統提示詞本文。</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>出貨的 agent 擴充包內容：前情提要、玩家自留地、內建行為準則、Ask 註記、分類器提示詞、自主任務、技能包與子代理。</summary>
public sealed class AgentPack
{
    /// <summary>前情提要（Briefing.md）：每次請求的第一層。缺檔時呼叫端用內建 fallback 兜底。</summary>
    public string Preamble { get; set; } = string.Empty;
    /// <summary>玩家自留地（AGENTS.md）：出貨是空的，玩家想交代 agent 的事寫這裡，擺在前情提要之後。</summary>
    public string PlayerNotes { get; set; } = string.Empty;
    /// <summary>常駐行為準則（Conduct.md）。</summary>
    public string Conduct { get; set; } = string.Empty;
    /// <summary>自動模式風險分類器的系統提示詞（RiskClassifier.md）。缺檔時分類器 fail-closed。</summary>
    public string ClassifierPrompt { get; set; } = string.Empty;
    /// <summary>自動模式決策模型的判準檔（DecisionRisk.md）。缺檔時回退 LLM 分類器。</summary>
    public string DecisionRiskCriteria { get; set; } = string.Empty;
    /// <summary>目標審計決策模型的判準檔（DecisionGoal.md）。缺檔時目標審計回傳 null。</summary>
    public string DecisionGoalCriteria { get; set; } = string.Empty;
    /// <summary>System One 代答抉擇的判準檔（DecisionChoice.md）。缺檔時抉擇照舊交給主 LLM。</summary>
    public string DecisionChoiceCriteria { get; set; } = string.Empty;
    /// <summary>System One 篩選基地狀況自主任務的判準檔（DecisionTriage.md）。缺檔時照舊叫主 LLM。</summary>
    public string DecisionTriageCriteria { get; set; } = string.Empty;
    /// <summary>自主任務文案（Autonomous.en.md，英文）。缺檔時該輪自主任務跳過。</summary>
    public AutonomousPack Autonomous { get; set; } = new AutonomousPack();
    public IReadOnlyList<AgentSkill> Skills { get; set; } = Array.Empty<AgentSkill>();
    public IReadOnlyList<AgentSubagent> Subagents { get; set; } = Array.Empty<AgentSubagent>();
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
/// 讀 AgentPack/ 資料夾（Prompts/ 下的內建提示詞、根目錄的 AGENTS.md、Skills/*/SKILL.md、Agents/*/AGENT.md）。
/// 純檔案邏輯，不碰遊戲 API，
/// rootDir 可注入，方便測試。缺檔、壞檔一律視為空，不丟例外。
/// </summary>
public static class AgentPackLoader
{
    /// <summary>單個技能讀進提示詞的上限（字數），超過截斷並註明。</summary>
    public const int MaxSkillChars = 3000;
    /// <summary>單個子代理提示詞的上限（字數），超過截斷並註明。</summary>
    public const int MaxSubagentChars = 4000;

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
            pack.ClassifierPrompt = ReadFile(promptsDir, "RiskClassifier.md");
            pack.DecisionRiskCriteria = ReadFile(promptsDir, "DecisionRisk.md");
            pack.DecisionGoalCriteria = ReadFile(promptsDir, "DecisionGoal.md");
            pack.DecisionChoiceCriteria = ReadFile(promptsDir, "DecisionChoice.md");
            pack.DecisionTriageCriteria = ReadFile(promptsDir, "DecisionTriage.md");
            LoadAutonomous(pack, promptsDir);
            pack.Subagents = LoadSubagents(Path.Combine(rootDir, "Agents"));

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
        bool inComment = false;
        List<string> lines = [.. Lines(text)];
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            // 註解留給編輯提示詞的人看，不該餵給模型。註解可跨多行，一路略過到含 --> 的那行
            // （該行 --> 之後的文字一併略過）；註解中的 ## 也不算標題。
            // 忘了收尾的註解只略過開頭那一行，免得整份檔案後面的章節無聲消失。
            if (inComment || line.StartsWith("<!--", StringComparison.Ordinal))
            {
                inComment = line.IndexOf("-->", StringComparison.Ordinal) < 0
                    && (inComment || lines.Skip(i + 1).Any(l => l.IndexOf("-->", StringComparison.Ordinal) >= 0));
                continue;
            }
            // Lines 已 Trim（含 \r），標題行即整行比對。
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection(sections, ref current, body);
                current = line.Substring(3).Trim();
                continue;
            }
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

    /// <summary>只讀子代理（設定頁用，不必讀整個擴充包）；缺檔、壞檔一樣視為空。</summary>
    public static IReadOnlyList<AgentSubagent> LoadSubagentsOnly(string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir)) return Array.Empty<AgentSubagent>();
        try
        {
            return LoadSubagents(Path.Combine(rootDir, "Agents"));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<AgentSubagent>();
        }
    }

    /// <summary>Agents/*/AGENT.md，依資料夾名排序；同名子代理只留第一個（名稱是設定頁與委派的鍵）。</summary>
    private static IReadOnlyList<AgentSubagent> LoadSubagents(string agentsDir)
    {
        if (!Directory.Exists(agentsDir)) return Array.Empty<AgentSubagent>();
        var subagents = new List<AgentSubagent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] dirs = Directory.GetDirectories(agentsDir);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        foreach (string dir in dirs)
        {
            string path = Path.Combine(dir, "AGENT.md");
            if (!File.Exists(path)) continue;
            string text = (File.ReadAllText(path) ?? string.Empty).Trim();
            if (text.Length == 0) continue;
            AgentSubagent subagent = ParseSubagent(Path.GetFileName(dir) ?? dir, text);
            if (subagent.Name.Length == 0 || subagent.Body.Length == 0 || !seen.Add(subagent.Name)) continue;
            subagents.Add(subagent);
        }
        return subagents;
    }

    /// <summary>
    /// frontmatter：name、description、categories（逗號分隔的 <see cref="ToolCategory"/> 名稱，不分大小寫）、
    /// read_only（true／yes／on／1 為是）、reasoning（<see cref="ReasoningLevel"/> 名稱）。
    /// 名稱一律轉小寫當鍵；沒寫類別或全寫錯時只給觀察類；Agents 類別一律剔除。
    /// </summary>
    internal static AgentSubagent ParseSubagent(string folderName, string text)
    {
        var subagent = new AgentSubagent { Name = folderName };
        string body = text;
        var categories = new List<ToolCategory>();
        if (text.StartsWith("---"))
        {
            int end = text.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 3)
            {
                body = text.Substring(end + 3).Trim();
                foreach (string line in Lines(text.Substring(3, end - 3)))
                {
                    ApplySubagentField(subagent, categories, line);
                }
            }
        }
        subagent.Name = subagent.Name.Trim().ToLowerInvariant();
        if (categories.Count > 0) subagent.Categories = categories;
        if (subagent.Description.Length == 0) subagent.Description = FirstLine(body);
        if (body.Length > MaxSubagentChars) body = body.Substring(0, MaxSubagentChars) + "\n[agent prompt truncated]";
        subagent.Body = body;
        return subagent;
    }

    private static void ApplySubagentField(AgentSubagent subagent, List<ToolCategory> categories, string line)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) return;
        string key = line.Substring(0, colon).Trim().ToLowerInvariant();
        string value = line.Substring(colon + 1).Trim();
        switch (key)
        {
            case "name":
                if (value.Length > 0) subagent.Name = value;
                break;
            case "description":
                subagent.Description = value;
                break;
            case "categories":
                foreach (string token in value.Split(','))
                {
                    // 子代理不委派、也不能讀檔換掉整場遊戲。
                    if (TryParseName(token, out ToolCategory category) && category != ToolCategory.Agents
                        && category != ToolCategory.Save && !categories.Contains(category))
                    {
                        categories.Add(category);
                    }
                }
                break;
            case "read_only":
                // YAML 常見的真值寫法都算；寫錯的值當作否，子代理拿到的是依序執行的完整工具。
                subagent.ReadOnly = Array.IndexOf(["true", "yes", "on", "1"], value.ToLowerInvariant()) >= 0;
                break;
            case "reasoning":
                if (TryParseName(value, out ReasoningLevel level)) subagent.Reasoning = level;
                break;
            default:
                break;
        }
    }

    /// <summary>Enum.TryParse 會把數字字串也當成合法值；這裡只收名稱。</summary>
    private static bool TryParseName<T>(string value, out T result) where T : struct
    {
        result = default(T);
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0 || char.IsDigit(trimmed[0]) || trimmed[0] == '-') return false;
        return Enum.TryParse(trimmed, true, out result) && Enum.IsDefined(typeof(T), result);
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

    /// <summary>兩處行迴圈共用的列舉器：集中切行與去除空白（含 \r）。</summary>
    private static IEnumerable<string> Lines(string text)
    {
        foreach (string rawLine in (text ?? string.Empty).Split('\n'))
        {
            yield return rawLine.Trim();
        }
    }

    private static string FirstLine(string text)
    {
        foreach (string line in Lines(text))
        {
            string stripped = line.TrimStart('#', ' ', '\t');
            if (stripped.Length > 0) return stripped;
        }
        return string.Empty;
    }
}
