using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>徵召與戰鬥指令，以及救援、俘虜、包紮等戰後命令。LLM 有數秒延遲，只適合戰前佈署與撤退。</summary>
    public sealed class CombatTools
    {
        private static readonly string[] Jobs = { "rescue", "capture", "tend", "strip", "equip", "enter_casket" };

        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public CombatTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("draft", ToolCategory.Combat, ToolRisk.Safe)]
        [Description("Drafts or undrafts colonists. Drafted colonists stop working and take direct orders; undraft them afterwards so they go back to work. Downed colonists are skipped.")]
        public string Draft(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("true to draft, false to undraft.")] bool drafted)
        {
            Result<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            string verbPreview = drafted ? "draft" : "undraft";
            string blocked = ToolText.Blocked(_gate, $"would {verbPreview} {ToolText.Names(pawns.Value)} (downed members will be skipped).");
            if (blocked != null) return blocked;

            var done = new List<ColonistInfo>();
            var failures = new List<string>();
            foreach (ColonistInfo c in pawns.Value)
            {
                if (c.Downed)
                {
                    failures.Add($"{c.Name} is downed");
                    continue;
                }
                CommandResult r = _world.SetDrafted(c.Id, drafted);
                if (r.Success) done.Add(c);
                else failures.Add($"{c.Name}: {r.Message}");
            }

            string verb = drafted ? "Drafted" : "Undrafted";
            if (done.Count == 0) return ToolText.Error($"nobody could be {verb.ToLowerInvariant()}. " + string.Join("; ", failures));
            string summary = $"{verb} {done.Count}: {ToolText.Names(done)}.";
            return failures.Count == 0 ? summary : summary + " Skipped: " + string.Join("; ", failures);
        }

        [AgentTool("order_move", ToolCategory.Combat, ToolRisk.Safe)]
        [Description("Orders colonists to walk to a cell, drafting them first if needed. Use it for positioning and retreats.")]
        public string OrderMove(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("Destination x.")] int x,
            [Description("Destination z.")] int z)
        {
            string outOfBounds = TargetResolver.CheckCell(x, z, _world.GetMapBounds());
            if (outOfBounds != null) return outOfBounds;

            Result<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            string blocked = ToolText.Blocked(_gate, $"would order {ToolText.Names(pawns.Value)} to move to {ToolText.Cell(x, z)} (drafted first if needed).");
            if (blocked != null) return blocked;

            var moved = new List<ColonistInfo>();
            var draftedNow = new List<ColonistInfo>();
            var failures = new List<string>();
            foreach (ColonistInfo c in pawns.Value)
            {
                if (!TryPrepareColonistForCombat(c, failures, out bool newlyDrafted)) continue;
                if (newlyDrafted) draftedNow.Add(c);

                CommandResult r = _world.OrderMoveTo(c.Id, x, z);
                if (r.Success) moved.Add(c);
                else failures.Add($"{c.Name}: {r.Message}");
            }

            if (moved.Count == 0) return ToolText.Error($"nobody could move to {ToolText.Cell(x, z)}. " + string.Join("; ", failures));
            string summary = $"Ordered {ToolText.Names(moved)} to move to {ToolText.Cell(x, z)}.";
            if (draftedNow.Count > 0) summary += $" Drafted first: {ToolText.Names(draftedNow)}.";
            return failures.Count == 0 ? summary : summary + " Skipped: " + string.Join("; ", failures);
        }

        [AgentTool("order_attack", ToolCategory.Combat, ToolRisk.Confirm)]
        [Description("Orders colonists to attack one target by id, drafting them first if needed. Hostiles come from list_threats, wild animals from find_things; colony members, prisoners and friendly-faction pawns are refused.")]
        public string OrderAttack(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("Target id from list_threats or find_things.")] int targetId)
        {
            string targetLabel = ResolveTargetLabel(targetId);
            if (targetLabel == null)
            {
                return ToolText.Error($"no target with id {targetId} on the map. Use list_threats or find_things to get a valid id.");
            }

            Result<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            string attackBlocked = ToolText.Blocked(_gate, $"would order {ToolText.Names(pawns.Value)} to attack {targetLabel} [{targetId}] (drafted first if needed).");
            if (attackBlocked != null) return attackBlocked;

            var attacking = new List<ColonistInfo>();
            var failures = new List<string>();
            foreach (ColonistInfo c in pawns.Value)
            {
                if (!TryPrepareColonistForCombat(c, failures, out _)) continue;

                CommandResult r = _world.OrderAttack(c.Id, targetId);
                if (r.Success) attacking.Add(c);
                else failures.Add($"{c.Name}: {r.Message}");
            }

            if (attacking.Count == 0) return ToolText.Error($"nobody could attack {targetLabel} [{targetId}]. " + string.Join("; ", failures));
            string summary = $"Ordered {ToolText.Names(attacking)} to attack {targetLabel} [{targetId}].";
            return failures.Count == 0 ? summary : summary + " Skipped: " + string.Join("; ", failures);
        }

        [AgentTool("order_job", ToolCategory.Combat, ToolRisk.Safe)]
        [Description("Orders one colonist to do one job now, like the in-game right-click menu: rescue (carry a downed non-hostile pawn to a bed, or help a stranded refugee who joins once helped), capture (carry a downed hostile to a prisoner bed), tend (treat wounds with the best reachable medicine), strip (take the gear off a corpse or downed hostile), equip (pick up a weapon) or enter_casket (climb into an empty cryptosleep casket; ship launch needs its caskets occupied, and the casket's eject gizmo brings them out). Works drafted or not. Targets come from list_threats, find_things or list_colonists.")]
        public string OrderJob(
            [Description("Colonist name, nickname or id.")] string colonist,
            [Description("rescue, capture, tend, strip, equip or enter_casket.")] string job,
            [Description("Target id from list_threats, find_things or list_colonists.")] int targetId)
        {
            string kind = job?.Trim().ToLowerInvariant() ?? string.Empty;
            if (Array.IndexOf(Jobs, kind) < 0) return ToolText.Error($"unknown job \"{job}\". Use rescue, capture, tend, strip, equip or enter_casket.");
            Result<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
            if (!pawn.Success) return pawn.Error;

            string blocked = ToolText.Blocked(_gate, $"would order {pawn.Value.Name} to {kind} [{targetId}].");
            if (blocked != null) return blocked;

            CommandResult r = _world.OrderJob(pawn.Value.Id, kind, targetId);
            if (!r.Success) return ToolText.Error($"{pawn.Value.Name} cannot {kind} [{targetId}]: {r.Message}");
            return $"Ordered {pawn.Value.Name} to {kind} [{targetId}]: {r.Message}.";
        }

        private string ResolveTargetLabel(int targetId)
        {
            ThreatInfo threat = _world.GetThreats().FirstOrDefault(t => t.Id == targetId);
            if (threat != null) return threat.Label;
            AnimalInfo animal = _world.GetAnimals().FirstOrDefault(a => a.Id == targetId);
            if (animal != null) return animal.Name;
            ThingInfo thing = _world.FindThings(new ThingQuery { Kind = ThingKind.Pawn, Limit = 200 }).FirstOrDefault(t => t.Id == targetId);
            return thing?.Label;
        }

        private bool TryPrepareColonistForCombat(ColonistInfo c, List<string> failures, out bool draftedNow)
        {
            draftedNow = false;
            if (c.Downed)
            {
                failures.Add($"{c.Name} is downed");
                return false;
            }
            if (!c.Drafted)
            {
                CommandResult draft = _world.SetDrafted(c.Id, true);
                if (!draft.Success)
                {
                    failures.Add($"{c.Name}: {draft.Message}");
                    return false;
                }
                draftedNow = true;
            }
            return true;
        }
    }
}
