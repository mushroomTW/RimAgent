using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public ResearchInfo GetResearch()
    {
        ResearchProjectDef current = Find.ResearchManager.GetProject();
        return new ResearchInfo
        {
            Current = current == null ? null : ToResearch(current),
            Available = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(r => !r.IsFinished && r.CanStartNow)
                .OrderBy(r => r.CostApparent)
                .Select(ToResearch)
                .ToList()
        };
    }

    /// <summary>
    /// 尚未完成的研發目錄。不快取：研究完成後目錄即變，
    /// 每次都重讀 DefDatabase（約百來個 def，可接受）。
    /// </summary>
    [SuppressMessage("Major Code Smell", "S2365:Properties should not copy collections", Justification = "Implements IGameWorld property contract which reflects dynamic game state")]
    public IReadOnlyList<DefInfo> ResearchProjects =>
        DefDatabase<ResearchProjectDef>.AllDefsListForReading
            .Where(r => !r.IsFinished)
            .Select(r => new DefInfo(r.defName, r.label))
            .ToList();

    [SuppressMessage("Major Code Smell", "S2365:Properties should not copy collections", Justification = "Implements IGameWorld property contract which reflects dynamic game state")]
    public IReadOnlyList<DefInfo> FinishedResearchProjects =>
        DefDatabase<ResearchProjectDef>.AllDefsListForReading
            .Where(r => r.IsFinished)
            .Select(r => new DefInfo(r.defName, r.label))
            .ToList();

    public CommandResult SetResearch(string researchDefName)
    {
        string want = (researchDefName ?? string.Empty).Trim();
        if (want.Length == 0 || string.Equals(want, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(want, "stop", StringComparison.OrdinalIgnoreCase))
        {
            ResearchProjectDef current = Find.ResearchManager.GetProject();
            if (current == null) return CommandResult.Ok("No research is running.");
            Find.ResearchManager.StopProject(current);
            return CommandResult.Ok($"Stopped research {current.label}.");
        }
        ResearchProjectDef def = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(want);
        if (def == null) return CommandResult.Fail($"unknown research {want}");
        if (def.IsFinished) return CommandResult.Fail($"{def.label} is already finished");
        if (!def.CanStartNow) return CommandResult.Fail($"{def.label} cannot start yet: finish its prerequisites first");
        if (Find.ResearchManager.GetProject() == def) return CommandResult.Ok($"Research {def.label} is already running.");
        Find.ResearchManager.SetCurrentProject(def);
        return CommandResult.Ok($"Research switched to {def.label}.");
    }

    private static ResearchProjectInfo ToResearch(ResearchProjectDef def)
    {
        return new ResearchProjectInfo
        {
            DefName = def.defName,
            Label = def.label,
            ProgressPercent = def.ProgressPercent,
            Cost = def.CostApparent
        };
    }
}
