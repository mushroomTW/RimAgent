using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>囚犯：查詢、互動模式切換、立即釋放。招募走獄卒原生機制，不硬轉派系。</summary>
    public sealed class PrisonerTools
    {
        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public PrisonerTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("list_prisoners", ToolCategory.Prisoner, ToolRisk.Query)]
        [Description("Lists prisoners with id, remaining resistance, recruitability, current interaction mode and health. Check it before set_prisoner_interaction or release_prisoner.")]
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

        [AgentTool("set_prisoner_interaction", ToolCategory.Prisoner, ToolRisk.Confirm)]
        [Description("Sets how wardens treat a prisoner: recruit, release, execution and the game's other modes. The outcome then unfolds over time through warden visits; release_prisoner releases at once instead.")]
        public string SetPrisonerInteraction(
            [Description("Prisoner name, nickname or id.")] string prisoner,
            [Description("Interaction mode, e.g. \"recruit\", \"release\" or \"execution\".")] string mode)
        {
            ResolveResult<PrisonerInfo> pawn = ResolvePrisoner(prisoner, _world.GetPrisoners());
            if (!pawn.Success) return pawn.Error;

            ResolveResult<DefInfo> def = DefResolver.Resolve(mode, _world.PrisonerInteractionModes, "prisoner interaction mode");
            if (!def.Success) return def.Error;

            string previous = string.IsNullOrEmpty(pawn.Value.InteractionModeLabel) ? pawn.Value.InteractionMode : pawn.Value.InteractionModeLabel;
            string blocked = ToolText.Blocked(_gate, $"would set {DisplayName(pawn.Value)}'s interaction {previous} -> {def.Value.Label}.");
            if (blocked != null) return blocked;

            CommandResult result = _world.SetPrisonerInteraction(pawn.Value.Id, def.Value.DefName);
            if (!result.Success) return ToolText.Error(result.Message);

            return $"Set {DisplayName(pawn.Value)}'s interaction {previous} -> {def.Value.Label}.";
        }

        [AgentTool("release_prisoner", ToolCategory.Prisoner, ToolRisk.Confirm)]
        [Description("Releases a prisoner right now, as the game's release action does; they leave the colony for good.")]
        public string ReleasePrisoner(
            [Description("Prisoner name, nickname or id.")] string prisoner)
        {
            ResolveResult<PrisonerInfo> pawn = ResolvePrisoner(prisoner, _world.GetPrisoners());
            if (!pawn.Success) return pawn.Error;

            string blocked = ToolText.Blocked(_gate, $"would release {DisplayName(pawn.Value)} [{pawn.Value.Id}]; they leave the colony for good.");
            if (blocked != null) return blocked;

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
            return TargetResolver.ResolveEntity(
                input,
                prisoners,
                p => p.Id,
                Names,
                p => $"[{p.Id}] {p.FullName}",
                "prisoner");
        }

        private static IEnumerable<string> Names(PrisonerInfo p)
        {
            if (!string.IsNullOrEmpty(p.Name)) yield return p.Name;
            if (!string.IsNullOrEmpty(p.Nickname)) yield return p.Nickname;
            if (!string.IsNullOrEmpty(p.FullName)) yield return p.FullName;
        }
    }
}
