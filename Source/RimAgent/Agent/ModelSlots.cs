#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

/// <summary>Agent 以外的每一種模型呼叫（子代理、分類器、/compact 摘要）各自指定模型與思考強度；槽值的三態解析在 <see cref="RimAgentSettings.ResolveModelSlot"/>。</summary>
public static class ModelSlots
{
    /// <summary>空白模型不動 <see cref="ChatOptions.ModelId"/>（交給框架備援鏈），其餘覆寫。</summary>
    public static void Apply(ChatOptions options, string model, ReasoningLevel reasoning)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyModel(options, model);
        ApplyReasoning(options, reasoning);
    }

    /// <summary>指定了具體模型就嚴格釘選，失敗時不改走備援鏈。</summary>
    public static void ApplyModel(ChatOptions options, string model, bool pin = true)
    {
        string trimmed = model?.Trim();
        // 哨兵值不是模型名稱：呼叫端若漏了 ResolveModelSlot，也不能把 =agent／=auto 送進框架。
        if (string.IsNullOrWhiteSpace(trimmed) || RimAgentSettings.IsModelSentinel(trimmed)) return;
        options.ModelId = trimmed;
        if (pin && IsPinned(trimmed)) Mark(options, o => o.StrictModel = true);
    }

    /// <summary>是不是框架釘得住的具體模型（"供應商:模型"）；空白、哨兵值、備援鏈別名與不帶供應商的名稱都不是。</summary>
    internal static bool IsPinned(string model)
    {
        string trimmed = model?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && !RimAgentSettings.IsModelSentinel(trimmed)
            && !RimLLMChatOptions.IsFallbackModelId(trimmed)
            && trimmed.IndexOf(':', StringComparison.Ordinal) > 0;
    }

    /// <summary>
    /// 明確開啟思考時的輸出上限下限：部分供應商把思考 token 算進輸出上限，
    /// 分類器那種 150 token 的上限會在寫出答案前就被思考吃光。
    /// </summary>
    public const int ReasoningOutputFloor = 2000;

    public static void ApplyReasoning(ChatOptions options, ReasoningLevel level)
    {
        if ((level == ReasoningLevel.Low || level == ReasoningLevel.Medium || level == ReasoningLevel.High)
            && options.MaxOutputTokens.HasValue && options.MaxOutputTokens.Value < ReasoningOutputFloor)
        {
            options.MaxOutputTokens = ReasoningOutputFloor;
        }
        switch (level)
        {
            case ReasoningLevel.Off:
                Mark(options, o => o.DisableReasoning = true);
                break;
            case ReasoningLevel.Low:
                options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low };
                break;
            case ReasoningLevel.Medium:
                options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium };
                break;
            case ReasoningLevel.High:
                options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High };
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 框架的旗標（關閉思考、嚴格釘選）只從 <see cref="RimLLMChatOptions"/> 公開；一般 ChatOptions（AF 摘要、分類器）
    /// 借一個 RimLLMChatOptions 寫好旗標，再把它的 AdditionalProperties 抄過去。
    /// </summary>
    private static void Mark(ChatOptions options, Action<RimLLMChatOptions> set)
    {
        if (options is RimLLMChatOptions rim)
        {
            set(rim);
            return;
        }
        var carrier = new RimLLMChatOptions();
        set(carrier);
        if (options.AdditionalProperties == null) options.AdditionalProperties = [];
        foreach (KeyValuePair<string, object> pair in carrier.AdditionalProperties)
        {
            options.AdditionalProperties[pair.Key] = pair.Value;
        }
    }
}

/// <summary>每次呼叫重讀設定，改完下一次生效；任一項為 null 視為「不指定」：模型走呼叫端原設定或框架備援鏈、思考強度為 Auto。</summary>
public sealed class ModelRouting
{
    public Func<bool> ClassifierPinned { get; set; }
    public Func<ReasoningLevel> ClassifierReasoning { get; set; }
    public Func<string> CompactModel { get; set; }
    public Func<ReasoningLevel> CompactReasoning { get; set; }
    public Func<string, string> SubagentModel { get; set; }
    public Func<ReasoningLevel> SubagentReasoning { get; set; }
}

/// <summary>替不接受 ChatOptions 的呼叫端（AF 的摘要策略）或只設了部分選項的呼叫端（分類器）套上模型槽；每次請求重讀槽值。</summary>
internal sealed class ModelSlotChatClient : DelegatingChatClient
{
    private readonly Func<string> _model;
    private readonly Func<ReasoningLevel> _reasoning;

    public ModelSlotChatClient(IChatClient innerClient, Func<string> model, Func<ReasoningLevel> reasoning)
        : base(innerClient)
    {
        _model = model;
        _reasoning = reasoning;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(messages, Slotted(options), cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options = null,
        CancellationToken cancellationToken = default)
    {
        // 串流路徑同樣要套槽值，否則走串流的呼叫端會悄悄略過指定的模型與思考強度。
        return base.GetStreamingResponseAsync(messages, Slotted(options), cancellationToken);
    }

    private ChatOptions Slotted(ChatOptions options)
    {
        // Clone：呼叫端的 options 可能被重用（分類器每次新建，但 AF 摘要策略不保證）。
        ChatOptions slotted = options?.Clone() ?? new ChatOptions();
        ModelSlots.Apply(slotted, _model?.Invoke(), _reasoning?.Invoke() ?? ReasoningLevel.Auto);
        return slotted;
    }
}
