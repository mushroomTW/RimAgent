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
    /// 物件的指令按鈕（gizmo）：選取建築、物品或 pawn 時畫面下方那排按鈕，
    /// 例如飛船反應爐啟動、飛船發射、開關電源。只支援按一下與開關。
    /// </summary>
    public sealed class GizmoTools
    {
        private const string DialogIdPrefix = "dialog-";
        private const int MaxDescriptionChars = 200;
        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public GizmoTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("list_gizmos", ToolCategory.Build, ToolRisk.Query)]
        [Description("Lists the command buttons (gizmos) shown when a building, item or pawn is selected: number, label, kind (action, toggle with its state, or unsupported), whether it is disabled and why, and what it does. Ship reactor startup and ship launch are gizmos. use_gizmo presses them.")]
        public string ListGizmos(
            [Description("Thing id from find_things, list_colonists or list_threats.")] int thingId)
        {
            IReadOnlyList<GizmoInfo> gizmos = _world.GetGizmos(thingId);
            if (gizmos == null) return NotFound(thingId);
            if (gizmos.Count == 0) return $"[{thingId}] has no command buttons.";
            return $"Command buttons of [{thingId}]:\n" + string.Join("\n", gizmos.Select(Format));
        }

        [AgentTool("use_gizmo", ToolCategory.Build, ToolRisk.Confirm)]
        [Description("Presses one command button (gizmo) of a thing, exactly like clicking it: an action runs, a toggle flips. Some buttons are irreversible (ship launch, self-destruct). When the button asks for confirmation, the reply lists the dialog; answer it with choose_option.")]
        public string UseGizmo(
            [Description("Thing id from find_things, list_colonists or list_threats.")] int thingId,
            [Description("Button number or label from list_gizmos.")] string gizmo)
        {
            IReadOnlyList<GizmoInfo> gizmos = _world.GetGizmos(thingId);
            if (gizmos == null) return NotFound(thingId);
            string needle = gizmo?.Trim() ?? string.Empty;
            if (!int.TryParse(needle, out int _) && gizmos.Count(g => string.Equals(g.Label, needle, StringComparison.OrdinalIgnoreCase)) > 1)
                return ToolText.Error($"[{thingId}] has multiple buttons named \"{needle}\". Use a button number from list_gizmos.");
            GizmoInfo picked = Resolve(gizmos, gizmo);
            if (picked == null)
            {
                string buttons = gizmos.Count == 0 ? "none" : string.Join("; ", gizmos.Select(g => g.Number + ") " + g.Label));
                return ToolText.Error($"[{thingId}] has no button \"{gizmo}\". Buttons: {buttons}");
            }
            if (picked.Disabled)
            {
                return ToolText.Error($"\"{picked.Label}\" is disabled" + (string.IsNullOrEmpty(picked.DisabledReason) ? "." : ": " + picked.DisabledReason));
            }
            if (picked.Kind == GizmoInfo.UnsupportedKind)
            {
                return ToolText.Error($"\"{picked.Label}\" needs a map target or a menu choice, which use_gizmo cannot do yet.");
            }

            string blocked = ToolText.Blocked(_gate, $"would press \"{picked.Label}\" on [{thingId}].");
            if (blocked != null) return blocked;

            CommandResult r = _world.UseGizmo(thingId, picked.Number, picked.Label);
            if (!r.Success) return ToolText.Error(r.Message);
            string summary = $"Pressed \"{picked.Label}\" on [{thingId}]" + (string.IsNullOrEmpty(r.Message) ? "." : ": " + r.Message + ".");
            // 不少按鈕會先跳確認；附上開著的對話框，模型才知道事情還沒做完。
            List<PendingChoiceInfo> dialogs = _world.GetPendingChoices()
                .Where(c => c.Id.StartsWith(DialogIdPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dialogs.Count == 0) return summary;
            return summary + " A dialog is open and waits for an answer (choose_option):\n" + string.Join("\n\n", dialogs.Select(ChoiceTools.Format));
        }

        /// <summary>序號優先；否則標籤完全相同，再退而求其次找唯一一個包含這段文字的。</summary>
        private static GizmoInfo Resolve(IReadOnlyList<GizmoInfo> gizmos, string input)
        {
            string needle = input?.Trim() ?? string.Empty;
            if (needle.Length == 0) return null;
            if (int.TryParse(needle, out int number)) return gizmos.FirstOrDefault(g => g.Number == number);
            GizmoInfo exact = gizmos.FirstOrDefault(g => string.Equals(g.Label, needle, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            List<GizmoInfo> partial = gizmos.Where(g => g.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }

        private static string Format(GizmoInfo g)
        {
            var sb = new StringBuilder();
            sb.Append(g.Number).Append(") ").Append(string.IsNullOrEmpty(g.Label) ? "(unnamed)" : g.Label).Append(" [").Append(g.Kind);
            if (g.Kind == GizmoInfo.ToggleKind) sb.Append(g.On ? ", on" : ", off");
            sb.Append(']');
            if (g.Disabled) sb.Append(" disabled").Append(string.IsNullOrEmpty(g.DisabledReason) ? string.Empty : ": " + g.DisabledReason);
            if (!string.IsNullOrEmpty(g.Description))
            {
                string description = g.Description.Replace('\n', ' ');
                if (description.Length > MaxDescriptionChars) description = description.Substring(0, MaxDescriptionChars) + "…";
                sb.Append(" — ").Append(description);
            }
            return sb.ToString();
        }

        private static string NotFound(int thingId)
        {
            return ToolText.Error($"no visible thing with id {thingId}. Use find_things to get a valid id.");
        }
    }
}
