using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;

namespace RimAgent.Tools;

/// <summary>
/// run_script：模型一次送出一串工具呼叫，依序執行並合併回報，省下逐一往返的模型呼叫。
/// 只給主 Agent，由會話建立。每一步都呼叫原本的工具實例，所以權限、模式、確認閘門、分類器、
/// 額度與行動紀錄都照常套用；本身不經 <see cref="LoggingAIFunction"/>，避免閘門的呼叫狀態巢狀。
/// </summary>
internal sealed class ScriptTools
{
    public const string ToolName = "run_script";

    /// <summary>展開 for_each 後一次最多執行的呼叫數；再多就該分批，讓模型看過結果再繼續。</summary>
    internal const int MaxCalls = 30;

    private const int MaxArgsShown = 120;

    /// <summary>工具說明裡的範例；參數必須與實際工具一致，由單元測試核對。</summary>
    internal const string Example =
        "[{\"tool\":\"set_time_speed\",\"args\":{\"speed\":\"Paused\"}}," +
        "{\"tool\":\"place_blueprint\",\"args\":{\"building\":\"wall\",\"shape\":\"single\",\"x2\":0,\"z2\":0,\"rotation\":0,\"stuff\":\"wood\"}," +
        "\"for_each\":[{\"x\":10,\"z\":5},{\"x\":12,\"z\":5}]}]";

    private readonly Func<IList<AITool>> _availableTools;
    private readonly ToolOutputStore _outputs;
    private readonly PlayerInterjections _interjections;

    /// <param name="availableTools">目前權限下主 Agent 可執行的工具（含未公告的工具包），每次執行時取用。</param>
    /// <param name="outputs">合併結果過長時存放原文的輸出庫。</param>
    /// <param name="interjections">玩家插話佇列；某一步帶出了插話就停下，讓模型先讀。</param>
    public ScriptTools(Func<IList<AITool>> availableTools, ToolOutputStore outputs = null, PlayerInterjections interjections = null)
    {
        _availableTools = availableTools ?? throw new ArgumentNullException(nameof(availableTools));
        _outputs = outputs;
        _interjections = interjections;
    }

    private sealed class Call
    {
        public AIFunction Function;
        public Dictionary<string, object> Arguments;
    }

    [Description("Runs several tool calls in order in one step and returns all their results, instead of one call per step. " +
                 "steps is a JSON array; each item is {\"tool\": name, \"args\": {...}} and may add \"for_each\": [{...}, ...] " +
                 "to repeat the tool once per object, each merged over args. Example: " +
                 Example + ". " +
                 "Any tool you can call works, including tool pack tools. Stops at the first error unless continueOnError is true, " +
                 "and always stops when an action needs the player's confirmation or the player sends a message. Max 30 calls. " +
                 "Use it for steps whose arguments you already know; check results before scripting steps that depend on them.")]
    public async Task<string> RunScript(
        [Description("JSON array of steps as described above.")] string steps,
        [Description("true keeps going after a failed call; default stops at the first error.")] bool continueOnError = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(steps, out List<Call> calls, out string error)) return ToolText.Error(error);

        var sb = new StringBuilder();
        int ok = 0;
        int failed = 0;
        int ran = 0;
        string stopReason = null;
        foreach (Call call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ran++;
            int deliveredBefore = _interjections?.Delivered ?? 0;
            string text = await InvokeAsync(call, cancellationToken);
            bool callFailed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
            if (callFailed) failed++;
            else ok++;
            sb.Append(ran).Append(". ").Append(call.Function.Name).Append('(').Append(Describe(call.Arguments)).Append("): ")
              .Append(text.Trim()).AppendLine();

            if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
            {
                stopReason = "that action waits for the player's confirmation; run the remaining steps after it is confirmed";
            }
            else if ((_interjections?.Delivered ?? 0) != deliveredBefore)
            {
                stopReason = "the player sent a message; read it before continuing";
            }
            else if (callFailed && !continueOnError)
            {
                stopReason = "a call failed";
            }
            if (stopReason != null) break;
        }

        sb.Append("Ran ").Append(ran).Append(" of ").Append(calls.Count).Append(" calls: ")
          .Append(ok).Append(" ok, ").Append(failed).Append(" failed.");
        if (stopReason != null && ran < calls.Count)
        {
            sb.Append(" Stopped because ").Append(stopReason).Append("; ").Append(calls.Count - ran).Append(" not run.");
        }
        return ToolOutputLimit.Apply(sb.ToString(), store: _outputs);
    }

    private static async Task<string> InvokeAsync(Call call, CancellationToken cancellationToken)
    {
        try
        {
            object result = await call.Function.InvokeAsync(new AIFunctionArguments(call.Arguments), cancellationToken);
            return result?.ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 參數綁定錯誤等例外只算這一步失敗，與模型直接呼叫時收到的訊息一致。
            return ToolText.Error(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>先完整解析並檢查全部步驟，任何一步有誤就一步都不執行。</summary>
    private bool TryParse(string steps, out List<Call> calls, out string error)
    {
        calls = [];
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(steps) ? "[]" : steps);
        }
        catch (JsonException ex)
        {
            error = "steps is not valid JSON: " + ex.Message;
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                error = "steps must be a non-empty JSON array of {\"tool\": name, \"args\": {...}} objects.";
                return false;
            }

            Dictionary<string, AIFunction> tools = _availableTools()
                .OfType<AIFunction>()
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            int index = 0;
            foreach (JsonElement step in document.RootElement.EnumerateArray())
            {
                index++;
                if (!TryExpandStep(step, index, tools, calls, out error)) return false;
                if (calls.Count > MaxCalls)
                {
                    error = $"the script expands to more than {MaxCalls} calls. Split it and check results between parts.";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryExpandStep(JsonElement step, int index, Dictionary<string, AIFunction> tools, List<Call> calls, out string error)
    {
        error = null;
        if (step.ValueKind != JsonValueKind.Object
            || !step.TryGetProperty("tool", out JsonElement toolElement)
            || toolElement.ValueKind != JsonValueKind.String)
        {
            error = $"step {index} needs a \"tool\" name.";
            return false;
        }

        string name = toolElement.GetString();
        if (string.Equals(name, ToolName, StringComparison.Ordinal))
        {
            error = $"step {index}: {ToolName} cannot call itself.";
            return false;
        }
        if (!tools.TryGetValue(name ?? string.Empty, out AIFunction function))
        {
            error = $"step {index}: tool \"{name}\" is not available. Use the exact name of a tool you can call.";
            return false;
        }

        Dictionary<string, object> baseArgs = [];
        if (step.TryGetProperty("args", out JsonElement args))
        {
            if (args.ValueKind != JsonValueKind.Object)
            {
                error = $"step {index}: \"args\" must be an object.";
                return false;
            }
            Merge(baseArgs, args);
        }

        if (!step.TryGetProperty("for_each", out JsonElement forEach))
        {
            calls.Add(new Call { Function = function, Arguments = baseArgs });
            return true;
        }
        if (forEach.ValueKind != JsonValueKind.Array || forEach.GetArrayLength() == 0)
        {
            error = $"step {index}: \"for_each\" must be a non-empty array of objects.";
            return false;
        }
        foreach (JsonElement item in forEach.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"step {index}: every \"for_each\" item must be an object of arguments.";
                return false;
            }
            var merged = new Dictionary<string, object>(baseArgs);
            Merge(merged, item);
            calls.Add(new Call { Function = function, Arguments = merged });
            if (calls.Count > MaxCalls) return true;
        }
        return true;
    }

    /// <summary>引數保留 JsonElement（與模型直接呼叫時相同），交給 AIFunctionFactory 依參數型別轉換。</summary>
    private static void Merge(Dictionary<string, object> target, JsonElement source)
    {
        foreach (JsonProperty property in source.EnumerateObject())
        {
            target[property.Name] = property.Value.Clone();
        }
    }

    private static string Describe(Dictionary<string, object> arguments)
    {
        string text = string.Join(", ", arguments.Select(a => a.Key + "=" + (a.Value is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : a.Value?.ToString())));
        return ToolText.TruncateField(text, MaxArgsShown);
    }
}
