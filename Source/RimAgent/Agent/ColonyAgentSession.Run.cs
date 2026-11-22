#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Agent;

public sealed partial class ColonyAgentSession
{
    public IReadOnlyList<ChatMessage> History
    {
        get
        {
            AgentSession session = _session;
            if (session == null || _agent.ChatHistoryProvider is not InMemoryChatHistoryProvider provider) return [];
            return provider.GetMessages(session);
        }
    }

    /// <summary>送出一句玩家指令並等待 agent 做完；失敗時把錯誤寫進 transcript 並回傳 null。失敗那一輪的使用者訊息不會進歷史（AF 的 ChatHistoryProvider 在例外時略過儲存）。執行中沒趕上工具結果的插話（<see cref="Interject"/>）會接著跑完才返回，回傳值仍是這句的回覆。</summary>
    /// <param name="autonomous">這句是排程器起的自主任務；只影響對話裡的標示，模型看到的內容相同。</param>
    /// <param name="maxToolSteps">本輪額外步數上限；自主排程器用它把單輪呼叫夾在剩餘每日預算內。</param>
    /// <param name="onAccepted">玩家訊息寫入對話後的通知；忙碌而拒收時不呼叫。</param>
    /// <param name="autonomousInfo">自主任務的起跑脈絡，只寫進執行紀錄；帶了就視為自主任務。</param>
    /// <param name="images">玩家貼上的圖片（JPEG）；只在這句指令期間附給模型，不寫進對話歷史。</param>
#pragma warning disable S107 // 公共方法參數多，但皆為選用參數，呼叫端可省略，無需重構
    public async Task<string> SendAsync(
        string userText,
        bool autonomous = false,
        int? maxToolSteps = null,
        bool goalRun = false,
        string displayText = null,
        ReasoningLevel? reasoningOverride = null,
        Action onAccepted = null,
        AutonomousRunInfo autonomousInfo = null,
        IReadOnlyList<byte[]> images = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userText);
        if (string.IsNullOrWhiteSpace(userText)) return null;
        // 紀錄關閉時呼叫端不建脈絡，所以只能單向推導：有脈絡必是自主任務，反之不一定。
        autonomous |= autonomousInfo != null;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new AgentBusyException();
        }

        _interjections.Open();
        try
        {
            var options = new InstructionOptions
            {
                Autonomous = autonomous,
                MaxToolSteps = maxToolSteps,
                GoalRun = goalRun,
                DisplayText = displayText,
                ReasoningOverride = reasoningOverride,
                OnAccepted = onAccepted,
                AutonomousInfo = autonomousInfo
            };
            string reply = await RunInstructionWithImagesAsync(userText.Trim(), options, images, cancellationToken).ConfigureAwait(false);
            await RunLateInterjectionsAsync(cancellationToken).ConfigureAwait(false);
            return reply;
        }
        finally
        {
            // 例外路徑的保險：忙碌結束後不留下永遠送不出的插話。
            _interjections.CloseAndDiscard();
            Volatile.Write(ref _busy, 0);
        }
    }

#pragma warning restore S107

    private async Task<string> RunInstructionWithImagesAsync(string text, InstructionOptions options, IReadOnlyList<byte[]> imgs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        _images.SetPlayerImages(imgs, text.Trim());
        try
        {
            return await RunInstructionAsync(text.Trim(), options, echo: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _images.SetPlayerImages(images: null);
        }
    }

    /// <summary>玩家在執行中送出的話：排入佇列，主 Agent 下一個工具結果會帶給模型，沒趕上的在本輪結束後接著當新指令跑。只在 <see cref="SendAsync"/> 執行期間收，其他時候回傳 false，由呼叫端照一般指令送出。</summary>
    public bool Interject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (!_interjections.TryAdd(trimmed)) return false;
        Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.User,
            Text = trimmed,
            InstructionId = Transcript.CurrentInstructionId,
            Interjection = true
        });
        return true;
    }

    /// <summary>佇列裡的插話在同一段忙碌期間接著當新指令跑，不與排程器或目標搶執行權；原指令的回覆不變，目標判讀不受影響。按了停止或有命令等玩家確認時不送（新指令會作廢那些確認），丟棄並留一則說明。</summary>
    private async Task RunLateInterjectionsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                DropInterjections("the run was stopped");
                return;
            }
            if (HasPendingConfirmation)
            {
                DropInterjections("commands are waiting for confirmation; confirm or discard them, then send it again");
                return;
            }
            string next = _interjections.TakeAllOrClose();
            if (next == null) return;
            await RunInstructionAsync(next, default, echo: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private void DropInterjections(string reason)
    {
        int dropped = _interjections.CloseAndDiscard();
        if (dropped == 0) return;
        Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = dropped.ToString(CultureInfo.InvariantCulture) + " message(s) sent while RimAgent was working were not delivered because " + reason + ".",
            InstructionId = Transcript.CurrentInstructionId
        });
    }

    private readonly struct InstructionOptions
    {
        public bool Autonomous { get; init; }
        public int? MaxToolSteps { get; init; }
        public bool GoalRun { get; init; }
        public string DisplayText { get; init; }
        public ReasoningLevel? ReasoningOverride { get; init; }
        public Action OnAccepted { get; init; }
        public AutonomousRunInfo AutonomousInfo { get; init; }
    }

    private async Task<string> RunInstructionAsync(
        string text,
        InstructionOptions options,
        bool echo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        // 新指令取代等待中的確認，先記錄捨棄，再建立新指令的編號。
        DiscardPendingConfirmation();
        // 句內分類器判定快取不跨指令，下一句重來。
        ResetInstructionCaches();
        int instructionId = Transcript.BeginInstruction();
        _loopGuard.Reset();
        RunLogRecord runLog = RunLoggingEnabled?.Invoke() == true ? new RunLogRecord
        {
            Instruction = RunLogRecord.Limit(options.DisplayText ?? text),
            Mode = Mode.ToString(),
            Source = DetermineRunSource(options.GoalRun, options.Autonomous),
            Autonomous = options.AutonomousInfo
        } : null;
        _activeRunLog = runLog;
        string runOutcome = "finished";
        AddUserTranscriptEntryIfEcho(echo, options.DisplayText, text, instructionId, options.Autonomous, options.GoalRun);
        options.OnAccepted?.Invoke();
        Undo.BeginFrame();
        ConfirmGate.BeginToolBudget(options.MaxToolSteps, options.Autonomous, ConfirmAutonomousMajor?.Invoke() == true);
        ApplyRunStepLimit(options.MaxToolSteps);

        try
        {
            // 指令開始前的遊戲錯誤與這句無關，先丟掉；之後每次工具呼叫只回報這段期間新增的。
            _world.TakeNewGameErrors();
            if (_session == null)
            {
                _session = await RestoreOrCreateSession(cancellationToken);
            }

            AgentResponse response = await RunWithRetryOnContextTooLongAsync(
                text, instructionId, options.ReasoningOverride, cancellationToken).ConfigureAwait(false);

            string reply = RecordReply(response, instructionId, options.GoalRun, ref runOutcome);
            Undo.EndFrame(text);
            HandleBlockedConfirmations(text, options.GoalRun, options.DisplayText, instructionId);
            return reply;
        }
        catch (Exception ex)
        {
            return HandleInstructionError(ex, text, instructionId, cancellationToken, ref runOutcome);
        }
        finally
        {
            TryWriteRunLog(runLog, instructionId, runOutcome);
            RecordToolUsage(instructionId);
            _activeRunLog = null;
            ConfirmGate.EndToolBudget();
            ApplyRunStepLimit(overrideSteps: null);
            PrepareSaveSnapshot();
        }
    }

    private string RecordReply(AgentResponse response, int instructionId, bool goalRun, ref string runOutcome)
    {
        string reply = FinalAssistantText(response);
        bool hitStepLimit = HitStepLimit(response.Messages, _invoking?.MaximumIterationsPerRequest ?? 0);
        if (hitStepLimit) reply = StripToolCallMarkup(reply);
        if (goalRun) runOutcome = "goal_reported_" + GoalProgress.ParseStatus(reply).ToString().ToLowerInvariant();
        Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Assistant,
            Text = goalRun ? GoalProgress.VisibleReply(reply) : reply,
            InstructionId = instructionId,
            ToolsStripped = IsToolsStripped(response)
        });
        if (hitStepLimit)
        {
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "The tool-step budget was reached before all planned actions completed. Ask a follow-up instruction to continue.",
                InstructionId = instructionId
            });
        }
        return reply;
    }

    private void AddUserTranscriptEntryIfEcho(
        bool echo,
        string displayText,
        string text,
        int instructionId,
        bool autonomous,
        bool goalRun)
    {
        if (echo)
        {
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.User,
                Text = displayText ?? text,
                InstructionId = instructionId,
                Autonomous = autonomous,
                GoalRun = goalRun
            });
        }
    }

    private void HandleBlockedConfirmations(
        string text,
        bool goalRun,
        string displayText,
        int instructionId)
    {
        IReadOnlyList<string> blocked = ConfirmGate.TakeBlocked();
        if (blocked.Count > 0)
        {
            _pendingConfirmation = text;
            _pendingGoalObjective = goalRun ? displayText : null;
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = $"{blocked.Count} command(s) waiting for confirmation. " +
                       "Review the previews above, then press Confirm to execute " +
                       "(or send a new instruction to discard).",
                InstructionId = instructionId
            });
        }
    }

    private string HandleInstructionError(
        Exception ex,
        string text,
        int instructionId,
        CancellationToken cancellationToken,
        ref string runOutcome)
    {
        runOutcome = ex is OperationCanceledException ? "cancelled" : "error";
        // 失敗那句可能只做了一半，但做了的部分仍該能復原；空框會被 EndFrame 丟掉。
        Undo.EndFrame(text + " (failed)");
        AgentErrorKind errorKind = Classify(ex);
        if (!(cancellationToken.IsCancellationRequested && errorKind == AgentErrorKind.Cancelled))
        {
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Error,
                ErrorKind = errorKind,
                Text = ex.Message,
                InstructionId = instructionId
            });
        }
        return null;
    }

    private static string DetermineRunSource(bool goalRun, bool autonomous)
    {
        if (goalRun) return "goal";
        if (autonomous) return "autonomous";
        return "player";
    }

    private async Task<AgentResponse> RunWithRetryOnContextTooLongAsync(
        string text,
        int instructionId,
        ReasoningLevel? reasoningOverride,
        CancellationToken cancellationToken)
    {
        RimLLMChatOptions options = await BuildRunOptionsAsync(text, reasoningOverride, cancellationToken).ConfigureAwait(false);
        // System One 的操作回報只給主 LLM 看：分流與預載判斷看的是原指令。
        string modelText = DecisionReports.Prepend(DecisionReports.TakeAll(), text);
        try
        {
            return await _agent
                .RunAsync(modelText, _session, new ChatClientAgentRunOptions(options), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Classify(ex) == AgentErrorKind.ContextTooLong)
        {
            // 工具若已經執行，整句重跑可能造成重複副作用；只允許尚未呼叫工具的請求自動重試。
            if (Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Tool && e.InstructionId == instructionId))
            {
                throw;
            }
            // 供應商仍可能因 schema、推理 token 或既有舊存檔超出視窗。
            // 重建 AF session 後僅重試一次目前指令，避免玩家卡死在每句都失敗的狀態。
            _session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Conversation context was too long; older model history was cleared and this instruction was retried.",
                InstructionId = instructionId
            });
            return await _agent
                .RunAsync(modelText, _session, new ChatClientAgentRunOptions(options), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>主 Agent 與子代理的模型回應都記進執行紀錄；並行的子代理會同時呼叫，所以上鎖。</summary>
    private void RecordModelResponse(ChatResponse response)
    {
        RunLogRecord log = _activeRunLog;
        if (log == null) return;
        lock (log)
        {
            log.AddModelResponse(response);
        }
    }

    private void TryWriteRunLog(RunLogRecord record, int instructionId, string outcome)
    {
        if (record == null) return;
        try
        {
            record.FinishedUtc = DateTime.UtcNow;
            record.CaptureTranscript(Transcript.Snapshot(), instructionId);
            record.Outcome = outcome;
            RunLogSink?.Invoke(record);
        }
        catch (Exception ex)
        {
            // 紀錄失敗不影響遊戲指令，但至少提醒玩家一次。
            if (_runLogWarningShown) return;
            _runLogWarningShown = true;
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Run log could not be written: " + ex.Message,
                InstructionId = instructionId
            });
        }
    }

    private void RecordToolUsage(int instructionId)
    {
        ToolUsageStats stats = ToolUsage;
        if (stats == null) return;
        try
        {
            stats.Record(Transcript.Snapshot().Where(e => e.InstructionId == instructionId), ToolPacks.PackOf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 統計寫不進去不影響指令；下一句照常累積，下次寫入成功時一併存下。
        }
    }

    private void NotifyStuck(string text)
    {
        Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = text,
            InstructionId = Transcript.CurrentInstructionId
        });
    }

    private void ApplyRunStepLimit(int? overrideSteps)
    {
        if (_invoking == null) return;
        int configured = MaxSteps;
        _invoking.MaximumIterationsPerRequest = overrideSteps.HasValue
            ? Math.Max(1, Math.Min(configured, overrideSteps.Value))
            : configured;
    }

    public void Clear()
    {
        if (IsBusy) throw new InvalidOperationException("Cannot clear while RimAgent is working.");
        // 丟棄 AF session（含歷史），下一次 SendAsync 重建；復原堆疊與等待中的確認一併作廢，對話歸零後「上一句」已無所指。
        _session = null;
        _serializedSession = null;
        _saveSnapshot = null;
        _tokenUsage.Reset();
        _pendingConfirmation = null;
        _pendingGoalObjective = null;
        _confirmedGoalObjective = null;
        _goalConfirmationReport = null;
        ConfirmGate.DiscardPendingApprovals();
        ConfirmGate.ClearSessionApprovals();
        ResetInstructionCaches();
        _registry.Outputs.Clear();
        UnloadToolPacks();
        Transcript.Clear();
        Undo.Clear();
    }

    private void ResetInstructionCaches()
    {
        _registry.ClearClassifierCache();
    }
}
