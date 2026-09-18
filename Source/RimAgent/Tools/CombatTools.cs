using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>徵召與戰鬥指令。LLM 有數秒延遲，只適合戰前佈署與撤退。</summary>
    public sealed class CombatTools
    {
        private readonly IGameWorld _world;
        private readonly ICommandGate _gate;

        public CombatTools(IGameWorld world) : this(world, null)
        {
        }

        public CombatTools(IGameWorld world, ICommandGate gate)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("draft", ToolCategory.Combat, ToolRisk.Safe)]
        [Description("Drafts or undrafts colonists. Drafted colonists stop working and follow direct orders; undraft them afterwards so they return to work.")]
        public string Draft(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("true to draft, false to undraft.")] bool drafted)
        {
            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            if (_gate != null && !_gate.AllowCommands())
            {
                string verbPreview = drafted ? "draft" : "undraft";
                string draftPreview = $"would {verbPreview} {ToolText.Names(pawns.Value)} (downed members will be skipped).";
                string draftBlocked = ToolText.Confirmation(draftPreview);
                _gate.NoteBlocked(draftBlocked);
                return draftBlocked;
            }

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
        [Description("Orders colonists to walk to a map cell. Colonists are drafted automatically if needed. Remember the model's latency: use this for positioning and retreats, not real-time dodging.")]
        public string OrderMove(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("Destination x.")] int x,
            [Description("Destination z.")] int z)
        {
            string outOfBounds = TargetResolver.CheckCell(x, z, _world.GetMapBounds());
            if (outOfBounds != null) return outOfBounds;

            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            if (_gate != null && !_gate.AllowCommands())
            {
                string movePreview = $"would order {ToolText.Names(pawns.Value)} to move to {ToolText.Cell(x, z)} (drafted first if needed).";
                string moveBlocked = ToolText.Confirmation(movePreview);
                _gate.NoteBlocked(moveBlocked);
                return moveBlocked;
            }

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
        [Description("Orders colonists to attack one hostile target by its id from list_threats. Colonists are drafted automatically if needed. Colony members, prisoners and members of friendly factions are refused; wild animals can be targeted.")]
        public string OrderAttack(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("Id of the target, e.g. a hostile pawn from list_threats.")] int targetId)
        {
            string targetLabel = ResolveTargetLabel(targetId);
            if (targetLabel == null)
            {
                return ToolText.Error($"no target with id {targetId} on the map. Use list_threats or find_things to get a valid id.");
            }

            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            if (_gate != null && !_gate.AllowCommands())
            {
                string attackPreview = $"would order {ToolText.Names(pawns.Value)} to attack {targetLabel} [{targetId}] (drafted first if needed).";
                string attackBlocked = ToolText.Confirmation(attackPreview);
                _gate.NoteBlocked(attackBlocked);
                return attackBlocked;
            }

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

        private string ResolveTargetLabel(int targetId)
        {
            ThreatInfo threat = _world.GetThreats().FirstOrDefault(t => t.Id == targetId);
            if (threat != null) return threat.Label;
            ThingInfo thing = _world.FindThings(new ThingQuery { Kind = ThingKind.Pawn, Limit = 500 }).FirstOrDefault(t => t.Id == targetId);
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
