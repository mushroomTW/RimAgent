using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace RimAgent.Tools;

/// <summary>
/// load_toolpack：只給主 Agent，由會話建立（展開要改的是這一輪送出中的工具清單，登錄表碰不到）。
/// 不經登錄表，子代理因此不會拿到它。
/// </summary>
internal sealed class ToolPackTools
{
    public const string ToolName = "load_toolpack";

    private readonly Func<string, string> _load;

    public ToolPackTools(Func<string, string> load)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
    }

    [Description("Loads a tool pack from the tool pack index so its tools can be called from your next step. Loaded packs stay for the rest of the conversation.")]
    public string LoadToolpack([Description("Pack name from the index, e.g. \"combat\".")] string pack)
    {
        return _load(pack);
    }

    /// <summary>系統提示的包索引；只隨權限與模式改變，展開與否不影響內容。</summary>
    public static string Index(IReadOnlyList<ToolPacks.Pack> packs)
    {
        return "Tool packs: some tools stay hidden until you load their pack with " + ToolName +
               ". Load one when the task needs it; it stays loaded for the rest of the conversation.\n" +
               string.Join("\n", packs.Select(p => "- " + p.Id + ": " + p.Summary));
    }
}
