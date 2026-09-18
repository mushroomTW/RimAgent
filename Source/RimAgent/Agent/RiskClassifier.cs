using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent
{
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
        /// <summary>true＝要玩家確認；分類器失敗時也是 true（fail-closed）。</summary>
        public bool Risky { get; set; }
        public string Reason { get; set; } = string.Empty;
        /// <summary>分類器本身出錯（網路、逾時、回覆解析不了），不是模型判定。</summary>
        public bool Failed { get; set; }
    }

    /// <summary>
    /// 自動模式的風險分類器：把一次命令的預覽交給模型做一次不帶工具的判定，回 low／high。
    /// 任何失敗都當 high，寧可多問玩家一次也不放過。
    /// </summary>
    public sealed class RiskClassifier
    {
        public const float Temperature = 0f;
        public const int MaxOutputTokens = 150;

        private const string SystemPrompt =
@"You are a safety reviewer for an assistant that manages a RimWorld colony on the player's behalf. The assistant is about to execute ONE game action. Decide whether the player should be asked to confirm it first.

Answer ""high"" (ask first) when the action:
- is hard or impossible to undo (trading, sending colonists off the map, releasing prisoners, surgery, deleting zones or bills, deconstructing buildings, attacking),
- affects many colonists or a large area at once,
- spends significant resources or silver,
- clearly goes beyond what the player's instruction asked for, or the instruction is ambiguous about it.

Answer ""low"" (just do it) when the action is small, reversible and plainly what the player asked for (work priorities, schedules, drafting, area or filter tweaks, research or time-speed changes).

Reply with exactly one JSON object and nothing else: {""risk"": ""low"" or ""high"", ""reason"": ""one short sentence""}.";

        private readonly IChatClient _client;

        public RiskClassifier(IChatClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<RiskVerdict> ClassifyAsync(RiskQuestion question, CancellationToken cancellationToken)
        {
            if (question == null) throw new ArgumentNullException(nameof(question));
            string text;
            try
            {
                var messages = new List<ChatMessage>
                {
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, Describe(question))
                };
                var options = new ChatOptions { Temperature = Temperature, MaxOutputTokens = MaxOutputTokens };
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
            return verdict ?? new RiskVerdict { Risky = true, Failed = true, Reason = "risk check returned an unreadable answer" };
        }

        internal static string Describe(RiskQuestion q)
        {
            return "Player's instruction: " + q.Instruction + "\n" +
                   "Tool: " + q.ToolName + "\n" +
                   "Arguments: " + q.Arguments + "\n" +
                   "What it would do: " + q.Preview;
        }

        /// <summary>找出回覆裡第一個 JSON 物件；沒有 risk 欄位或值不是 low／high 就回 null。</summary>
        internal static RiskVerdict Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(text.Substring(start, end - start + 1)))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("risk", out JsonElement risk) || risk.ValueKind != JsonValueKind.String) return null;
                    string level = (risk.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                    bool risky;
                    if (level == "high") risky = true;
                    else if (level == "low") risky = false;
                    else return null;
                    string reason = root.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String
                        ? (r.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    return new RiskVerdict { Risky = risky, Reason = reason };
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
