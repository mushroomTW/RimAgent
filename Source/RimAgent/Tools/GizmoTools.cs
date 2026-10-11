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
    /// 例如飛船反應爐啟動、飛船發射、開關電源。支援按一下、開關，以及按下後要在地圖上選目標的按鈕
    /// （迫擊砲指定射擊位置、虛空巨石柱與互動物件指名殖民者、使用物品指名使用者）。
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
        [Description("Lists the command buttons (gizmos) shown when a building, item or pawn is selected: number, label, kind (action, toggle with its state, target with the kinds of target it accepts, or unsupported), whether it is disabled and why, and what it does. Ship reactor startup and ship launch are gizmos. use_gizmo presses them. A target button (turret force-target, abilities) needs a thing id or a cell. Some buttons listed as action (for example activating the void monolith or an interactable) also start targeting when pressed; use_gizmo then reports what target they want.")]
        public string ListGizmos(
            [Description("Thing id from find_things, list_colonists or list_threats.")] int thingId)
        {
            IReadOnlyList<GizmoInfo> gizmos = _world.GetGizmos(thingId);
            if (gizmos == null) return NotFound(thingId);
            if (gizmos.Count == 0) return $"[{thingId}] has no command buttons.";
            return $"Command buttons of [{thingId}]:\n" + string.Join("\n", gizmos.Select(Format));
        }

        [AgentTool("use_gizmo", ToolCategory.Build, ToolRisk.Confirm)]
        [Description("Presses one command button (gizmo) of a thing, exactly like clicking it: an action runs, a toggle flips. A button that asks for a target on the map (kind target in list_gizmos, or an action that turns out to start targeting) takes it as targetId (a thing id, for example the colonist who should do it) or as targetX and targetZ (a cell); the target is checked exactly like the game's own targeter and the game's reason is returned when it is refused. Some buttons are irreversible (ship launch, self-destruct). When the button asks for confirmation, the reply lists the dialog; answer it with choose_option.")]
        public string UseGizmo(
            [Description("Thing id from find_things, list_colonists or list_threats.")] int thingId,
            [Description("Button number or label from list_gizmos.")] string gizmo,
            [Description("Target thing id (colonist, building, item...) for a button that asks for a target; -1 for none. Give either this or targetX and targetZ.")] int targetId = -1,
            [Description("Target cell x for a button that asks for a target; -1 for none.")] int targetX = -1,
            [Description("Target cell z for a button that asks for a target; -1 for none.")] int targetZ = -1)
        {
            Result<MapTarget> parsed = TargetResolver.ResolveMapTarget(targetId, targetX, targetZ);
            if (!parsed.Success) return parsed.Error;
            MapTarget target = parsed.Value;

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
                return ToolText.Error($"\"{picked.Label}\" needs a menu choice or another kind of input that use_gizmo cannot give.");
            }
            if (picked.Kind == GizmoInfo.ToggleKind && target != null)
            {
                return ToolText.Error($"\"{picked.Label}\" is a toggle and takes no target.");
            }
            if (picked.Kind == GizmoInfo.TargetKind && target == null)
            {
                return ToolText.Error($"\"{picked.Label}\" needs a target: {TargetKinds(picked)}. Pass targetId (a thing id) or targetX and targetZ (a cell).");
            }

            string blocked = ToolText.Blocked(_gate, $"would press \"{picked.Label}\" on [{thingId}]" + (target == null ? "." : $" aimed at {target}."));
            if (blocked != null) return blocked;

            CommandResult r = _world.UseGizmo(thingId, picked.Number, picked.Label, target);
            if (!r.Success) return ToolText.Error(r.Message);
            string summary = $"Pressed \"{picked.Label}\" on [{thingId}]" + (string.IsNullOrEmpty(r.Message) ? "." : ": " + r.Message + ".");
            return summary + OpenDialogs(_world);
        }

        /// <summary>
        /// 不少按鈕與右鍵選項會先跳確認；附上開著的對話框，模型才知道事情還沒做完。沒有對話框回傳空字串。
        /// </summary>
        internal static string OpenDialogs(IGameWorld world)
        {
            List<PendingChoiceInfo> dialogs = world.GetPendingChoices()
                .Where(c => c.Id.StartsWith(DialogIdPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (dialogs.Count == 0) return string.Empty;
            return " A dialog is open and waits for an answer (choose_option):\n" + string.Join("\n\n", dialogs.Select(ChoiceTools.Format));
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
            if (g.Kind == GizmoInfo.TargetKind) sb.Append(": ").Append(TargetKinds(g));
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

        private static string TargetKinds(GizmoInfo g)
        {
            return string.IsNullOrEmpty(g.TargetHint) ? "a thing or a cell" : g.TargetHint;
        }

        private static string NotFound(int thingId)
        {
            return ToolText.Error($"no visible thing with id {thingId}. Use find_things to get a valid id.");
        }
    }
}
