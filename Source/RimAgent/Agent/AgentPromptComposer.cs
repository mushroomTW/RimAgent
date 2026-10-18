using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimAgent.Agent;

/// <summary>
/// 每句指令的系統提示詞組裝：前情提要→DLC 啟用狀況→玩家自留地→內建準則→技能索引。
/// 純字串邏輯；任一層為空就跳過，不留空標題。
/// </summary>
public static class AgentPromptComposer
{
    /// <summary>前情提要保底：擴充包遺失或損毀時模型至少知道自己是誰。</summary>
    public const string FallbackPreamble =
@"You are RimAgent. You manage the player's RimWorld colony through tools; the player talks to you in the game's Agent tab.";

    /// <summary>技能區的總量上限（字數），超過就省略剩下的並註明。</summary>
    public const int MaxSkillsChars = 8000;

    /// <summary>
    /// 索引版組裝：只列技能名稱與描述，全文由 `read_skill` 按需讀取。
    /// 易變的記憶索引由呼叫端接在尾端，保住前綴快取。
    /// </summary>
    public static string ComposeWithSkillIndex(string preamble, string playerNotes, string conduct, IReadOnlyList<AgentSkill> skills, string expansions = null)
    {
        var sb = new StringBuilder(preamble ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(expansions))
        {
            sb.Append("\n\n").Append(expansions.Trim());
        }
        if (!string.IsNullOrWhiteSpace(playerNotes))
        {
            sb.Append("\n\nPlayer notes:\n").Append(playerNotes.Trim());
        }
        if (!string.IsNullOrWhiteSpace(conduct))
        {
            sb.Append("\n\nStanding conduct:\n").Append(conduct.Trim());
        }
        AppendSkillIndex(sb, skills);
        return sb.ToString();
    }

    /// <summary>
    /// 本局 DLC 啟用狀況的一行說明（名稱用 defName，不隨遊戲語言變動）；本體不列。整場遊戲不變，不破壞前綴快取。
    /// </summary>
    public static string FormatExpansions(IReadOnlyList<(string Name, bool IsCore, bool IsActive)> expansions)
    {
        var dlcs = (expansions ?? Array.Empty<(string Name, bool IsCore, bool IsActive)>()).Where(e => !e.IsCore).ToList();
        List<string> active = [.. dlcs.Where(e => e.IsActive).Select(e => e.Name)];
        List<string> inactive = [.. dlcs.Where(e => !e.IsActive).Select(e => e.Name)];
        string line = "Game expansions active: " +
                      (active.Count == 0 ? "none (base game only)" : string.Join(", ", active)) + ".";
        if (inactive.Count > 0)
        {
            line += " Not active: " + string.Join(", ", inactive) + "; their content does not exist in this game.";
        }
        return line;
    }

    private static void AppendSkillIndex(StringBuilder sb, IReadOnlyList<AgentSkill> skills)
    {
        if (skills == null || skills.Count == 0) return;
        var section = new StringBuilder();
        int omitted = 0;
        foreach (AgentSkill skill in skills)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Name)) continue;
            string entry = "## " + skill.Name.Trim();
            if (!string.IsNullOrWhiteSpace(skill.Description))
            {
                entry += " - " + skill.Description.Trim();
            }
            entry += "\n";
            if (!TryAppendSkillEntry(section, entry)) omitted++;
        }
        if (section.Length == 0) return;
        sb.Append("\n\nAvailable skills (names and descriptions only; use read_skill to load the full text when relevant):\n")
            .Append(TrimTrailingWhitespace(section.ToString()));
        if (omitted > 0) sb.Append("\n[").Append(omitted).Append(" further skill(s) omitted: over budget]");
    }

    /// <summary>技能區共用的總量管制：塞得下回傳 true，否則記一次省略。</summary>
    private static bool TryAppendSkillEntry(StringBuilder section, string entry)
    {
        if (section.Length + entry.Length > MaxSkillsChars) return false;
        section.Append(entry);
        return true;
    }

    private static string TrimTrailingWhitespace(string value)
    {
        // 僅清除提示詞組合時使用的行尾空白。
        return (value ?? string.Empty).TrimEnd('\r', '\n', ' ', '\t');
    }
}
