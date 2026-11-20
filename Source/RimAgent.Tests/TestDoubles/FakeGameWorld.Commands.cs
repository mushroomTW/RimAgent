using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public CommandResult SetDrafted(int pawnId, bool drafted)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetDrafted({pawnId},{drafted})"));
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("pawn not found");
        pawn.Drafted = drafted;
        return CommandResult.Ok();
    }

    public CommandResult OrderMoveTo(int pawnId, int x, int z)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"OrderMoveTo({pawnId},{x},{z})"));
        if (UnwalkableCells.Contains((x, z))) return CommandResult.Fail("cell is not walkable");
        return Colonists.Exists(c => c.Id == pawnId) ? CommandResult.Ok() : CommandResult.Fail("pawn not found");
    }

    public Dictionary<int, string> InspectTexts { get; } = new Dictionary<int, string>();

    public string GetInspectText(int thingId) => InspectTexts.TryGetValue(thingId, out string text) ? text : string.Empty;

    public CommandResult ChooseOption(string choiceId, int option, string input = null)
    {
        Calls.Add(input == null ? string.Create(CultureInfo.InvariantCulture, $"ChooseOption({choiceId},{option})") : string.Create(CultureInfo.InvariantCulture, $"ChooseOption({choiceId},{option},{input})"));
        PendingChoiceInfo choice = PendingChoices.FirstOrDefault(c => c.Id == choiceId);
        ChoiceOptionInfo picked = choice?.Options.FirstOrDefault(o => o.Number == option);
        if (picked == null) return CommandResult.Fail("choice not found");
        PendingChoices.Remove(choice);
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
        }
        return CommandResult.Ok(picked.Text);
    }

    public IReadOnlyList<QuestInfo> GetQuests() => Quests;

    public CommandResult AcceptQuest(int questId, int rewardNumber, int accepterPawnId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"AcceptQuest({questId},{rewardNumber},{accepterPawnId})"));
        QuestInfo quest = Quests.FirstOrDefault(q => q.Id == questId);
        if (quest == null) return CommandResult.Fail("quest not found");
        quest.State = QuestInfo.ActiveState;
        quest.ExpiresInTicks = null;
        quest.AccepterName = Colonists.FirstOrDefault(c => c.Id == accepterPawnId)?.Name ?? string.Empty;
        return CommandResult.Ok(quest.Name);
    }

    public CommandResult SetQuestDismissed(int questId, bool dismissed)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetQuestDismissed({questId},{dismissed})"));
        QuestInfo quest = Quests.FirstOrDefault(q => q.Id == questId);
        if (quest == null) return CommandResult.Fail("quest not found");
        quest.Dismissed = dismissed;
        return CommandResult.Ok();
    }

    public IReadOnlyList<GizmoInfo> GetGizmos(int thingId)
    {
        if (Gizmos.TryGetValue(thingId, out List<GizmoInfo> gizmos)) return gizmos;
        return Things.Exists(t => t.Id == thingId) ? new List<GizmoInfo>() : null;
    }

    public CommandResult UseGizmo(int thingId, int gizmoNumber, string label, MapTarget target)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"UseGizmo({thingId},{gizmoNumber},{label}") + (target == null ? ")" : $",{target})"));
        GizmoInfo gizmo = GetGizmos(thingId)?.FirstOrDefault(g => g.Number == gizmoNumber);
        if (gizmo != null && !string.Equals(gizmo.Label, label, StringComparison.OrdinalIgnoreCase))
            return CommandResult.Fail("button list changed");
        if (gizmo == null) return CommandResult.Fail("no such button");
        if (gizmo.Kind == GizmoInfo.TargetKind || target != null)
        {
            if (target == null) return CommandResult.Fail("asks for a target");
            if (RejectedTargets.TryGetValue(target.ToString(), out string reason)) return CommandResult.Fail(reason);
            return CommandResult.Ok("targeted " + target);
        }
        if (gizmo.Kind == GizmoInfo.ToggleKind)
        {
            gizmo.On = !gizmo.On;
            return CommandResult.Ok(gizmo.On ? "now on" : "now off");
        }
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
        }
        return CommandResult.Ok();
    }

    public FloatMenuInfo GetFloatMenuOptions(int colonistId, MapTarget target)
    {
        ColonistInfo colonist = Colonists.FirstOrDefault(c => c.Id == colonistId);
        if (colonist == null) return new FloatMenuInfo { Error = "colonist not found" };
        if (!string.IsNullOrEmpty(FloatMenuError)) return new FloatMenuInfo { Error = FloatMenuError, PawnName = colonist.Name };
        return new FloatMenuInfo { PawnName = colonist.Name, Drafted = colonist.Drafted, TargetLabel = target.ToString(), Options = FloatMenuOptions };
    }

    public CommandResult UseFloatMenuOption(int colonistId, MapTarget target, int optionNumber, string label)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"UseFloatMenuOption({colonistId},{target},{optionNumber},{label})"));
        FloatMenuOptionInfo option = FloatMenuOptions.FirstOrDefault(o => o.Number == optionNumber);
        if (option == null || !string.Equals(option.Label, label, StringComparison.OrdinalIgnoreCase)) return CommandResult.Fail("menu changed");
        if (option.Disabled) return CommandResult.Fail("disabled");
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
        }
        return CommandResult.Ok(FloatMenuResult);
    }

    public CommandResult OrderJob(int pawnId, string job, int targetId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"OrderJob({pawnId},{job},{targetId})"));
        if (Colonists.TrueForAll(c => c.Id != pawnId)) return CommandResult.Fail("pawn not found");
        bool targetExists = Threats.Exists(t => t.Id == targetId) || Things.Exists(t => t.Id == targetId) || Colonists.Exists(c => c.Id == targetId);
        return targetExists ? CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{job} [{targetId}]")) : CommandResult.Fail("target not found on the map");
    }

    public CommandResult SetForbidden(CellRectSpec? area, string labelContains, bool forbidden)
    {
        Calls.Add($"SetForbidden({area?.ToString() ?? "map"},{labelContains},{forbidden})");
        List<ThingInfo> changed = [.. Things
            .Where(t => (t.Kind == ThingKind.Item) && t.Forbidden != forbidden && (!area.HasValue || area.Value.Contains(t.X, t.Z)) && (string.IsNullOrEmpty(labelContains) || t.Label.Contains(labelContains, StringComparison.OrdinalIgnoreCase)))];
        if (changed.Count == 0) return CommandResult.Fail("no visible item or corpse there");
        foreach (ThingInfo t in changed) t.Forbidden = forbidden;
        return CommandResult.Ok($"{changed.Count} item(s): {string.Join(", ", changed.Select(t => t.Label))}");
    }

    public CommandResult OrderAttack(int pawnId, int targetId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"OrderAttack({pawnId},{targetId})"));
        bool targetExists = Threats.Exists(t => t.Id == targetId) || Things.Exists(t => t.Id == targetId);
        return targetExists ? CommandResult.Ok() : CommandResult.Fail("target not found");
    }

    public CommandResult SetMedicalCare(int pawnId, string careDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetMedicalCare({pawnId},{careDefName})"));
        ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
        if (pawn == null) return CommandResult.Fail("pawn not found");
        if (!MedicalCareLevels.Any(d => string.Equals(d.DefName, careDefName, StringComparison.OrdinalIgnoreCase)))
            return CommandResult.Fail($"unknown medical care level {careDefName}");
        pawn.MedicalCare = careDefName;
        return CommandResult.Ok();
    }
}
