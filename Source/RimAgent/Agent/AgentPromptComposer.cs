#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimAgent.Agent;

/// <summary>每句指令的系統提示詞組裝；任一層為空就跳過，不留空標題。</summary>
public static class AgentPromptComposer
{
    public const string FallbackPreamble =
@"You are RimAgent. You manage the player's RimWorld colony through tools; the player talks to you in the game's Agent tab.";

    /// <summary>模型常在規劃階段就決定一件一件放，看到工具說明時已來不及，所以房型建議放進系統提示詞；設定只在設定頁改，不破壞前綴快取。</summary>
    public const string LayoutsHint =
        "Room layouts are enabled: build every room (bedroom, kitchen, workshop, storage, hospital, etc.) with place_blueprint shape=layout first. " +
        "List the layouts with an empty building and pick the closest fit; place walls, doors and furniture one by one only when the player asks for a specific size or shape, or no layout fits.";

    /// <summary>房型開關開著，且玩家沒關掉建造類工具（關掉時模型拿不到 place_blueprint）才附 <see cref="LayoutsHint"/>。</summary>
    public static bool LayoutsHintApplies(bool layoutsOn, RimAgent.Tools.ToolPermissions permissions)
    {
        return layoutsOn && (permissions == null || permissions.Allows(RimAgent.Tools.ToolCategory.Build));
    }

    public const int MaxSkillsChars = 8000;

    /// <summary>易變的記憶索引由呼叫端接在尾端，保住前綴快取。</summary>
    public static string ComposeWithSkillIndex(string preamble, string playerNotes, string conduct, IReadOnlyList<AgentSkill> skills, string expansions = null, bool layouts = false)
    {
        var sb = new StringBuilder(preamble ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(expansions))
        {
            sb.Append("\n\n").Append(expansions.Trim());
        }
        if (layouts)
        {
            sb.Append("\n\n").Append(LayoutsHint);
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

    /// <summary>DLC 名稱用 defName，不隨遊戲語言變動；本體不列。整場遊戲不變，不破壞前綴快取。</summary>
    public static string FormatExpansions(IReadOnlyList<(string Name, bool IsCore, bool IsActive)> expansions)
    {
        var dlcs = (expansions ?? []).Where(e => !e.IsCore).ToList();
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

    public const int MaxModsChars = 1500;

    /// <summary>模組清單整場遊戲不變，不破壞前綴快取；超過上限只列前面的名稱並附剩餘數量。</summary>
    public static string FormatMods(IReadOnlyList<string> modNames)
    {
        if (modNames == null || modNames.Count == 0) return string.Empty;
        var shown = new List<string>();
        int length = 0;
        foreach (string name in modNames)
        {
            if (length + name.Length + 2 > MaxModsChars) break;
            shown.Add(name);
            length += name.Length + 2;
        }
        string list = string.Join(", ", shown);
        if (shown.Count < modNames.Count) list += $" (+{modNames.Count - shown.Count} more)";
        return "Mods active: " + list + ". Mods can change vanilla rules, mechanics, costs and needs, so your RimWorld knowledge may not apply. " +
               "When tool results, menus, alerts or lookup_def disagree with what you expect, trust the game and adapt. " +
               "lookup_def with kind=mod shows what a mod adds and which game code it patches.";
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
        sb.Append("\n\nAvailable skills (names and descriptions only; use read_skill to load the full text when relevant; they are written for vanilla RimWorld, so mods can make parts of them wrong):\n")
            .Append(TrimTrailingWhitespace(section.ToString()));
        if (omitted > 0) sb.Append("\n[").Append(omitted).Append(" further skill(s) omitted: over budget]");
    }

    private static bool TryAppendSkillEntry(StringBuilder section, string entry)
    {
        if (section.Length + entry.Length > MaxSkillsChars) return false;
        section.Append(entry);
        return true;
    }

    private static string TrimTrailingWhitespace(string value)
    {
        return (value ?? string.Empty).TrimEnd('\r', '\n', ' ', '\t');
    }
}
