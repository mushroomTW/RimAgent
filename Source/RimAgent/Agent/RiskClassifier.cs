using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent;

/// <summary>分類器要看的一次命令：玩家原話、工具、參數、工具算好的預覽。</summary>
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

    /// <summary>只有「低風險且玩家明確要求」才執行；其餘由自動模式拒絕。</summary>
    public bool ShouldReject => Failed || Risky || !Authorized;
}

/// <summary>
/// 自動模式的風險分類器：把一次命令的預覽交給模型做一次不帶工具的判定。
/// 兩軸分開問：動作本身危不危險（low／high），以及玩家這句話有沒有要它做這件事（authorized）。
/// 只問風險會放掉「順手多做一件小事」——每件都低風險，但玩家沒要求。
/// 任何失敗都當高風險並拒絕該命令。
/// 系統提示詞來自 AgentPack/Prompts/RiskClassifier.md（經建構參數注入）；缺檔不斷言，
/// 直接回 fail-closed，讓自動模式拒絕命令。
/// </summary>
public sealed class RiskClassifier
{
    public const float Temperature = 0f;
    public const int MaxOutputTokens = 150;

    private readonly IChatClient _client;
    private readonly Func<string> _modelProvider;
    private readonly Func<string> _systemPrompt;
    /// <summary>同一句指令內的判定快取：模型重試相同命令時不再燒一次模型呼叫。</summary>
    private readonly InstructionCache<RiskVerdict> _verdictCache = new();

    public RiskClassifier(IChatClient client, Func<string> modelProvider = null, Func<string> systemPrompt = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelProvider = modelProvider;
        _systemPrompt = systemPrompt;
    }

    public async Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken)
    {
        if (question == null) throw new ArgumentNullException(nameof(question));
        string prompt = _systemPrompt?.Invoke();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new RiskVerdict { Risky = true, Failed = true, Reason = "risk check unavailable: classifier prompt is missing" };
        }
        // 鍵只用「玩家原話＋工具＋參數」：這兩軸（動作危不危險、玩家有沒有要求）由此決定，
        // 預覽裡的新舊數值是同一動作的當下快照，不影響判定；失敗與無法解析的 verdict 不進快取。
        string cacheKey = (question.Instruction ?? string.Empty) + "\n" +
            (question.ToolName ?? string.Empty) + "\n" + (question.Arguments ?? string.Empty);
        if (_verdictCache.TryGet(cacheKey, out RiskVerdict cached)) return cached;
        string text;
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, prompt),
                new(ChatRole.User, Describe(question))
            };
            string preferredModel = _modelProvider?.Invoke();
            var options = new ChatOptions
            {
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens,
                ModelId = string.IsNullOrWhiteSpace(preferredModel) ? null : preferredModel.Trim()
            };
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

    /// <summary>新的一句指令開始時清空：判定只在同一句內重用，不跨指令。</summary>
    internal void ClearCache()
    {
        _verdictCache.Clear();
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
        return value.Replace("<", "\\<");
    }

    /// <summary>找出回覆裡第一個 JSON 物件；兩個欄位缺一或值不合法就回 null（由呼叫端當失敗處理）。</summary>
    internal static RiskVerdict Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
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

    /// <summary>模型常把布林寫成字串；true／false 以外的值一律不接受（回 null 即 fail-closed）。</summary>
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
