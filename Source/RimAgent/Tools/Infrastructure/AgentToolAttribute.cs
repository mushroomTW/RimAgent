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
    Agents,
    /// <summary>存檔與讀檔重來；子代理永遠拿不到。</summary>
    Save
}

/// <summary>
/// 工具的靜態風險，依可逆性分級。確認模式只放行 Query；執行模式擋 Confirm；
/// 自動模式由分類器逐次判斷（不看此標記）。
/// </summary>
public enum ToolRisk
{
    Query,
    /// <summary>會改遊戲狀態，但改回來就好（優先級、作息、徵召、設定值）。</summary>
    Safe,
    /// <summary>結果不可逆或代價高（交易、離圖、釋放、手術、刪除、拆除、攻擊）。</summary>
    Confirm
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class AgentToolAttribute : Attribute
{
    public AgentToolAttribute(string name, ToolCategory category, ToolRisk risk)
    {
        Name = name;
        Category = category;
        Risk = risk;
    }

    public string Name { get; }
    public ToolCategory Category { get; }
    public ToolRisk Risk { get; }
}
