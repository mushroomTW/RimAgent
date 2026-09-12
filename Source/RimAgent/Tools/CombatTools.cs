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

        public CombatTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("draft", ToolCategory.Combat)]
        [Description("Drafts or undrafts colonists. Drafted colonists stop working and follow direct orders; undraft them afterwards so they return to work.")]
        public string Draft(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("true to draft, false to undraft.")] bool drafted)
        {
            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

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

        [AgentTool("order_move", ToolCategory.Combat)]
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

            var moved = new List<ColonistInfo>();
            var draftedNow = new List<ColonistInfo>();
            var failures = new List<string>();
            foreach (ColonistInfo c in pawns.Value)
            {
                if (c.Downed)
                {
                    failures.Add($"{c.Name} is downed");
                    continue;
                }
                if (!c.Drafted)
                {
                    CommandResult draft = _world.SetDrafted(c.Id, true);
                    if (!draft.Success)
                    {
                        failures.Add($"{c.Name}: {draft.Message}");
                        continue;
                    }
                    draftedNow.Add(c);
                }
                CommandResult r = _world.OrderMoveTo(c.Id, x, z);
                if (r.Success) moved.Add(c);
                else failures.Add($"{c.Name}: {r.Message}");
            }

            if (moved.Count == 0) return ToolText.Error($"nobody could move to {ToolText.Cell(x, z)}. " + string.Join("; ", failures));
            string summary = $"Ordered {ToolText.Names(moved)} to move to {ToolText.Cell(x, z)}.";
            if (draftedNow.Count > 0) summary += $" Drafted first: {ToolText.Names(draftedNow)}.";
            return failures.Count == 0 ? summary : summary + " Skipped: " + string.Join("; ", failures);
        }

        [AgentTool("order_attack", ToolCategory.Combat)]
        [Description("Orders colonists to attack one target by its id (from list_threats or find_things). Colonists are drafted automatically if needed.")]
        public string OrderAttack(
            [Description("Colonist names or ids separated by commas, or \"all\".")] string colonists,
            [Description("Id of the target, e.g. a hostile pawn from list_threats.")] int targetId)
        {
            ThreatInfo threat = _world.GetThreats().FirstOrDefault(t => t.Id == targetId);
            ThingInfo thing = threat == null
                ? _world.FindThings(new ThingQuery { Kind = ThingKind.Pawn, Limit = 500 }).FirstOrDefault(t => t.Id == targetId)
                : null;
            if (threat == null && thing == null)
            {
                return ToolText.Error($"no target with id {targetId} on the map. Use list_threats or find_things to get a valid id.");
            }
            string targetLabel = threat != null ? threat.Label : thing.Label;

            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            var attacking = new List<ColonistInfo>();
            var failures = new List<string>();
            foreach (ColonistInfo c in pawns.Value)
            {
                if (c.Downed)
                {
                    failures.Add($"{c.Name} is downed");
                    continue;
                }
                if (!c.Drafted)
                {
                    CommandResult draft = _world.SetDrafted(c.Id, true);
                    if (!draft.Success)
                    {
                        failures.Add($"{c.Name}: {draft.Message}");
                        continue;
                    }
                }
                CommandResult r = _world.OrderAttack(c.Id, targetId);
                if (r.Success) attacking.Add(c);
                else failures.Add($"{c.Name}: {r.Message}");
            }

            if (attacking.Count == 0) return ToolText.Error($"nobody could attack {targetLabel} [{targetId}]. " + string.Join("; ", failures));
            string summary = $"Ordered {ToolText.Names(attacking)} to attack {targetLabel} [{targetId}].";
            return failures.Count == 0 ? summary : summary + " Skipped: " + string.Join("; ", failures);
        }
    }
}
