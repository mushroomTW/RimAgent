using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

public sealed class FloatMenuTools
{
    private readonly IGameWorld _world;
    private readonly ConfirmationGate? _gate;

    public FloatMenuTools(IGameWorld world, ConfirmationGate? gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
    }

    [AgentTool("list_right_click_menu", ToolCategory.Build, ToolRisk.Query)]
    [Description("Lists the right-click menu options a colonist gets on a target, exactly as the game builds them: option number, label and whether it is disabled. A disabled option carries the game's reason in its label. The target is a thing id (targetId, from find_things, list_colonists...) or a cell (targetX and targetZ); options for everything on that cell are included. Drafted and undrafted colonists get different options (the reply says which; draft or undraft first when needed). Covers using items such as a neuroformer, interacting with buildings, boarding, studying, picking up and more. Run an option with use_right_click_option.")]
    public string ListRightClickMenu(
        [Description("Colonist name, nickname or id.")] string colonist,
        [Description("Target thing id; -1 for none. Give either this or targetX and targetZ.")] int targetId = -1,
        [Description("Target cell x; -1 for none.")] int targetX = -1,
        [Description("Target cell z; -1 for none.")] int targetZ = -1)
    {
        Result<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
        if (!pawn.Success) return pawn.Error;
        Result<MapTarget> target = RequireTarget(targetId, targetX, targetZ);
        if (!target.Success) return target.Error;

        FloatMenuInfo menu = _world.GetFloatMenuOptions(pawn.Value.Id, target.Value);
        if (!string.IsNullOrEmpty(menu.Error)) return ToolText.Error(menu.Error);
        string header = $"Right-click menu of {menu.PawnName} ({(menu.Drafted ? "drafted" : "not drafted")}) on {Target(menu, target.Value)}:";
        if (menu.Options.Count == 0) return header + " no options.";
        return header + "\n" + string.Join('\n', menu.Options.Select(Format));
    }

    [AgentTool("use_right_click_option", ToolCategory.Build, ToolRisk.Confirm)]
    [Description("Runs one right-click menu option for a colonist on a target, exactly like clicking it in the game menu. The menu is rebuilt first and the option number must still match its label, so list it again with list_right_click_menu if anything changed. Some options are irreversible (using a neuroformer, entering a ship, activating a monolith). When the option asks for confirmation, the reply lists the dialog; answer it with choose_option. An option that asks for a further target or opens a selection screen stays open; the reply lists it and choose_option answers it.")]
    public string UseRightClickOption(
        [Description("Colonist name, nickname or id.")] string colonist,
        [Description("Option number or label from list_right_click_menu.")] string option,
        [Description("Target thing id; -1 for none. Give either this or targetX and targetZ.")] int targetId = -1,
        [Description("Target cell x; -1 for none.")] int targetX = -1,
        [Description("Target cell z; -1 for none.")] int targetZ = -1)
    {
        Result<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
        if (!pawn.Success) return pawn.Error;
        Result<MapTarget> target = RequireTarget(targetId, targetX, targetZ);
        if (!target.Success) return target.Error;

        FloatMenuInfo menu = _world.GetFloatMenuOptions(pawn.Value.Id, target.Value);
        if (!string.IsNullOrEmpty(menu.Error)) return ToolText.Error(menu.Error);
        string needle = option?.Trim() ?? string.Empty;
        if (!int.TryParse(needle, CultureInfo.InvariantCulture, out int _) && menu.Options.Where(o => string.Equals(o.Label, needle, StringComparison.OrdinalIgnoreCase)).Skip(1).Any())
            return ToolText.Error($"{menu.PawnName}'s menu has multiple options named \"{needle}\". Use an option number from list_right_click_menu.");
        FloatMenuOptionInfo? picked = Resolve(menu.Options, needle);
        if (picked == null)
        {
            string options = menu.Options.Count == 0 ? "none" : string.Join("; ", menu.Options.Select(o => o.Number.ToString(CultureInfo.InvariantCulture) + ") " + o.Label));
            return ToolText.Error($"{menu.PawnName} has no option \"{option}\" on {Target(menu, target.Value)}. Options: {options}");
        }
        if (picked.Disabled)
        {
            return ToolText.Error($"\"{picked.Label}\" is disabled (the game's reason is in the label).");
        }

        string? blocked = ToolText.Blocked(_gate, $"would order {menu.PawnName} to \"{picked.Label}\" on {Target(menu, target.Value)}.");
        if (blocked != null) return blocked;

        CommandResult r = _world.UseFloatMenuOption(pawn.Value.Id, target.Value, picked.Number, picked.Label);
        if (!r.Success) return ToolText.Error(r.Message);
        string summary = $"Ordered {menu.PawnName}: \"{picked.Label}\" on {Target(menu, target.Value)}" + (string.IsNullOrEmpty(r.Message) ? "." : ": " + r.Message + ".");
        return summary + GizmoTools.OpenDialogs(_world);
    }

    private static Result<MapTarget> RequireTarget(int targetId, int x, int z)
    {
        Result<MapTarget?> parsed = TargetResolver.ResolveMapTarget(targetId, x, z);
        if (!parsed.Success) return Result<MapTarget>.Fail(parsed.Error);
        if (parsed.Value == null)
            return Result<MapTarget>.Fail(ToolText.Error("give a target: targetId (a thing id) or targetX and targetZ (a cell)."));
        return Result<MapTarget>.Ok(parsed.Value);
    }

    private static FloatMenuOptionInfo? Resolve(IReadOnlyList<FloatMenuOptionInfo> options, string needle)
    {
        if (needle.Length == 0) return null;
        if (int.TryParse(needle, CultureInfo.InvariantCulture, out int number)) return options.FirstOrDefault(o => o.Number == number);
        FloatMenuOptionInfo? exact = options.FirstOrDefault(o => string.Equals(o.Label, needle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        List<FloatMenuOptionInfo> partial = [.. options.Where(o => o.Label.Contains(needle, StringComparison.OrdinalIgnoreCase))];
        return partial.Count == 1 ? partial[0] : null;
    }

    private static string Target(FloatMenuInfo menu, MapTarget target)
    {
        return string.IsNullOrEmpty(menu.TargetLabel) ? target.ToString() : menu.TargetLabel;
    }

    private static string Format(FloatMenuOptionInfo o)
    {
        var sb = new StringBuilder();
        sb.Append(o.Number).Append(") ").Append(string.IsNullOrEmpty(o.Label) ? "(unnamed)" : o.Label);
        if (o.Disabled) sb.Append(" [disabled]");
        return sb.ToString();
    }
}
