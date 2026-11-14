using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 包住每個工具：被 FunctionInvokingChatClient 叫用時即時寫入行動紀錄（玩家不必等整個迴圈
/// 結束才看到 agent 在做什麼），在呼叫前後把工具的靜態風險告知閘門，
/// 並在自動模式下做兩段式執行：先強制預覽拿到「將做的事」，交給分類器判定，
/// 分類器核可才強制執行一次；未核可則拒絕該命令並回報原因。
/// 這裡刻意不用 ConfigureAwait(false)：續行要留在主線程，第二段執行才能碰遊戲狀態。
///
/// 同一輪多個查詢可由框架一起送出：每個呼叫的暫存狀態只用區域變數與 AsyncLocal，
/// 共用狀態（閘門、快取、行動紀錄）各自上鎖，因此並行查詢安全；命令仍由閘門逐次把關。
///
/// 另外兩道與世界有關的守衛也在這裡，工具本身不必管：
/// 遊戲沒在進行（讀檔中、回到主選單）時不碰工具直接回錯誤；
/// 工具回傳後把這段期間遊戲記錄的錯誤附在結果後面，模型才知道命令雖然「成功」但遊戲隨即噴錯。
/// </summary>
internal sealed class LoggingAIFunction : DelegatingAIFunction
{
    private const int MaxGameErrorsAppended = 5;

    private readonly AgentTranscript _transcript;
    private readonly IGameWorld _world;
    private readonly ToolCategory _category;
    private readonly ToolRisk _risk;
    private readonly ConfirmationGate _gate;
    private readonly IRiskChecker _classifier;
    private readonly ToolOutputStore _outputs;
    private readonly PlayerInterjections _interjections;
    private readonly bool _consumesBudget;
    private readonly AgentActivityTracker _activity;

    /// <summary>子代理在自己的非同步流程內指定儲存；主 Agent 則用工具建立時綁定的那一份。</summary>
    private ToolOutputStore Outputs => ToolOutputStore.Current ?? _outputs;

    private bool RequiresExplicitConfirmation =>
        _gate != null && _gate.RequiresExplicitConfirmation(Name);

    public LoggingAIFunction(AIFunction inner, AgentTranscript transcript, IGameWorld world, ToolCategory category, ToolRisk risk, ConfirmationGate gate, IRiskChecker classifier = null, ToolOutputStore outputs = null, PlayerInterjections interjections = null, bool consumesBudget = true, AgentActivityTracker activity = null)
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
    }

    protected override async ValueTask<object> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        object result;
        using (_activity?.Begin(AgentActivityKind.Tool, Name))
        {
            result = await InvokeLoggedAsync(arguments, cancellationToken);
        }
        // 玩家執行中插話：附在主 Agent 這次的工具結果後面送給模型。只附在送回的文字，
        // 行動紀錄與失敗紀錄都存工具本身的結果。子代理的工具不取，免得話被子代理吃掉。
        string interjection = _transcript.CurrentAgent.Length == 0 ? _interjections?.TakeAll() : null;
        return interjection == null ? result : (result?.ToString() ?? string.Empty) + FormatInterjection(interjection);
    }

    private static string FormatInterjection(string text)
    {
        return "\n\n<player_message>\n" + text + "\n</player_message>\n" +
               "The player sent this while you were working. Take it into account before your next step; it may change or cancel your current plan.";
    }

    private async Task<object> InvokeLoggedAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var entry = new TranscriptEntry
        {
            Kind = TranscriptKind.Tool,
            ToolName = Name,
            ToolCategory = _category,
            ToolRisk = _risk,
            Arguments = FormatArguments(arguments),
            InstructionId = _transcript.CurrentInstructionId,
            Agent = _transcript.CurrentAgent
        };
        string callKey = CallKey(arguments);

        if (TryHandlePreInvocation(entry, callKey, out string earlyResult))
        {
            return earlyResult;
        }

        _gate?.BeginInvocation(Name, _risk);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            object result;
            if (_gate != null && RequiresExplicitConfirmation)
            {
                result = await InvokeExplicitConfirmationAsync(arguments, cancellationToken);
            }
            else if (_gate != null && _classifier != null && _gate.NeedsClassification(_risk))
            {
                result = await InvokeClassifiedAsync(arguments, entry, cancellationToken);
            }
            else
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            string text = ToolOutputLimit.Apply(AppendGameErrors(result?.ToString() ?? string.Empty), store: Outputs);
            entry.Result = text;
            entry.Failed = text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal);
            bool isPreview = text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal);
            // 失敗的呼叫記下來擋掉同句的原樣重送；成功命令後可重新嘗試。
            if (_risk != ToolRisk.Query && entry.Failed && entry.RiskCheck != "unavailable") _gate?.StoreFailedCall(Name, callKey, text);
            else if (_risk != ToolRisk.Query && !isPreview) _gate?.ClearFailedCalls();
            if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
            {
                // 此閉包保留本次工具呼叫的引數，確認時不會再讓模型選工具或改參數。
                _gate?.RegisterPendingApproval(Name, token => ExecuteApprovedAsync(arguments, entry, token));
            }
            return text;
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

    private bool TryHandlePreInvocation(TranscriptEntry entry, string callKey, out string earlyResult)
    {
        string notReady = _world.GetNotReadyReason();
        if (notReady != null)
        {
            entry.Result = ToolText.Error($"the game cannot run tools right now ({notReady}). Tell the player and stop.");
            entry.Failed = true;
            _transcript.Add(entry);
            earlyResult = entry.Result;
            return true;
        }

        if (_risk != ToolRisk.Query && _gate != null && _gate.Mode != AgentMode.Autonomous
            && _gate.TryGetFailedCall(Name, callKey, out string previousError))
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

    private async Task<object> ExecuteApprovedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
    {
        // 核准路徑不經過 TryHandlePreInvocation；玩家按下確認後遊戲可能已回到主選單或正在讀檔。
        string notReady = _world.GetNotReadyReason();
        if (notReady != null)
        {
            string error = ToolText.Error($"the game cannot run tools right now ({notReady}). Tell the player and stop.");
            _transcript.UpdateToolResult(entry, error, true);
            return error;
        }

        _gate?.BeginInvocation(Name, _risk);
        _gate?.ForceExecute();
        try
        {
            object result;
            using (_activity?.Begin(AgentActivityKind.Tool, Name))
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            string text = ToolOutputLimit.Apply(AppendGameErrors(result?.ToString() ?? string.Empty), store: Outputs);
            _transcript.UpdateToolResult(entry, text, text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal));
            return text;
        }
        catch (Exception ex)
        {
            _transcript.UpdateToolResult(entry, AppendGameErrors(ex.GetType().Name + ": " + ex.Message), true);
            throw;
        }
        finally
        {
            _gate?.EndInvocation();
        }
    }

    /// <summary>MCP 以使用者權限執行本機程式；所有可執行模式都必須先取得玩家明確確認。</summary>
    private async Task<object> InvokeExplicitConfirmationAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        object preview;
        _gate.ForcePreview();
        try
        {
            preview = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            _gate.ClearForce();
        }

        string text = preview?.ToString() ?? string.Empty;
        if (text.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal))
        {
            _gate.NoteBlocked(text);
        }
        return preview;
    }

    /// <summary>自動模式：黑名單直接擋、預覽 → 分類 → 執行或擋下。工具的預覽分支不碰世界，靠 CommandGateTests 鎖住。</summary>
    private async Task<object> InvokeClassifiedAsync(AIFunctionArguments arguments, TranscriptEntry entry, CancellationToken cancellationToken)
    {
        // 高危黑名單在自動模式永遠是高風險：直接拒絕，不必再燒一次分類器模型。
        if (AgentToolRegistry.IsHighRiskBlacklisted(Name))
        {
            entry.RiskCheck = "high";
            return ToolText.Error("safety review rejected " + Name + " (high): " +
                "this tool is on the high-risk blacklist and never runs in Auto mode. " +
                "Do not retry it unchanged.");
        }
        object previewResult;
        _gate.ForcePreview();
        try
        {
            previewResult = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            _gate.ClearForce();
        }

        string preview = previewResult?.ToString() ?? string.Empty;
        // 沒走到閘門（參數錯、目標不存在）就沒有可判定的命令，原樣回給模型。
        if (!preview.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal)) return previewResult;

        RiskVerdict verdict;
        using (_activity?.Begin(AgentActivityKind.Checking, Name))
        {
            verdict = await _classifier.ClassifyAsync(new RiskQuestion
            {
                Instruction = _transcript.CurrentInstructionText,
                ToolName = Name,
                Arguments = entry.Arguments,
                // 分類器也是模型呼叫：預覽過長會直接撐爆它的視窗，這裡照同一個上限縮。
                Preview = ToolOutputLimit.Apply(preview.Substring(ToolText.ConfirmationPrefix.Length))
            }, cancellationToken);
        }
        string riskCheck;
        if (verdict.Failed)
        {
            riskCheck = "unavailable";
        }
        else if (verdict.Risky)
        {
            riskCheck = "high";
        }
        else
        {
            // 低風險但玩家沒要求：擋下的理由跟「危險」不同，紀錄要分得出來。
            riskCheck = verdict.Authorized ? "low" : "unrequested";
        }
        entry.RiskCheck = riskCheck;
        entry.RiskCheckSource = string.IsNullOrEmpty(verdict.Source) ? "llm" : verdict.Source;

        if (verdict.ShouldReject)
        {
            string reason = string.IsNullOrEmpty(verdict.Reason) ? riskCheck : verdict.Reason;
            return ToolText.Error("safety review rejected " + Name + " (" + riskCheck + "): " + reason
                + ". This action cannot be confirmed in Auto mode; do not retry it unchanged.");
        }

        _gate.ForceExecute();
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        finally
        {
            _gate.ClearForce();
        }
    }

    /// <summary>
    /// 把這次呼叫期間遊戲記錄的錯誤附在結果後面。工具回「成功」只代表命令送進遊戲了，
    /// 遊戲隨即噴的錯（例如藍圖放下後的例外）模型否則看不到。預覽也一樣附：它同樣在主線程跑過遊戲程式碼。
    /// </summary>
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

    /// <summary>
    /// 失敗紀錄的鍵：依參數名排序，同一組參數換順序送來仍視為同一個呼叫
    /// （模型重送時常打亂順序）；顯示用的 <see cref="FormatArguments"/> 保留原順序。
    /// </summary>
    private static string CallKey(AIFunctionArguments arguments)
    {
        if (arguments == null || arguments.Count == 0) return string.Empty;
        return string.Join(", ", arguments
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + (pair.Value?.ToString() ?? "null")));
    }

    private static string FormatArguments(AIFunctionArguments arguments)
    {
        if (arguments == null || arguments.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (KeyValuePair<string, object> pair in arguments)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(pair.Key).Append('=').Append(pair.Value?.ToString() ?? "null");
        }
        return sb.ToString();
    }
}
