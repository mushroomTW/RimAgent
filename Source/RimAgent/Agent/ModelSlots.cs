using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

/// <summary>
/// 模型槽：Agent 以外的每一種模型呼叫（子代理、分類器、/compact 摘要）各自指定模型與思考強度。
/// 這裡只負責把已解析的值寫進 ChatOptions；槽值的三態解析在 <see cref="RimAgentSettings.ResolveModelSlot"/>。
/// </summary>
public static class ModelSlots
{
    /// <summary>空白模型不動 <see cref="ChatOptions.ModelId"/>（交給框架備援鏈），其餘覆寫。</summary>
    public static void Apply(ChatOptions options, string model, ReasoningLevel reasoning)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        string trimmed = model?.Trim();
        // 哨兵值不是模型名稱：呼叫端若漏了 ResolveModelSlot，也不能把 =agent／=auto 送進框架。
        if (!string.IsNullOrWhiteSpace(trimmed) && !RimAgentSettings.IsModelSentinel(trimmed))
        {
            options.ModelId = trimmed;
        }
        ApplyReasoning(options, reasoning);
    }

    /// <summary>
    /// 明確開啟思考時的輸出上限下限：部分供應商把思考 token 算進輸出上限，
    /// 分類器那種 150 token 的上限會在寫出答案前就被思考吃光。
    /// </summary>
    public const int ReasoningOutputFloor = 2000;

    /// <summary>Auto 什麼都不設（框架補預設）；Off 走框架的 DisableReasoning；其餘交給 MEAI 的 ReasoningEffort。</summary>
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
                DisableReasoning(options);
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
    /// 框架的關閉思考旗標只從 <see cref="RimLLMChatOptions"/> 公開；一般 ChatOptions（AF 摘要、分類器）
    /// 借一個 RimLLMChatOptions 寫好旗標，再把它的 AdditionalProperties 抄過去。
    /// </summary>
    private static void DisableReasoning(ChatOptions options)
    {
        if (options is RimLLMChatOptions rim)
        {
            rim.DisableReasoning = true;
            return;
        }
        var carrier = new RimLLMChatOptions { DisableReasoning = true };
        if (options.AdditionalProperties == null) options.AdditionalProperties = [];
        foreach (KeyValuePair<string, object> pair in carrier.AdditionalProperties)
        {
            options.AdditionalProperties[pair.Key] = pair.Value;
        }
    }
}

/// <summary>
/// 交給 <see cref="ColonyAgentSession"/> 的模型槽讀取器；每次呼叫重讀設定，改完下一次生效。
/// 任一項為 null 視為「不指定」：模型走呼叫端原設定或框架備援鏈、思考強度為 Auto。
/// </summary>
public sealed class ModelRouting
{
    public Func<ReasoningLevel> ClassifierReasoning { get; set; }
    public Func<string> CompactModel { get; set; }
    public Func<ReasoningLevel> CompactReasoning { get; set; }
    /// <summary>子代理名稱 → 已解析的模型識別碼。</summary>
    public Func<string, string> SubagentModel { get; set; }
    public Func<ReasoningLevel> SubagentReasoning { get; set; }
}

/// <summary>
/// 替不接受 ChatOptions 的呼叫端（AF 的摘要策略）或只設了部分選項的呼叫端（分類器）套上模型槽。
/// 每次請求重讀槽值，設定頁改完下一次呼叫生效。
/// </summary>
internal sealed class ModelSlotChatClient : DelegatingChatClient
{
    private readonly Func<string> _model;
    private readonly Func<ReasoningLevel> _reasoning;

    /// <summary>初始化模型槽包裝 Client。</summary>
    /// <param name="innerClient">底層實際執行的 IChatClient。</param>
    /// <param name="model">已解析的模型識別碼；null 或回傳空白則保留呼叫端的 ModelId。</param>
    /// <param name="reasoning">思考強度；null 視為 Auto。</param>
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
        // Clone：呼叫端的 options 可能被重用（分類器每次新建，但 AF 摘要策略不保證）。
        ChatOptions slotted = options?.Clone() ?? new ChatOptions();
        ModelSlots.Apply(slotted, _model?.Invoke(), _reasoning?.Invoke() ?? ReasoningLevel.Auto);
        return base.GetResponseAsync(messages, slotted, cancellationToken);
    }
}
