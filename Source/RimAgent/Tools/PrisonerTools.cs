using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>囚犯：查詢、互動模式切換、立即釋放。招募走獄卒原生機制，不硬轉派系。</summary>
    public sealed class PrisonerTools
    {
        private readonly IGameWorld _world;

        public PrisonerTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("list_prisoners", ToolCategory.Prisoner)]
        [Description("Lists the colony's prisoners: remaining resistance, recruitability, interaction mode and health. Use this before changing how a prisoner is treated.")]
        public string ListPrisoners()
        {
            List<PrisonerInfo> prisoners = _world.GetPrisoners().ToList();
            if (prisoners.Count == 0) return "There are no prisoners in the colony.";

            var sb = new StringBuilder();
            sb.Append(prisoners.Count == 1 ? "1 prisoner:" : prisoners.Count + " prisoners:").AppendLine();
            foreach (PrisonerInfo p in prisoners)
            {
                sb.Append("[").Append(p.Id).Append("] ").Append(DisplayName(p));
                sb.Append(" - resistance ").Append(FormatResistance(p.Resistance));
                sb.Append(p.Recruitable ? ", recruitable" : ", not recruitable");
                sb.Append(", mode: ").Append(string.IsNullOrEmpty(p.InteractionModeLabel) ? p.InteractionMode : p.InteractionModeLabel);
                sb.Append(" (health ").Append(ToolText.Pct(p.HealthPercent)).Append(')');
                if (p.Downed) sb.Append(" DOWNED");
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        [AgentTool("set_prisoner_interaction", ToolCategory.Prisoner)]
        [Description("Sets how wardens treat a prisoner: recruit, release, execution and other modes from the game. Recruitment itself still happens through the game's warden chats over time, not instantly.")]
        public string SetPrisonerInteraction(
            [Description("Prisoner name, nickname or id.")] string prisoner,
            [Description("Interaction mode, e.g. \"recruit\", \"release\" or \"execution\".")] string mode)
        {
            ResolveResult<PrisonerInfo> pawn = ResolvePrisoner(prisoner, _world.GetPrisoners());
            if (!pawn.Success) return pawn.Error;

            ResolveResult<DefInfo> def = DefResolver.Resolve(mode, _world.PrisonerInteractionModes, "prisoner interaction mode");
            if (!def.Success) return def.Error;

            string previous = string.IsNullOrEmpty(pawn.Value.InteractionModeLabel) ? pawn.Value.InteractionMode : pawn.Value.InteractionModeLabel;
            CommandResult result = _world.SetPrisonerInteraction(pawn.Value.Id, def.Value.DefName);
            if (!result.Success) return ToolText.Error(result.Message);

            return $"Set {DisplayName(pawn.Value)}'s interaction {previous} -> {def.Value.Label}.";
        }

        [AgentTool("release_prisoner", ToolCategory.Prisoner)]
        [Description("Releases a prisoner immediately, the same as the game's release action. They leave the colony for good.")]
        public string ReleasePrisoner(
            [Description("Prisoner name, nickname or id.")] string prisoner)
        {
            ResolveResult<PrisonerInfo> pawn = ResolvePrisoner(prisoner, _world.GetPrisoners());
            if (!pawn.Success) return pawn.Error;

            CommandResult result = _world.ReleasePrisoner(pawn.Value.Id);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        private static string DisplayName(PrisonerInfo p)
        {
            return string.IsNullOrEmpty(p.Name) ? p.FullName : p.Name;
        }

        private static string FormatResistance(float resistance)
        {
            return resistance < 0f ? "none" : ToolText.Num(resistance);
        }

        /// <summary>囚犯解析：id、全名、綽號、短名或其前綴；大小寫不敏感，歧義即報錯。</summary>
        internal static ResolveResult<PrisonerInfo> ResolvePrisoner(string input, IReadOnlyList<PrisonerInfo> prisoners)
        {
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return ResolveResult<PrisonerInfo>.Fail("Error: prisoner is empty. Give a name or id.");
            }

            if (int.TryParse(needle, out int id))
            {
                PrisonerInfo byId = prisoners.FirstOrDefault(p => p.Id == id);
                return byId != null
                    ? ResolveResult<PrisonerInfo>.Ok(byId)
                    : ResolveResult<PrisonerInfo>.Fail($"Error: no prisoner has id {id}. {ListAll(prisoners)}");
            }

            List<PrisonerInfo> exact = prisoners.Where(p => Names(p).Any(n => Eq(n, needle))).ToList();
            if (exact.Count == 1) return ResolveResult<PrisonerInfo>.Ok(exact[0]);
            if (exact.Count > 1) return Ambiguous(needle, exact);

            List<PrisonerInfo> prefix = prisoners.Where(p => Names(p).Any(n => n.StartsWith(needle, StringComparison.OrdinalIgnoreCase))).ToList();
            if (prefix.Count == 1) return ResolveResult<PrisonerInfo>.Ok(prefix[0]);
            if (prefix.Count > 1) return Ambiguous(needle, prefix);

            List<PrisonerInfo> contains = prisoners.Where(p => Names(p).Any(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (contains.Count == 1) return ResolveResult<PrisonerInfo>.Ok(contains[0]);
            if (contains.Count > 1) return Ambiguous(needle, contains);

            return ResolveResult<PrisonerInfo>.Fail($"Error: no prisoner matches \"{needle}\". {ListAll(prisoners)}");
        }

        private static IEnumerable<string> Names(PrisonerInfo p)
        {
            if (!string.IsNullOrEmpty(p.Name)) yield return p.Name;
            if (!string.IsNullOrEmpty(p.Nickname)) yield return p.Nickname;
            if (!string.IsNullOrEmpty(p.FullName)) yield return p.FullName;
        }

        private static ResolveResult<PrisonerInfo> Ambiguous(string needle, List<PrisonerInfo> matches)
        {
            return ResolveResult<PrisonerInfo>.Fail(
                $"Error: \"{needle}\" matches several prisoners: {string.Join(", ", matches.Select(m => $"[{m.Id}] {m.FullName}"))}. Use the id.");
        }

        private static string ListAll(IReadOnlyList<PrisonerInfo> prisoners)
        {
            return prisoners.Count == 0
                ? "There are no prisoners."
                : "Prisoners: " + string.Join(", ", prisoners.Select(m => $"[{m.Id}] {m.FullName}")) + ".";
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
