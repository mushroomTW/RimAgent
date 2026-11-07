using System;
using System.Collections.Generic;
using Verse;

namespace RimAgent.Agent;

/// <summary>一個分流類別：說明是決策模型看的判準，思考強度是這一類要用的。</summary>
public sealed class DecisionRouteCategory : IExposable
{
    public string id = string.Empty;          // 穩定識別碼
    public string name = string.Empty;        // 顯示名稱
    public string description = string.Empty; // 判準：這一類包含什麼、不包含什麼
    public ReasoningLevel reasoning = ReasoningLevel.Auto;

    public DecisionRouteCategory() { }

    public DecisionRouteCategory(
        string id,
        string name,
        string description,
        ReasoningLevel reasoning = ReasoningLevel.Auto)
    {
        this.id = id ?? string.Empty;
        this.name = name ?? string.Empty;
        this.description = description ?? string.Empty;
        this.reasoning = reasoning;
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref id, "id", string.Empty);
        Scribe_Values.Look(ref name, "name", string.Empty);
        Scribe_Values.Look(ref description, "description", string.Empty);
        Scribe_Values.Look(ref reasoning, "reasoning", ReasoningLevel.Auto);
    }

    /// <summary>建立內建的四個決策分流類別。</summary>
    public static List<DecisionRouteCategory> CreateDefaultCategories()
    {
        return
        [
            new DecisionRouteCategory(
                "observe",
                "查詢解釋",
                "Read and explain current colony state without modifying the colony. Includes status checks, pawn inspections, resource/item queries, threat reviews, advice, and summaries. Excludes making changes, issuing commands, designating jobs, or altering any game settings."),
            new DecisionRouteCategory(
                "routine",
                "日常維護",
                "Routine, small-scale, and reversible colony upkeep. Includes work priorities, schedules, drafting, zoning, medical care, cleaning, firefighting, and basic colonist orders. Excludes major construction, resource-heavy bills, combat tactics, diplomacy, or high-stakes irrevocable actions."),
            new DecisionRouteCategory(
                "build",
                "建造生產",
                "Construction, deconstruction, crafting production bills, and zone or storage configuration. Includes placing or canceling blueprints, dismantling structures, managing stockpile filters, and recipe bills. Excludes pure queries and high-stakes survival decisions."),
            new DecisionRouteCategory(
                "risky",
                "高風險規劃",
                "High-risk, costly, or irreversible strategic decisions. Includes trading deals, medical surgeries, prisoner interactions (execution/release/organ extraction), caravan formation, combat orders, and security or global policy choices. Excludes safe status queries or routine daily upkeep.")
        ];
    }

    public static bool IsBuiltIn(string id)
    {
        return id is "observe" or "routine" or "build" or "risky";
    }
}
