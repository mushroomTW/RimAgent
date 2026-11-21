namespace RimAgent.Agent;

/// <summary>Auto 不設定、沿用 RimLLM Framework 的預設；Off 對應框架的 DisableReasoning；其餘映射到 MEAI 的 ReasoningEffort。</summary>
public enum ReasoningLevel
{
    Auto,
    Off,
    Low,
    Medium,
    High
}
