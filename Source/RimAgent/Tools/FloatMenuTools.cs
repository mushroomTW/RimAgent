using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>
    /// 殖民者的右鍵選單（FloatMenu）：對一個物件或格子按右鍵會跳出的選項，
    /// 例如使用神經增幅器、啟動互動物件、登上載具、研究、拾取。原版選單由各種 FloatMenuOptionProvider 與
    /// 物件元件組成，這一組工具把它整份列出來，再依序號執行，涵蓋 use_gizmo 與 order_job 沒有的操作。
    /// </summary>
    public sealed class FloatMenuTools
    {
        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public FloatMenuTools(IGameWorld world, ConfirmationGate gate = null)
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
            return header + "\n" + string.Join("\n", menu.Options.Select(Format));
        }

        [AgentTool("use_right_click_option", ToolCategory.Build, ToolRisk.Confirm)]
        [Description("Runs one right-click menu option for a colonist on a target, exactly like clicking it in the game menu. The menu is rebuilt first and the option number must still match its label, so list it again with list_right_click_menu if anything changed. Some options are irreversible (using a neuroformer, entering a ship, activating a monolith). When the option asks for confirmation, the reply lists the dialog; answer it with choose_option. Options that open a further target picker or window are cancelled and reported as unsupported.")]
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
            if (!int.TryParse(needle, out int _) && menu.Options.Count(o => string.Equals(o.Label, needle, StringComparison.OrdinalIgnoreCase)) > 1)
                return ToolText.Error($"{menu.PawnName}'s menu has multiple options named \"{needle}\". Use an option number from list_right_click_menu.");
            FloatMenuOptionInfo picked = Resolve(menu.Options, needle);
            if (picked == null)
            {
                string options = menu.Options.Count == 0 ? "none" : string.Join("; ", menu.Options.Select(o => o.Number + ") " + o.Label));
                return ToolText.Error($"{menu.PawnName} has no option \"{option}\" on {Target(menu, target.Value)}. Options: {options}");
            }
            if (picked.Disabled)
            {
                return ToolText.Error($"\"{picked.Label}\" is disabled (the game's reason is in the label).");
            }

            string blocked = ToolText.Blocked(_gate, $"would order {menu.PawnName} to \"{picked.Label}\" on {Target(menu, target.Value)}.");
            if (blocked != null) return blocked;

            CommandResult r = _world.UseFloatMenuOption(pawn.Value.Id, target.Value, picked.Number, picked.Label);
            if (!r.Success) return ToolText.Error(r.Message);
            string summary = $"Ordered {menu.PawnName}: \"{picked.Label}\" on {Target(menu, target.Value)}" + (string.IsNullOrEmpty(r.Message) ? "." : ": " + r.Message + ".");
            return summary + GizmoTools.OpenDialogs(_world);
        }

        private static Result<MapTarget> RequireTarget(int targetId, int x, int z)
        {
            Result<MapTarget> parsed = TargetResolver.ResolveMapTarget(targetId, x, z);
            if (parsed.Success && parsed.Value == null)
                return Result<MapTarget>.Fail(ToolText.Error("give a target: targetId (a thing id) or targetX and targetZ (a cell)."));
            return parsed;
        }

        /// <summary>序號優先；否則標籤完全相同，再退而求其次找唯一一個包含這段文字的。</summary>
        private static FloatMenuOptionInfo Resolve(IReadOnlyList<FloatMenuOptionInfo> options, string needle)
        {
            if (needle.Length == 0) return null;
            if (int.TryParse(needle, out int number)) return options.FirstOrDefault(o => o.Number == number);
            FloatMenuOptionInfo exact = options.FirstOrDefault(o => string.Equals(o.Label, needle, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            List<FloatMenuOptionInfo> partial = options.Where(o => o.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
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
}
