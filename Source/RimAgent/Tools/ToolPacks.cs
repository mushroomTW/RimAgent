using System;
using System.Collections.Generic;

namespace RimAgent.Tools;

/// <summary>
/// 按需揭露的工具包：包內工具照常建立、照常受權限與模式過濾，但不放進主 Agent 的工具清單，
/// 模型看到系統提示的包索引，需要時以 load_toolpack 展開。工具 schema 每次模型呼叫都要重送，
/// 低頻工具收起來能省下大部分固定開銷。
/// 成員在編譯期固定：可由設定或模型更動的話，等於能把工具移出審查過的範圍。
/// 子代理不套用：它們的工具已依宣告的類別限縮。
/// </summary>
internal static class ToolPacks
{
    public sealed class Pack
    {
        public Pack(string id, string summary)
        {
            Id = id;
            Summary = summary;
        }

        public string Id { get; }
        public string Summary { get; }
    }

    /// <summary>包的順序即系統提示索引的順序。</summary>
    public static readonly IReadOnlyList<Pack> All = new[]
    {
        new Pack("combat", "draft colonists, move them, attack and force jobs"),
        new Pack("medical", "injuries, diseases and health conditions (get_health), medical care levels and surgery"),
        new Pack("zone", "stockpiles and growing zones, storage filters"),
        new Pack("production", "production bills"),
        new Pack("animal", "list, tame and train animals, animal areas"),
        new Pack("trade", "traders, trading, caravans and the world map (travel, settle)"),
        new Pack("prisoner", "prisoners: interaction mode and release"),
        new Pack("memory_edit", "write, edit and delete memory files (reading is always available)"),
        new Pack("mcp", "list and call tools on configured MCP servers")
    };

    private static readonly Dictionary<ToolCategory, string> ByCategory = new()
    {
        [ToolCategory.Combat] = "combat",
        [ToolCategory.Medical] = "medical",
        [ToolCategory.Zone] = "zone",
        [ToolCategory.Production] = "production",
        [ToolCategory.Animal] = "animal",
        [ToolCategory.Trade] = "trade",
        [ToolCategory.Prisoner] = "prisoner",
        [ToolCategory.Mcp] = "mcp"
    };

    /// <summary>記憶只收寫入類：讀取工具常駐，系統提示的記憶索引要求模型先讀偏好再行動。</summary>
    private static readonly HashSet<string> MemoryEditTools = new(StringComparer.Ordinal)
    {
        "file_memory_write", "file_memory_delete", "file_memory_replace", "file_memory_replace_lines"
    };

    /// <summary>工具所屬的包；常駐工具回傳 null。</summary>
    public static string PackOf(string toolName, ToolCategory category)
    {
        if (MemoryEditTools.Contains(toolName)) return "memory_edit";
        return ByCategory.TryGetValue(category, out string pack) ? pack : null;
    }
}
