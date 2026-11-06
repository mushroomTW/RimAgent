using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 要玩家按選項的事件：流浪者求加入、難民求助、勒索贖金等抉擇信件，以及開著的對話框。
/// 不回應就招不到人；會暫停遊戲的對話框更會讓自主遊玩整個卡住。
/// </summary>
public sealed class ChoiceTools
{
    internal const string DialogIdPrefix = "dialog-";
    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;

    public ChoiceTools(IGameWorld world, ConfirmationGate gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
    }

    [AgentTool("list_pending_choices", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Lists events waiting for the player's decision: choice letters (a wanderer asking to join, refugees, ransom demands and the like), open dialogs and confirmation boxes, and game screens waiting for input (world map tile and destination pickers, map target pickers, menus, ritual and ceremony starts, transporter loading, new colony selection, gravship landing), each with an id, its text, numbered options and when it expires. Answer them with choose_option.")]
    public string ListPendingChoices()
    {
        IReadOnlyList<PendingChoiceInfo> choices = _world.GetPendingChoices();
        if (choices.Count == 0) return "No pending choices.";
        return string.Join("\n\n", choices.Select(Format));
    }

    [AgentTool("choose_option", ToolCategory.Observe, ToolRisk.Confirm)]
    [Description("Answers one pending choice by picking a numbered option, exactly as clicking it in-game. A choice that shows an Input line also needs input in that format. Many answers are permanent (accepting a joiner, paying a ransom, turning refugees away). If a dialog follows, the reply lists its options.")]
    public string ChooseOption(
        [Description("Choice id from list_pending_choices, e.g. \"letter-12\".")] string choiceId,
        [Description("Option number from list_pending_choices.")] int option,
        [Description("Only when the choice shows an Input line: the tile, target or selection in that format.")] string input = null)
    {
        string id = choiceId?.Trim() ?? string.Empty;
        PendingChoiceInfo choice = _world.GetPendingChoices().FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (choice == null) return ToolText.Error($"no pending choice with id \"{id}\". Call list_pending_choices for the current ids.");
        ChoiceOptionInfo picked = choice.Options.FirstOrDefault(o => o.Number == option);
        if (picked == null)
        {
            return ToolText.Error($"\"{choice.Title}\" has no option {option}. Options: " + string.Join("; ", choice.Options.Select(FormatOption)));
        }
        if (picked.Disabled) return ToolText.Error($"option {option} of \"{choice.Title}\" is disabled" + DisabledSuffix(picked));

        // 輸入常是清單序號，附上清單，確認的人才看得出選了什麼。
        string given = string.IsNullOrEmpty(input) ? string.Empty : $" Input: {input}." + (string.IsNullOrEmpty(choice.Text) ? string.Empty : "\n" + choice.Text);
        string blocked = ToolText.Blocked(_gate, $"would answer \"{choice.Title}\" with option {option}: {picked.Text}.{given}");
        if (blocked != null) return blocked;

        CommandResult r = _world.ChooseOption(choice.Id, option, input);
        if (!r.Success) return ToolText.Error(r.Message);
        string summary = $"Chose \"{r.Message}\" on \"{choice.Title}\".";
        // 選完可能接著出下一段對話（信件也可能開出新的對話框）；附上還開著的，免得模型以為事情結束了。
        List<PendingChoiceInfo> dialogs = [.. _world.GetPendingChoices().Where(c => c.Id.StartsWith(DialogIdPrefix, StringComparison.OrdinalIgnoreCase))];
        if (dialogs.Count == 0) return summary;
        return summary + " A dialog is still open and waits for an answer:\n" + string.Join("\n\n", dialogs.Select(Format));
    }

    internal static string Format(PendingChoiceInfo c)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(c.Id).Append("] ").Append(string.IsNullOrEmpty(c.Title) ? "(untitled)" : c.Title);
        if (c.ExpiresInTicks.HasValue) sb.Append(" (expires in ").Append(ToolText.Num(Math.Max(0, c.ExpiresInTicks.Value) / 2500f)).Append(" h)");
        if (!string.IsNullOrEmpty(c.Text)) sb.AppendLine().Append(c.Text);
        if (!string.IsNullOrEmpty(c.InputHint)) sb.AppendLine().Append("Input: ").Append(c.InputHint);
        sb.AppendLine().Append("Options: ").Append(c.Options.Count == 0 ? "none" : string.Join("; ", c.Options.Select(FormatOption)));
        return sb.ToString();
    }

    private static string FormatOption(ChoiceOptionInfo o)
    {
        return o.Number + ") " + o.Text + (o.Disabled ? " (disabled" + DisabledSuffix(o) + ")" : string.Empty);
    }

    private static string DisabledSuffix(ChoiceOptionInfo o)
    {
        return string.IsNullOrEmpty(o.DisabledReason) ? string.Empty : ": " + o.DisabledReason;
    }
}
