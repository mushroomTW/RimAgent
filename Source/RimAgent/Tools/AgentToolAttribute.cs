using System;

namespace RimAgent.Tools;

public enum ToolCategory
{
    Observe,
    Work,
    Build,
    Combat,
    Trade,
    Medical,
    Prisoner,
    Animal,
    Zone,
    Production,
    Time,
    Mcp,
    Web,
    Memory,
    /// <summary>委派子代理（delegate_task）；子代理自己的工具仍各依其類別過濾。</summary>
    Agents
}

/// <summary>
/// 工具的靜態風險，依可逆性分級。確認模式只放行 Query；執行模式擋 Confirm；
/// 自動模式由分類器逐次判斷（不看此標記），黑名單工具除外（直接拒絕，不問分類器）。
/// </summary>
public enum ToolRisk
{
    /// <summary>只讀，不改遊戲狀態。</summary>
    Query,
    /// <summary>會改遊戲狀態，但改回來就好（優先級、作息、徵召、設定值）。</summary>
    Safe,
    /// <summary>結果不可逆或代價高（交易、離圖、釋放、手術、刪除、拆除、攻擊）。</summary>
    Confirm
}

/// <summary>
/// 標記一個公開方法為 agent 工具。<see cref="AgentToolRegistry"/> 以反射收集，
/// 工具說明與參數說明則交給 <see cref="System.ComponentModel.DescriptionAttribute"/>，
/// 由 MEAI 的 AIFunctionFactory 讀進 schema。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class AgentToolAttribute : Attribute
{
    public AgentToolAttribute(string name, ToolCategory category, ToolRisk risk)
    {
        Name = name;
        Category = category;
        Risk = risk;
    }

    /// <summary>送給模型的工具名稱（snake_case）。</summary>
    public string Name { get; }
    public ToolCategory Category { get; }
    public ToolRisk Risk { get; }
}
