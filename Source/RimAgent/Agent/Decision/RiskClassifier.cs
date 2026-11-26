#nullable disable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent;

public sealed class RiskQuestion
{
    public string Instruction { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
}

public sealed class RiskVerdict
{
    /// <summary>動作本身是否高風險（難以復原、影響面大、花費多）；分類器失敗時為 true（fail-closed）。</summary>
    public bool Risky { get; set; }
    /// <summary>玩家這句指令是否明確涵蓋這個動作；不確定時為 false（fail-closed）。</summary>
    public bool Authorized { get; set; }
    public string Reason { get; set; } = string.Empty;
    /// <summary>分類器本身出錯（網路、逾時、回覆解析不了），不是模型判定。</summary>
    public bool Failed { get; set; }
    /// <summary>判定來源：llm／decision／fallback。</summary>
    public string Source { get; set; } = "llm";

    /// <summary>只有「低風險且玩家明確要求」才執行；其餘由自動模式拒絕。</summary>
    public bool ShouldReject => Failed || Risky || !Authorized;
}

/// <summary>兩軸分開問：動作本身危不危險（low／high），以及玩家這句話有沒有要它做這件事（authorized）；只問風險會放掉「順手多做一件小事」。任何失敗都當高風險並拒絕該命令；缺系統提示詞（AgentPack/Prompts/RiskClassifier.md）時直接回 fail-closed。</summary>
public sealed class RiskClassifier : IRiskChecker
{
    public const float Temperature = 0f;
    /// <summary>答案只有一小段 JSON；預設就會思考的模型把思考算進上限，太小會沒有答案而被當成失敗、全部拒絕。</summary>
    public const int MaxOutputTokens = ModelSlots.ReasoningOutputFloor;

    private readonly IChatClient _client;
    private readonly Func<string> _modelProvider;
    private readonly Func<string> _systemPrompt;
    private readonly Func<bool> _pinned;
    private readonly InstructionCache<RiskVerdict> _verdictCache = new();

    public RiskClassifier(IChatClient client, Func<string> modelProvider = null, Func<string> systemPrompt = null, Func<bool> pinned = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelProvider = modelProvider;
        _systemPrompt = systemPrompt;
        _pinned = pinned;
    }

    public async Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        string prompt = _systemPrompt?.Invoke();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new RiskVerdict { Risky = true, Failed = true, Reason = "risk check unavailable: classifier prompt is missing" };
        }
        // 動態索引或依當下選取目標的工具（相同參數在不同狀態下目標不同）需納入預覽，其餘工具只看指令＋工具＋參數。
        string cacheKey = BuildCacheKey(question);
        if (_verdictCache.TryGet(cacheKey, out RiskVerdict cached)) return cached;
        string text;
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, prompt),
                new(ChatRole.User, Describe(question))
            };
            var options = new ChatOptions
            {
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens
            };
            ModelSlots.ApplyModel(options, _modelProvider?.Invoke(), _pinned?.Invoke() ?? true);
            ChatResponse response = await _client.GetResponseAsync(messages, options, cancellationToken);
            text = response?.Text ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RiskVerdict { Risky = true, Failed = true, Reason = "risk check unavailable: " + ex.Message };
        }

        RiskVerdict verdict = Parse(text);
        if (verdict == null)
            return new RiskVerdict { Risky = true, Failed = true, Reason = "risk check returned an unreadable answer" };
        if (!verdict.Failed) _verdictCache.Store(cacheKey, verdict);
        return verdict;
    }

    internal void ClearCache()
    {
        _verdictCache.Clear();
    }

    internal static string BuildCacheKey(RiskQuestion question)
    {
        string baseKey = (question.Instruction ?? string.Empty) + "\0" +
            (question.ToolName ?? string.Empty) + "\0" + (question.Arguments ?? string.Empty);
        return IncludesPreviewInCacheKey(question.ToolName)
            ? baseKey + "\0" + (question.Preview ?? string.Empty)
            : baseKey;
    }

    private static bool IncludesPreviewInCacheKey(string toolName)
    {
        return Tools.AgentToolRegistry.IsGenericActionTool(toolName)
            || string.Equals(toolName, "delete_bill", StringComparison.Ordinal)
            || string.Equals(toolName, "choose_option", StringComparison.Ordinal)
            || string.Equals(toolName, "designate_area", StringComparison.Ordinal);
    }

    /// <summary>
    /// 每段用固定標籤包住並逐段轉義，工具參數或預覽裡的文字才不能冒充成玩家指令
    /// （模型看到的是 \&lt;player_instruction&gt;，不是另一段開頭）。
    /// </summary>
    internal static string Describe(RiskQuestion q)
    {
        return Section("player_instruction", q.Instruction) +
               Section("tool_name", q.ToolName) +
               Section("tool_arguments", q.Arguments) +
               Section("action_preview", q.Preview);
    }

    private static string Section(string tag, string value)
    {
        return "<" + tag + ">\n" + Escape(value ?? string.Empty) + "\n</" + tag + ">\n";
    }

    private static string Escape(string value)
    {
        return value.Replace("<", "\\<", StringComparison.Ordinal);
    }

    /// <summary>找出回覆裡第一個 JSON 物件；兩個欄位缺一或值不合法就回 null（由呼叫端當失敗處理）。</summary>
    internal static RiskVerdict Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{', StringComparison.Ordinal);
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("risk", out JsonElement risk) || risk.ValueKind != JsonValueKind.String) return null;
            string level = (risk.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            bool risky;
            if (level == "high") risky = true;
            else if (level == "low") risky = false;
            else return null;
            if (!root.TryGetProperty("authorized", out JsonElement authorized)) return null;
            bool? covered = ReadBoolean(authorized);
            if (!covered.HasValue) return null;
            string reason = root.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String
                ? (r.GetString() ?? string.Empty).Trim()
                : string.Empty;
            return new RiskVerdict { Risky = risky, Authorized = covered.Value, Reason = reason };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>模型常把布林寫成字串；只接受 true／false／yes／no，其餘一律回 null（即 fail-closed）。</summary>
    private static bool? ReadBoolean(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.True) return true;
        if (element.ValueKind == JsonValueKind.False) return false;
        if (element.ValueKind != JsonValueKind.String) return null;
        string value = (element.GetString() ?? string.Empty).Trim().ToLowerInvariant();
        if (value == "true" || value == "yes") return true;
        if (value == "false" || value == "no") return false;
        return null;
    }
}
