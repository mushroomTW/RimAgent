using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 這裡刻意不用 ConfigureAwait(false)：續行要留在主線程，第二段執行才能碰遊戲狀態。
/// 同一輪多個查詢可由框架一起送出：每個呼叫的暫存狀態只用區域變數與 AsyncLocal，
/// 共用狀態（閘門、快取、行動紀錄）各自上鎖，因此並行查詢安全；命令仍由閘門逐次把關。
/// 另外兩道與世界有關的守衛也在這裡，工具本身不必管：
/// 遊戲沒在進行（讀檔中、回到主選單）時不碰工具直接回錯誤；
/// 工具回傳後把這段期間遊戲記錄的錯誤附在結果後面，模型才知道命令雖然「成功」但遊戲隨即噴錯。
/// </summary>
internal sealed partial class LoggingAIFunction : DelegatingAIFunction
{
    private const int MaxGameErrorsAppended = 5;

    private readonly AgentTranscript _transcript;
    private readonly IGameWorld _world;
    private readonly ToolCategory _category;
    private readonly ToolRisk _risk;
    private readonly ConfirmationGate? _gate;
    private readonly IRiskChecker? _classifier;
    private readonly ToolOutputStore? _outputs;
    private readonly PlayerInterjections? _interjections;
    private readonly bool _consumesBudget;
    private readonly AgentActivityTracker? _activity;
    private readonly FailureLoopGuard? _loopGuard;

    private ToolOutputStore? Outputs => ToolOutputStore.Current ?? _outputs;

    private bool RequiresExplicitConfirmation =>
        _gate != null && _gate.RequiresExplicitConfirmation(Name);

#pragma warning disable S107 // 建構子參數皆為相依元件，無需重構
    public LoggingAIFunction(AIFunction inner, AgentTranscript transcript, IGameWorld world, ToolCategory category, ToolRisk risk, ConfirmationGate? gate, IRiskChecker? classifier = null, ToolOutputStore? outputs = null, PlayerInterjections? interjections = null, bool consumesBudget = true, AgentActivityTracker? activity = null, FailureLoopGuard? loopGuard = null)
        : base(inner)
    {
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _category = category;
        _risk = risk;
        _gate = gate;
        _classifier = classifier;
        _outputs = outputs;
        _interjections = interjections;
        _consumesBudget = consumesBudget;
        _activity = activity;
        _loopGuard = loopGuard;
    }
#pragma warning restore S107

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        object? result;
        using (_activity?.Begin(AgentActivityKind.Tool, Name))
        {
            result = await InvokeLoggedAsync(arguments, cancellationToken);
        }
        // 玩家執行中插話：附在主 Agent 這次的工具結果後面送給模型。只附在送回的文字，
        // 行動紀錄與失敗紀錄都存工具本身的結果。子代理的工具不取，免得話被子代理吃掉。
        bool mainAgent = _transcript.CurrentAgent.Length == 0;
        string? redirect = mainAgent && _loopGuard != null
            ? _loopGuard.Observe(Name, result?.ToString())
            : null;
        string? interjection = mainAgent ? _interjections?.TakeAll() : null;
        if (redirect == null && interjection == null) return result;
        return (result?.ToString() ?? string.Empty) + redirect + (interjection == null ? null : FormatInterjection(interjection));
    }

    /// <summary>玩家插話與守衛提示以標籤附在工具結果後面；工具內容（網頁、MCP、遊戲內名稱）自帶同名標籤就能冒充玩家。</summary>
    internal static string NeutralizeTrustedTags(string text) => TrustedTag.Replace(text ?? string.Empty, "&lt;${tag}");

    [GeneratedRegex(@"<(?<tag>\s*(?:/\s*)?(?:player_message|loop_guard)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrustedTag { get; }

    private static string FormatInterjection(string text)
    {
        return "\n\n<player_message>\n" + text + "\n</player_message>\n" +
               "The player sent this while you were working. Take it into account before your next step; it may change or cancel your current plan.";
    }

    private async Task<object?> InvokeLoggedAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        NormalizeRectangleStart(arguments);
        var entry = new TranscriptEntry
        {
            Kind = TranscriptKind.Tool,
            ToolName = Name,
            ToolCategory = _category,
            ToolRisk = _risk,
            Arguments = FormatArguments(arguments),
            ArgumentValues = arguments,
            InstructionId = _transcript.CurrentInstructionId,
            Agent = _transcript.CurrentAgent
        };
        string callKey = CallKey(arguments);

        if (TryHandlePreInvocation(entry, callKey, out string? earlyResult))
        {
            return earlyResult;
        }

        _gate?.BeginInvocation(Name, _risk);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            object? result = await InvokeThroughGateAsync(arguments, entry, cancellationToken);
            return RecordResult(result, arguments, entry, callKey);
        }
        catch (Exception ex)
        {
            // 例外那次期間的遊戲錯誤也算這次的，不能留給下一個工具呼叫背鍋。
            entry.Result = AppendGameErrors(ex.GetType().Name + ": " + ex.Message);
            entry.Failed = true;
            entry.Cancelled = ex is OperationCanceledException;
            throw;
        }
        finally
        {
            entry.DurationMs = stopwatch.ElapsedMilliseconds;
            _gate?.EndInvocation();
            _transcript.Add(entry);
        }
    }

    /// <summary>有的工具矩形起點叫 x1、z1，有的叫 x、z，模型常混用；改成這個工具的名字。</summary>
    private void NormalizeRectangleStart(AIFunctionArguments arguments)
    {
        if (!JsonSchema.TryGetProperty("properties", out JsonElement properties)) return;
        foreach ((string name, string alias) in new[] { ("x", "x1"), ("z", "z1"), ("x1", "x"), ("z1", "z") })
        {
            if (!properties.TryGetProperty(name, out _) || properties.TryGetProperty(alias, out _) || arguments.ContainsKey(name)) continue;
            if (arguments.Remove(alias, out object? value)) arguments[name] = value;
        }
    }

    /// <summary>缺必填參數時 MEAI 丟的 ArgumentException 一次只說一個，也不說這個工具要哪些。</summary>
    private string? MissingArguments(AIFunctionArguments arguments)
    {
        if (!JsonSchema.TryGetProperty("required", out JsonElement required) || required.ValueKind != JsonValueKind.Array) return null;
        string[] names = [.. required.EnumerateArray().Select(r => r.GetString()).OfType<string>()];
        string[] missing = [.. names.Where(n => !arguments.ContainsKey(n))];
        return missing.Length == 0 ? null : ToolText.Error($"missing argument(s): {string.Join(", ", missing)}. {Name} always needs: {string.Join(", ", names)} (pass \"\" for text that may be empty).");
    }

    private ValueTask<object?> InvokeThroughGateAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
    {
        if (MissingArguments(arguments) is { } missing) return new ValueTask<object?>(missing);
        if (_gate != null && RequiresExplicitConfirmation)
        {
            return new ValueTask<object?>(InvokeExplicitConfirmationAsync(_gate, arguments, cancellationToken));
        }
        if (_gate != null && _classifier != null && _gate.NeedsClassification(_risk))
        {
            return new ValueTask<object?>(InvokeClassifiedAsync(_gate, _classifier, arguments, entry, cancellationToken));
        }
        return base.InvokeCoreAsync(arguments, cancellationToken);
    }

    private string RecordResult(object? result, AIFunctionArguments arguments, TranscriptEntry entry, string callKey)
    {
        string text = ToolOutputLimit.Apply(NeutralizeTrustedTags(AppendGameErrors(result?.ToString() ?? string.Empty)), store: Outputs);
        entry.Result = text;
        entry.Failed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
        bool isPreview = text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal);
        if (_risk != ToolRisk.Query && entry.Failed && entry.RiskCheck != "unavailable") _gate?.StoreFailedCall(Name, callKey, text);
        else if (_risk != ToolRisk.Query && !isPreview) _gate?.ClearFailedCalls();
        if (isPreview)
        {
            // 此閉包保留本次工具呼叫的引數，確認時不會再讓模型選工具或改參數。
            _gate?.RegisterPendingApproval(Name, token => ExecuteApprovedAsync(arguments, entry, token));
        }
        return text;
    }

    private bool TryHandlePreInvocation(TranscriptEntry entry, string callKey, [NotNullWhen(true)] out string? earlyResult)
    {
        string? notReady = _world.GetNotReadyReason();
        if (notReady != null)
        {
            entry.Result = ToolText.Error($"the game cannot run tools right now ({notReady}). Tell the player and stop.");
            entry.Failed = true;
            _transcript.Add(entry);
            earlyResult = entry.Result;
            return true;
        }

        if (_risk != ToolRisk.Query && _gate != null && _gate.Mode != AgentMode.Autonomous
            && _gate.TryGetFailedCall(Name, callKey, out string? previousError))
        {
            entry.Result = ToolText.Error("this exact call already failed twice in this instruction and was not run again. " +
                "Do not repeat it unchanged: use different arguments, try another approach, or stop and tell the player. " +
                "Previous result: " + previousError);
            entry.Failed = true;
            entry.BudgetConsumed = false;
            _transcript.Add(entry);
            earlyResult = entry.Result;
            return true;
        }

        bool budgetConsumed = false;
        if (_consumesBudget && _gate != null && !_gate.TryConsumeToolBudget(out budgetConsumed))
        {
            entry.Result = ToolText.Error("autonomous tool budget exhausted.");
            entry.Failed = true;
            _transcript.Add(entry);
            earlyResult = entry.Result;
            return true;
        }
        entry.BudgetConsumed = budgetConsumed;
        earlyResult = null;
        return false;
    }

    private async Task<object?> ExecuteApprovedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
    {
        // 核准路徑不經過 TryHandlePreInvocation；玩家按下確認後遊戲可能已回到主選單或正在讀檔。
        string? notReady = _world.GetNotReadyReason();
        if (notReady != null)
        {
            string error = ToolText.Error($"the game cannot run tools right now ({notReady}). Tell the player and stop.");
            _transcript.UpdateToolResult(entry, error, failed: true);
            return error;
        }

        _gate?.BeginInvocation(Name, _risk);
        _gate?.ForceExecute();
        try
        {
            object? result;
            using (_activity?.Begin(AgentActivityKind.Tool, Name))
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            string text = ToolOutputLimit.Apply(NeutralizeTrustedTags(AppendGameErrors(result?.ToString() ?? string.Empty)), store: Outputs);
            _transcript.UpdateToolResult(entry, text, text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal));
            return text;
        }
        catch (Exception ex)
        {
            _transcript.UpdateToolResult(entry, AppendGameErrors(ex.GetType().Name + ": " + ex.Message), failed: true);
            throw;
        }
        finally
        {
            _gate?.EndInvocation();
        }
    }

    /// <summary>確認模式下的 MCP 呼叫：MCP 以使用者權限執行本機程式，即使玩家按過「不再問」也要每次確認。</summary>
    private async Task<object?> InvokeExplicitConfirmationAsync(
        ConfirmationGate gate,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        object? preview;
        gate.ForcePreview();
        try
        {
            preview = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            gate.ClearForce();
        }

        string text = preview?.ToString() ?? string.Empty;
        if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
        {
            gate.NoteBlocked(text);
        }
        return preview;
    }

    /// <summary>自動模式：預覽 → 分類 → 執行或擋下。工具的預覽分支不碰世界，靠 CommandGateTests 鎖住。</summary>
    private async Task<object?> InvokeClassifiedAsync(ConfirmationGate gate, IRiskChecker classifier, AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
    {
        object? previewResult = await InvokePreviewAsync(gate, arguments, cancellationToken);
        string preview = previewResult?.ToString() ?? string.Empty;
        // 沒走到閘門（參數錯、目標不存在）就沒有可判定的命令，原樣回給模型。
        if (!preview.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal)) return previewResult;

        RiskVerdict verdict;
        using (_activity?.Begin(AgentActivityKind.Checking, Name))
        {
            verdict = await classifier.ClassifyAsync(new RiskQuestion
            {
                Instruction = _transcript.CurrentInstructionText,
                ToolName = Name,
                Arguments = entry.Arguments,
                // 分類器也是模型呼叫：預覽過長會直接撐爆它的視窗，這裡照同一個上限縮。
                Preview = ToolOutputLimit.Apply(preview.Substring(ToolText.ConfirmationPrefix.Length))
            }, cancellationToken);
        }
        string riskCheck = RiskCheckLabel(verdict);
        entry.RiskCheck = riskCheck;
        entry.RiskCheckSource = string.IsNullOrEmpty(verdict.Source) ? "llm" : verdict.Source;

        if (verdict.ShouldReject)
        {
            string reason = string.IsNullOrEmpty(verdict.Reason) ? riskCheck : verdict.Reason;
            return ToolText.Error("safety review rejected " + Name + " (" + riskCheck + "): " + reason
                + ". This action cannot be confirmed in Auto mode; do not retry it unchanged.");
        }

        gate.ForceExecute();
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            gate.ClearForce();
        }
    }

    private async Task<object?> InvokePreviewAsync(ConfirmationGate gate, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        gate.ForcePreview();
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            gate.ClearForce();
        }
    }

    /// <summary>紀錄用的風險標籤；低風險但玩家沒要求的擋下理由跟「危險」不同，要分得出來。</summary>
    private static string RiskCheckLabel(RiskVerdict verdict)
    {
        if (verdict.Failed) return "unavailable";
        if (verdict.Risky) return "high";
        return verdict.Authorized ? "low" : "unrequested";
    }

    /// <summary>把這次呼叫期間遊戲記錄的錯誤附在結果後面；工具回成功只代表命令送進遊戲，隨後噴的錯（例如藍圖放下後的例外）模型否則看不到。預覽同樣會附，它也在主線程跑過遊戲程式碼。</summary>
    private string AppendGameErrors(string text)
    {
        return AppendGameErrors(text, out _);
    }

    private string AppendGameErrors(string text, out bool hasFreshGameErrors)
    {
        IReadOnlyList<string> errors = _world.TakeNewGameErrors();
        hasFreshGameErrors = errors.Count > 0;
        return FormatWithGameErrors(text, errors);
    }

    private static string FormatWithGameErrors(string text, IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) return text;
        var sb = new StringBuilder(text);
        sb.AppendLine().Append("Game errors logged during this call (").Append(errors.Count).Append("):");
        foreach (string error in errors.Take(MaxGameErrorsAppended)) sb.AppendLine().Append("- ").Append(error);
        if (errors.Count > MaxGameErrorsAppended) sb.AppendLine().Append("- ... and ").Append(errors.Count - MaxGameErrorsAppended).Append(" more");
        return sb.ToString();
    }

    /// <summary>失敗紀錄的鍵依參數名排序，模型重送時常打亂順序，仍視為同一個呼叫。</summary>
    private static string CallKey(AIFunctionArguments? arguments)
    {
        if (arguments == null || arguments.Count == 0) return string.Empty;
        return string.Join(", ", arguments
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + (pair.Value?.ToString() ?? "null")));
    }

    private static string FormatArguments(AIFunctionArguments? arguments)
    {
        if (arguments == null || arguments.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(pair.Key).Append('=').Append(pair.Value?.ToString() ?? "null");
        }
        return sb.ToString();
    }
}
