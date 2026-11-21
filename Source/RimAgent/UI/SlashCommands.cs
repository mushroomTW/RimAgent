#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RimAgent.UI;

public sealed class SlashCommand
{
    public string Name { get; }
    public bool TakesArgs { get; }
    public string DescriptionKey { get; }

    public SlashCommand(string name, bool takesArgs, string descriptionKey)
    {
        Name = name;
        TakesArgs = takesArgs;
        DescriptionKey = descriptionKey;
    }
}

public enum GoalAction
{
    List,
    Add,
    Remove,
    Edit,
    Show,
    Clear,
    Pause,
    Resume
}

/// <summary>純字串邏輯，不得碰 Verse（供單元測試）。</summary>
public static class SlashCommands
{
    public static readonly SlashCommand Goal = new("goal", takesArgs: true, "RimAgent.Slash.Goal");
    public static readonly SlashCommand Clear = new("clear", takesArgs: false, "RimAgent.Slash.Clear");
    public static readonly SlashCommand Compact = new("compact", takesArgs: false, "RimAgent.Slash.Compact");
    public static readonly SlashCommand Stop = new("stop", takesArgs: false, "RimAgent.Slash.Stop");

    /// <summary>復原不是指令：狀態列在有可復原的操作時顯示按鈕（見 Window_Agent.DrawUndoButton）。</summary>
    public static readonly IReadOnlyList<SlashCommand> All = [Goal, Clear, Compact, Stop];

    public static bool IsTypingName(string input)
    {
        return input != null && input.Length > 0 && input[0] == '/' && input.IndexOf(' ', StringComparison.Ordinal) < 0;
    }

    public static List<SlashCommand> Match(string input)
    {
        if (!IsTypingName(input)) return [];
        string prefix = input.Substring(1);
        return [.. All.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
    }

    public static bool TryParse(string input, out SlashCommand command, out string args)
    {
        args = string.Empty;
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text[0] != '/')
        {
            command = null;
            return false;
        }
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string name = space < 0 ? text.Substring(1) : text.Substring(1, space - 1);
        args = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
        command = All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public static bool TryGetSkillToken(string input, out string prefix, out int start)
    {
        prefix = string.Empty;
        start = -1;
        if (string.IsNullOrEmpty(input)) return false;
        int space = input.LastIndexOfAny([' ', (char)9, (char)10]);
        start = space + 1;
        if (start >= input.Length || input[start] != '$') return false;
        prefix = input.Substring(start + 1);
        return true;
    }

    public static List<string> MatchSkills(string input, IEnumerable<string> skillNames)
    {
        if (!TryGetSkillToken(input, out string prefix, out _) || skillNames == null) return [];
        return [.. skillNames
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Trim().IndexOfAny([' ', (char)9]) < 0
                        && n.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Trim())];
    }

    public static GoalAction ParseGoal(string args, out int index, out string text)
    {
        index = 0;
        text = (args ?? string.Empty).Trim();
        if (text.Length == 0) return GoalAction.List;

        if (TryParseSimpleCommand(text, out GoalAction simpleResult)) return simpleResult;
        if (TryParseShowCommand(text, out index)) return GoalAction.Show;
        if (TryParseEditCommand(text, out index, out string rest))
        {
            text = rest;
            return GoalAction.Edit;
        }
        if (TryParseRemoveCommand(text, out index)) return GoalAction.Remove;
        return GoalAction.Add;
    }

    private static bool TryParseSimpleCommand(string text, out GoalAction result)
    {
        result = text.ToLowerInvariant() switch
        {
            "pause" => GoalAction.Pause,
            "resume" => GoalAction.Resume,
            "clear" => GoalAction.Clear,
            "show" => GoalAction.Show,
            "edit" => GoalAction.Edit,
            "remove" => GoalAction.Remove,
            _ => GoalAction.List
        };
        return result != GoalAction.List;
    }

    private static bool TryParseShowCommand(string text, out int index)
    {
        index = 0;
        if (text.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text.Substring(5).Trim(), CultureInfo.InvariantCulture, out index);
        return false;
    }

    private static bool TryParseEditCommand(string text, out int index, out string rest)
    {
        index = 0;
        rest = string.Empty;
        if (!text.StartsWith("edit ", StringComparison.OrdinalIgnoreCase)) return false;
        string trimmed = text.Substring(5).Trim();
        int separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (!int.TryParse(separator < 0 ? trimmed : trimmed.Substring(0, separator), CultureInfo.InvariantCulture, out index)) return false;
        rest = separator < 0 ? string.Empty : trimmed.Substring(separator + 1).Trim();
        return true;
    }

    private static bool TryParseRemoveCommand(string text, out int index)
    {
        index = 0;
        if (text.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(text.Substring(7).Trim(), CultureInfo.InvariantCulture, out index))
            {
                index = 0;
            }
            return true;
        }
        return false;
    }
}
