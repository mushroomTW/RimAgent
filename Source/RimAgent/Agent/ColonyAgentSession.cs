using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using RimAgent.Game;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent
{
    /// <summary>
    /// 一場遊戲期間的對話：歷史交給 Agent Framework 的 session，行動紀錄寫進 transcript，
    /// 「送出一句指令」即一次 <see cref="ChatClientAgent.RunAsync"/>。
    /// 多輪工具呼叫仍由框架的主線程包裝（inner）在單次 GetResponseAsync 內跑完；
    /// ChatClientAgent 以 UseProvidedChatClientAsIs 持有 inner，只做歷史拼接與回覆包裝，不另起工具迴圈。
    /// 同一時間只允許一個請求在飛。
    /// </summary>
    public sealed class ColonyAgentSession
    {
        public const string ModId = "greenmushroom.rimagent";
        public const float Temperature = 0.3f;
        // 高推理強度的供應商可能把推理 token 計入輸出上限；保留足夠空間給工具規劃與最終回覆。
        public const int MaxOutputTokens = 4000;
        private const int StoredHistoryMessageLimit = 60;
        /// <summary>自動折疊保留的最近組數（免 LLM，只折舊工具輸出）。</summary>
        private const int AutoFoldPreservedGroups = 6;
        /// <summary>手動 /compact 保留的最近組數：比自動折疊更積極，壓完只留最近一兩輪。</summary>
        private const int ManualSummaryPreservedGroups = 4;

        private readonly ChatClientAgent _agent;
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
        private readonly CompactionProvider _compaction;
#pragma warning restore MAAI001
        private readonly AgentToolRegistry _registry;
        private readonly Func<ToolPermissions> _permissions;
        private readonly IChatClient _inner;
        private readonly TokenUsageChatClient _tokenUsage;
        private readonly Func<string, int?> _contextWindowLookup;
        /// <summary>摘要用的原始 client：不經工具迴圈包裝，壓縮呼叫不會觸發工具執行。</summary>
        private readonly IChatClient _summarizer;
        private readonly IGameWorld _world;
        private readonly Func<string> _instructions;
        private readonly Func<string> _askNote;
        private AgentSession _session;
        private string _serializedSession;
        /// <summary>每句指令結束時備妥的存檔內容；存檔直接取用，不在主線程序列化，也不因忙碌而漏存。</summary>
        private string _saveSnapshot;
        private bool _snapshotWarningShown;
        private int _busy;
        private int _maxSteps;
        private RunLogRecord _activeRunLog;
        private bool _runLogWarningShown;

        /// <summary>可選的獨立執行紀錄；預設不記錄，由遊戲設定決定。</summary>
        public Func<bool> RunLoggingEnabled { get; set; }
        public Action<RunLogRecord> RunLogSink { get; set; }

        /// <param name="innerClient">框架交出的 IChatClient（測試時為假 client）；這裡再包上工具迴圈。</param>
        /// <param name="permissions">每次送出時讀取的權限，讓設定頁的變更下一句立即生效。</param>
        /// <param name="mode">執行模式；同樣每次送出時讀取。null 視為 <see cref="AgentMode.Autonomous"/>（命令全放行；排程器不在這層）。</param>
        /// <param name="instructions">每句送出時組裝的完整系統提示詞；null 則沿用核心提示詞。</param>
        /// <param name="mcp">MCP 管理器；null 則用停用的預設管理器。</param>
        /// <param name="classifierClient">自動模式分類器用的 client；null 則與 innerClient 共用（測試可另給假 client）。</param>
        /// <param name="classifierModel">自動模式分類器指定使用的模型識別碼 provider；null 則沿用框架預設。</param>
        /// <param name="classifierPrompt">分類器的系統提示詞（AgentPack/Prompts/RiskClassifier.md）；null 則分類器 fail-closed。</param>
        /// <param name="askNote">詢問模式附加註記（AgentPack/Prompts/AskMode.md）；null 則詢問模式不加註。</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "DI constructor with optional dependencies for testing")]
        public ColonyAgentSession(IChatClient innerClient, IGameWorld world, Func<ToolPermissions> permissions, int maxSteps, Func<AgentMode> mode = null, Func<string> instructions = null, McpManager mcp = null, IChatClient classifierClient = null, Func<string> classifierModel = null, Func<string> classifierPrompt = null, Func<string> askNote = null, Func<string, int?> contextWindowLookup = null)
        {
            if (innerClient == null) throw new ArgumentNullException(nameof(innerClient));
            if (world == null) throw new ArgumentNullException(nameof(world));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

            Transcript = new AgentTranscript();
            Undo = new WorkUndoStack();
            ConfirmGate = new ConfirmationGate(mode ?? (() => AgentMode.Autonomous));
            _world = world;
            _instructions = instructions;
            _askNote = askNote;
            _registry = new AgentToolRegistry(world, Transcript, Undo, ConfirmGate, mcp, new RiskClassifier(classifierClient ?? innerClient, classifierModel, classifierPrompt));
            _tokenUsage = new TokenUsageChatClient(innerClient);
            _contextWindowLookup = contextWindowLookup ?? RimLLMProvider.GetContextWindow;
            _tokenUsage.ResponseObserved = response => _activeRunLog?.AddModelResponse(response);
            _inner = _tokenUsage.AsMainThreadFunctionInvokingClient(maxSteps);
            _summarizer = innerClient;
            _maxSteps = NormalizeSteps(maxSteps);
            // 建構時不綁 tools：每輪經 RunOptions 傳，權限變更下一句生效。
            // 前情提要走 Instructions（AF 正統）；檔遺失時以一行保底代替，不拿空提示詞燒 API。
#pragma warning disable MAAI001 // AF 1.21 將壓縮 API 標記為評估中；已鎖定相依版本並以回歸測試保護。
            // 只折疊送出請求裡較舊的工具輸出，保留最近的對話、使用者意圖與工具呼叫。
            // 折疊不動 session 內存的歷史；存量上限另由 TrimStoredHistory 管。
            // 觸發條件讀即時設定（ShouldAutoCompact），設定頁調整下一句生效，不必重建會話。
            _compaction = new CompactionProvider(new ToolResultCompactionStrategy(
                ShouldAutoCompact,
                AutoFoldPreservedGroups));
            _agent = new ChatClientAgent(_inner, new ChatClientAgentOptions
            {
                Name = "ColonyAgent",
                ChatOptions = new ChatOptions { Instructions = AgentSystemPrompt.FallbackPreamble },
                AIContextProviders = new[] { _compaction },
                UseProvidedChatClientAsIs = true
            });
#pragma warning restore MAAI001
        }

        public AgentTranscript Transcript { get; }

        /// <summary>工作設定復原堆疊（工作優先級與時間表）。UI 的復原鍵經此操作。</summary>
        public WorkUndoStack Undo { get; }

        /// <summary>命令閘門：命令工具經此決定執行或預覽。</summary>
        public ConfirmationGate ConfirmGate { get; }

        /// <summary>目前的執行模式（每次讀取設定）。</summary>
        public AgentMode Mode => ConfirmGate.Mode;

        private string _pendingConfirmation;
        private string _pendingGoalObjective;
        private string _confirmedGoalObjective;
        private string _goalConfirmationReport;

        /// <summary>上一句是否有被擋下的命令在等玩家確認（有則 UI 顯示確認鍵）。</summary>
        public bool HasPendingConfirmation => _pendingConfirmation != null;

        /// <summary>等待中的確認是否由目標執行器提出，供目標指令避免取消玩家自己的命令。</summary>
        public bool HasPendingGoalConfirmation => _pendingConfirmation != null && _pendingGoalObjective != null;

        /// <summary>查看確認結果但先保留，避免模型請求失敗時遺失。</summary>
        public string PeekGoalConfirmationReport(string objective)
        {
            return _confirmedGoalObjective == objective ? _goalConfirmationReport : null;
        }

        /// <summary>取走指定目標上一次確認的實際執行結果，供下一輪模型續跑。</summary>
        public string TakeGoalConfirmationReport(string objective)
        {
            if (PeekGoalConfirmationReport(objective) == null) return null;
            string report = _goalConfirmationReport;
            _confirmedGoalObjective = null;
            _goalConfirmationReport = null;
            return report;
        }

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        /// <summary>模型思考強度；每句指令組請求時讀取，改完下一句生效。由 GameComponent 從設定同步。</summary>
        public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Auto;

        /// <summary>
        /// 優先使用的模型（"供應商:模型"）；空白代表交給框架備援鏈。每句指令組請求時讀取，由 GameComponent 從設定同步。
        /// 只影響玩家指令與自主任務，風險分類器與 /compact 摘要仍各走自己的設定。
        /// </summary>
        public string PreferredModel { get; set; }

        /// <summary>工具迴圈的迭代上限；正式遊戲傳入 int.MaxValue，僅測試或單輪預算另行縮小。</summary>
        public int MaxSteps => Volatile.Read(ref _maxSteps);

        /// <summary>自動壓縮開關。設定頁變更後由 GameComponent 經 <see cref="SyncAutoCompact"/> 同步，觸發條件每輪讀取即時值。</summary>
        public bool AutoCompactEnabled { get; set; } = true;

        /// <summary>自動壓縮門檻，為模型上下文上限的百分比。</summary>
        public int AutoCompactThreshold { get; set; } = 90;

        /// <summary>
        /// 標題列計量用的快照：歷史則數、視窗上限與設定百分比。UI 不直接碰歷史與設定。
        /// </summary>
        public readonly struct ContextStatus
        {
            public readonly int Messages;
            public readonly int Threshold;
            public readonly int? ContextWindow;
            public readonly bool AutoCompactEnabled;
            public readonly long? LastInputTokens;

            public ContextStatus(int messages, int threshold, bool autoCompactEnabled, long? lastInputTokens, int? contextWindow)
            {
                Messages = messages;
                Threshold = Math.Max(1, threshold);
                AutoCompactEnabled = autoCompactEnabled;
                LastInputTokens = lastInputTokens;
                ContextWindow = contextWindow;
            }

            /// <summary>最近一次供應商回報用量占模型視窗的比例；任一數值未知時無法計算。</summary>
            public double? UsageFraction => LastInputTokens.HasValue && ContextWindow.HasValue
                ? (double)LastInputTokens.Value / ContextWindow.Value : (double?)null;
        }

        /// <summary>給 GameComponent 的單一同步點：開關與門檻永遠成對更新。</summary>
        public void SyncAutoCompact(bool enabled, int threshold)
        {
            AutoCompactEnabled = enabled;
            AutoCompactThreshold = threshold;
        }

        /// <summary>主模型最近一次回報的輸入 token、歷史則數及框架提供的視窗上限，供 UI 顯示。</summary>
        public ContextStatus GetContextStatus()
        {
            return new ContextStatus(History.Count, AutoCompactThreshold, AutoCompactEnabled,
                _tokenUsage.LastInputTokens, GetContextWindow());
        }

        /// <summary>
        /// 以 AF 估算的模型歷史 token 數觸發；保留輸出空間，避免等到視窗滿才折疊。
        /// 框架查不到模型視窗時不猜上限，仍可用 /compact 手動摘要。
        /// </summary>
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
        internal bool ShouldAutoCompact(CompactionMessageIndex index)
        {
            if (index == null || !AutoCompactEnabled) return false;
            int? window = GetContextWindow();
            if (!window.HasValue) return false;
            long budget = Math.Min((long)window.Value * Math.Max(1, AutoCompactThreshold) / 100,
                (long)window.Value - MaxOutputTokens);
            return budget > 0 && index.TotalTokenCount >= budget;
        }

        private int? GetContextWindow()
        {
            try
            {
                string model = PreferredModel;
                if (string.IsNullOrWhiteSpace(model))
                {
                    model = RimLLMProvider.GetFallbackChain().FirstOrDefault();
                }
                return _contextWindowLookup(model?.Trim());
            }
            catch (InvalidOperationException)
            {
                // 框架尚未初始化時，視窗上限暫不可用。
                return null;
            }
        }
#pragma warning restore MAAI001

        /// <summary>
        /// 更新工具迴圈的迭代上限，供有明確單輪預算的呼叫端使用。
        /// 重設框架屬性不影響 AF session 的歷史。
        /// </summary>
        public void UpdateMaxSteps(int maxSteps)
        {
            int normalized = NormalizeSteps(maxSteps);
            if (Interlocked.Exchange(ref _maxSteps, normalized) == normalized) return;
            if (_inner is FunctionInvokingChatClient invoking)
            {
                invoking.MaximumIterationsPerRequest = normalized;
            }
        }

        private static int NormalizeSteps(int maxSteps)
        {
            return Math.Max(1, maxSteps);
        }

        /// <summary>
        /// 送給模型的完整歷史（含工具呼叫與結果），直接讀 AF session 內的存貨，供測試檢查脈絡是否延續。
        /// </summary>
        public IReadOnlyList<ChatMessage> History
        {
            get
            {
                AgentSession session = _session;
                var provider = _agent.ChatHistoryProvider as InMemoryChatHistoryProvider;
                if (session == null || provider == null) return Array.Empty<ChatMessage>();
                return provider.GetMessages(session);
            }
        }

        /// <summary>
        /// 送出一句玩家指令並等待 agent 做完。回傳最後一則 assistant 文字。
        /// 失敗時把錯誤寫進 transcript 並回傳 null；失敗那一輪的使用者訊息不會進歷史
        /// （AF 的 ChatHistoryProvider 在例外時略過儲存，等同以往的手動回滾）。
        /// </summary>
        /// <param name="autonomous">這句是排程器起的自主任務；只影響對話裡的標示，模型看到的內容相同。</param>
        /// <param name="maxToolSteps">本輪額外步數上限；自主排程器用它把單輪呼叫夾在剩餘每日預算內。</param>
        public async Task<string> SendAsync(
            string userText,
            CancellationToken cancellationToken = default,
            bool autonomous = false,
            int? maxToolSteps = null,
            bool goalRun = false,
            string displayText = null)
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }

            string text = userText.Trim();
            // 新指令取代等待中的確認，先記錄捨棄，再建立新指令的編號。
            DiscardPendingConfirmation();
            int instructionId = Transcript.BeginInstruction();
            RunLogRecord runLog = RunLoggingEnabled?.Invoke() == true ? new RunLogRecord
            {
                Instruction = RunLogRecord.Limit(displayText ?? text),
                Mode = Mode.ToString(),
                Source = goalRun ? "goal" : autonomous ? "autonomous" : "player"
            } : null;
            _activeRunLog = runLog;
            string runOutcome = "finished";
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.User,
                Text = displayText ?? text,
                InstructionId = instructionId,
                Autonomous = autonomous,
                GoalRun = goalRun
            });
            Undo.BeginFrame();
            ConfirmGate.BeginToolBudget(maxToolSteps);
            ApplyRunStepLimit(maxToolSteps);

            try
            {
                // 指令開始前的遊戲錯誤與這句無關，先丟掉；之後每次工具呼叫只回報這段期間新增的。
                _world.TakeNewGameErrors();
                if (_session == null)
                {
                    _session = RestoreOrCreateSession(cancellationToken);
                }

                AgentResponse response;
                try
                {
                    response = await _agent
                        .RunAsync(text, _session, new ChatClientAgentRunOptions(BuildRunOptions()), cancellationToken)
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
                    _session = await _agent.CreateSessionAsync(cancellationToken).AsTask().ConfigureAwait(false);
                    Transcript.Add(new TranscriptEntry
                    {
                        Kind = TranscriptKind.Notice,
                        Text = "Conversation context was too long; older model history was cleared and this instruction was retried.",
                        InstructionId = instructionId
                    });
                    response = await _agent
                        .RunAsync(text, _session, new ChatClientAgentRunOptions(BuildRunOptions()), cancellationToken)
                        .ConfigureAwait(false);
                }

                string reply = FinalAssistantText(response);
                if (goalRun) runOutcome = "goal_reported_" + GoalProgress.ParseStatus(reply).ToString().ToLowerInvariant();
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Assistant,
                    Text = goalRun ? GoalProgress.VisibleReply(reply) : reply,
                    InstructionId = instructionId,
                    ToolsStripped = IsToolsStripped(response)
                });
                if (IsToolsStripped(response))
                {
                    Transcript.Add(new TranscriptEntry
                    {
                        Kind = TranscriptKind.Notice,
                        Text = "The tool-step budget was reached before all planned actions completed. Ask a follow-up instruction to continue.",
                        InstructionId = instructionId
                    });
                }
                Undo.EndFrame(text);
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
                return reply;
            }
            catch (Exception ex)
            {
                runOutcome = ex is OperationCanceledException ? "cancelled" : "error";
                // 失敗那句可能只做了一半，但做了的部分仍該能復原；空框會被 EndFrame 丟掉。
                Undo.EndFrame(text + " (failed)");
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Error,
                    ErrorKind = Classify(ex),
                    Text = ex.Message,
                    InstructionId = instructionId
                });
                return null;
            }
            finally
            {
                TryWriteRunLog(runLog, instructionId, runOutcome);
                _activeRunLog = null;
                ConfirmGate.EndToolBudget();
                ApplyRunStepLimit(null);
                PrepareSaveSnapshot();
                Volatile.Write(ref _busy, 0);
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

        private void ApplyRunStepLimit(int? overrideSteps)
        {
            if (!(_inner is FunctionInvokingChatClient invoking)) return;
            int configured = MaxSteps;
            invoking.MaximumIterationsPerRequest = overrideSteps.HasValue
                ? Math.Max(1, Math.Min(configured, overrideSteps.Value))
                : configured;
        }

        public void Clear()
        {
            if (IsBusy) throw new InvalidOperationException("Cannot clear while RimAgent is working.");
            // 丟棄 AF session（含歷史）；下一次 SendAsync 重建。復原堆疊跟著清掉：
            // 對話歸零後「上一句」已無所指，留著只會讓人困惑。等待中的確認一併作廢。
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
            Transcript.Clear();
            Undo.Clear();
        }

        /// <summary>
        /// 手動壓縮（/compact）：用一次 LLM 摘要把較舊的模型歷史折成一則摘要，只保留最近幾組；
        /// 可見的對話紀錄（transcript）、復原堆疊與等待中的確認不受影響。忙碌中不可壓縮。
        /// 沒有歷史可壓、或壓完一則沒少（AF 策略會吞掉摘要失敗並原樣回傳）時寫 Notice 並回傳 null；
        /// 其他例外記一則 Error。任何情況下歷史都不會丟失。
        /// 注意：此處不呼叫 Verse 翻譯（背景續行執行緒不安全），訊息維持英文，
        /// 與 <see cref="ConfirmAsync"/>、<see cref="UndoLast"/> 的 session 層訊息一致。
        /// </summary>
        public async Task<string> CompactAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }
            try
            {
                var provider = _agent.ChatHistoryProvider as InMemoryChatHistoryProvider;
                List<ChatMessage> before = provider != null && _session != null
                    ? provider.GetMessages(_session).ToList()
                    : new List<ChatMessage>();
                if (before.Count == 0)
                {
                    AddNotice("Nothing to compact: model history is empty.");
                    return null;
                }
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
                var strategy = new SummarizationCompactionStrategy(
                    _summarizer,
                    CompactionTriggers.Always,
                    ManualSummaryPreservedGroups,
                    null,
                    null);
                IList<ChatMessage> after = (await CompactionProvider.CompactAsync(strategy, before, null, cancellationToken).ConfigureAwait(false)).ToList();
#pragma warning restore MAAI001
                // 摘要只會把多組折成一則，不可能變多；一樣多代表沒折到
                // （歷史太短，或策略吞掉了摘要失敗），誠實回報而不是謊稱成功。
                if (after.Count >= before.Count)
                {
                    AddNotice($"Already compact: model history has {before.Count} messages, nothing to fold.");
                    return null;
                }
                provider.SetMessages(_session, after.ToList());
                _tokenUsage.Reset();
                string summary = $"Context compacted: model history {before.Count} -> {after.Count} messages. Visible log kept.";
                AddNotice(summary);
                return summary;
            }
            catch (Exception ex)
            {
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Error,
                    ErrorKind = Classify(ex),
                    Text = ex.Message,
                    InstructionId = Transcript.CurrentInstructionId
                });
                return null;
            }
            finally
            {
                PrepareSaveSnapshot();
                Volatile.Write(ref _busy, 0);
            }
        }

        /// <summary>Compact 系 Notice 的單一入口，免得每次重複 new TranscriptEntry。</summary>
        private void AddNotice(string text)
        {
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = text,
                InstructionId = Transcript.CurrentInstructionId
            });
        }

        /// <summary>
        /// 執行等待中的確認：執行預覽當下捕捉的原工具呼叫與引數，不再重送模型。
        /// 沒有等待中的確認回傳 null。
        /// </summary>
        /// <param name="rememberForSession">
        /// 把這批工具記成「本局不再問」：之後確認模式下同名工具直接執行。
        /// 只記工具名稱，重開遊戲即失效；MCP 工具永遠不記。
        /// </param>
        public async Task<string> ConfirmAsync(CancellationToken cancellationToken = default, bool rememberForSession = false)
        {
            string text = _pendingConfirmation;
            if (text == null || !ConfirmGate.HasPendingApprovals) return null;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }
            int instructionId = Transcript.CurrentInstructionId;
            RunLogRecord confirmLog = RunLoggingEnabled?.Invoke() == true ? new RunLogRecord
            {
                Instruction = RunLogRecord.Limit(text),
                Mode = Mode.ToString(),
                Source = "confirmation"
            } : null;
            string goalObjective = _pendingGoalObjective;
            string confirmOutcome = "confirmation_failed";
            try
            {
                Undo.BeginFrame();
                _world.TakeNewGameErrors();
                int executed = await ConfirmGate.ApprovePendingAsync(cancellationToken, rememberForSession).ConfigureAwait(false);
                Undo.EndFrame(text + " (confirmed)");
                _pendingConfirmation = null;
                _pendingGoalObjective = null;
                string summary = $"Executed {executed} confirmed command(s).";
                if (rememberForSession) summary += " Those tools will not ask again this session.";
                if (goalObjective != null)
                {
                    _confirmedGoalObjective = goalObjective;
                    _goalConfirmationReport = BuildGoalConfirmationReport(summary, instructionId);
                }
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = summary,
                    InstructionId = Transcript.CurrentInstructionId
                });
                confirmOutcome = "confirmed";
                return summary;
            }
            catch (Exception ex)
            {
                Undo.EndFrame(text + " (confirmation failed)");
                _pendingConfirmation = null;
                _pendingGoalObjective = null;
                if (goalObjective != null)
                {
                    _confirmedGoalObjective = goalObjective;
                    _goalConfirmationReport = BuildGoalConfirmationReport("Confirmation failed: " + ex.Message, instructionId);
                }
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Error,
                    ErrorKind = Classify(ex),
                    Text = ex.Message,
                    InstructionId = Transcript.CurrentInstructionId
                });
                return null;
            }
            finally
            {
                TryWriteRunLog(confirmLog, instructionId, confirmOutcome);
                Volatile.Write(ref _busy, 0);
            }
        }

        /// <summary>丟掉等待中的確認（玩家送新指令時由 SendAsync 自動呼叫；UI 的取消鍵也走這裡）。</summary>
        public void DiscardPendingConfirmation()
        {
            if (_pendingConfirmation == null) return;
            string instruction = _pendingConfirmation;
            int instructionId = Transcript.CurrentInstructionId;
            _pendingConfirmation = null;
            _pendingGoalObjective = null;
            ConfirmGate.DiscardPendingApprovals();
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Pending confirmation discarded.",
                InstructionId = Transcript.CurrentInstructionId
            });
            RunLogRecord discardLog = RunLoggingEnabled?.Invoke() == true ? new RunLogRecord
            {
                Instruction = RunLogRecord.Limit(instruction),
                Mode = Mode.ToString(),
                Source = "confirmation"
            } : null;
            TryWriteRunLog(discardLog, instructionId, "discarded");
        }

        private string BuildGoalConfirmationReport(string summary, int instructionId)
        {
            var lines = new List<string> { summary };
            foreach (TranscriptEntry entry in Transcript.Snapshot().Where(e =>
                e.InstructionId == instructionId && e.Kind == TranscriptKind.Tool))
            {
                lines.Add(entry.ToolName + " (" + entry.Arguments + "): " +
                    (entry.Failed ? "failed: " : string.Empty) + RunLogRecord.Limit(entry.Result));
            }
            string report = string.Join("\n", lines);
            return report.Length <= 4000 ? report : report.Substring(0, 4000) + "…";
        }

        /// <summary>
        /// 供 GameComponent 在存檔時保存 AF 的模型歷史與工具呼叫內容。
        /// 讀檔後還沒送過指令時 AF session 尚未重建，原樣帶回讀進來的內容；
        /// 其餘情況用上一句指令結束時備妥的快照（忙碌中存檔也拿得到上一句為止的歷史）。
        /// </summary>
        public string SerializeForSave()
        {
            if (_session == null) return _serializedSession;
            string snapshot = Volatile.Read(ref _saveSnapshot);
            if (snapshot != null || IsBusy) return snapshot;
            // 快照失敗過（見 PrepareSaveSnapshot）才走到這裡：存檔當下同步序列化一次。
            try
            {
                return SerializeSession(_session);
            }
            catch (Exception ex)
            {
                // 存檔不能因框架升級或供應商資料不相容而失敗；保留 transcript 作為兜底。
                Verse.Log.Warning("RimAgent: failed to serialize agent session for save: " + ex.Message);
                return null;
            }
        }

        public void RestoreSerializedSession(string serialized)
        {
            if (IsBusy) throw new InvalidOperationException("Cannot restore while RimAgent is working.");
            _serializedSession = string.IsNullOrWhiteSpace(serialized) ? null : serialized;
            // 讀進來的內容先當快照：讀檔後第一句還在跑時存檔，也不會漏掉舊歷史。
            _saveSnapshot = _serializedSession;
            _session = null;
        }

        private string SerializeSession(AgentSession session)
        {
            JsonElement document = _agent.SerializeSessionAsync(session, null, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            return document.GetRawText();
        }

        /// <summary>
        /// 指令或 /compact 結束、仍持有忙碌旗標時呼叫（通常在背景續行上，不佔主線程）：
        /// 裁掉超量的舊歷史、丟掉壓縮器的分組快取，再把 session 序列化成存檔快照。
        /// 分組快取裡是另一份完整歷史，存進檔案只會讓存檔翻倍；下次請求時壓縮器會從歷史重建。
        /// </summary>
        private void PrepareSaveSnapshot()
        {
            AgentSession session = _session;
            if (session == null) return;
            try
            {
                TrimStoredHistory(session);
#pragma warning disable MAAI001 // 同建構式：AF 1.21 壓縮 API 評估中，鎖定版本並以回歸測試保護。
                foreach (string key in _compaction.StateKeys) session.StateBag.TryRemoveValue(key);
#pragma warning restore MAAI001
                Volatile.Write(ref _saveSnapshot, SerializeSession(session));
            }
            catch (Exception ex)
            {
                // 快照失敗不影響這句指令；清掉舊快照，存檔時改走同步序列化。
                // 裁歷史失敗時同步序列化可能照樣成功，所以這裡自己記一次，免得存量上限失效卻沒人知道。
                Volatile.Write(ref _saveSnapshot, null);
                if (_snapshotWarningShown) return;
                _snapshotWarningShown = true;
                Verse.Log.Warning("RimAgent: failed to prepare agent session save snapshot: " + ex.Message);
            }
        }

        /// <summary>
        /// 自動壓縮開啟時的存量上限：請求端的折疊只縮送出的內容（且摘要仍含工具輸出全文），
        /// session 內存的歷史不會變少，會隨遊戲無限成長並拖慢存讀檔。
        /// 超過門檻兩倍時，從最舊的完整一輪往後裁到門檻以內；只在玩家訊息處下刀，
        /// 工具呼叫與結果不會被拆開，也不會留下開頭不是玩家訊息的歷史（部分供應商會拒收）。
        /// 最近一輪本身就超過門檻時只留最近一輪。
        /// </summary>
        private void TrimStoredHistory(AgentSession session)
        {
            if (!AutoCompactEnabled) return;
            var provider = _agent.ChatHistoryProvider as InMemoryChatHistoryProvider;
            if (provider == null) return;
            List<ChatMessage> messages = provider.GetMessages(session);
            int threshold = StoredHistoryMessageLimit;
            if (messages.Count <= threshold * 2) return;

            int start = -1;
            for (int i = 1; i < messages.Count; i++)
            {
                if (messages[i].Role != ChatRole.User) continue;
                start = i;
                if (messages.Count - i <= threshold) break;
            }
            if (start <= 0) return;
            provider.SetMessages(session, messages.GetRange(start, messages.Count - start));
        }

        private AgentSession RestoreOrCreateSession(CancellationToken cancellationToken)
        {
            string serialized = _serializedSession;
            _serializedSession = null;
            if (!string.IsNullOrWhiteSpace(serialized))
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(serialized))
                    {
                        return _agent.DeserializeSessionAsync(document.RootElement, null, cancellationToken)
                            .AsTask().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    // 舊版存檔或 Agent Framework 升級後無法還原時，安全地退回新會話。
                    Verse.Log.Warning("RimAgent: failed to restore agent session, starting fresh: " + ex.Message);
                }
            }
            return _agent.CreateSessionAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 把上一個有工作設定改動的指令復原（工作優先級與時間表寫回舊值）。
        /// 忙碌中不可復原；沒東西可復原回傳 null。呼叫者應在主線程（遊戲內由 UI 按鍵觸發）。
        /// </summary>
        public string UndoLast()
        {
            if (IsBusy) throw new InvalidOperationException("Cannot undo while RimAgent is working.");
            UndoFrame frame = Undo.Pop();
            if (frame == null) return null;

            Dictionary<int, string> names = _world.GetColonists().ToDictionary(c => c.Id, c => c.Name);
            var failures = new List<string>();
            int restored = 0;
            for (int i = frame.Priorities.Count - 1; i >= 0; i--)
            {
                UndoWorkPriority p = frame.Priorities[i];
                CommandResult r = _world.SetWorkPriority(p.PawnId, p.WorkTypeDefName, p.OldPriority);
                if (r.Success) restored++;
                else failures.Add($"{NameOf(names, p.PawnId)} {p.WorkTypeDefName}: {r.Message}");
            }
            for (int i = frame.Assignments.Count - 1; i >= 0; i--)
            {
                UndoTimeAssignment a = frame.Assignments[i];
                CommandResult r = _world.SetTimeAssignment(a.PawnId, a.Hour, a.OldAssignmentDefName);
                if (r.Success) restored++;
                else failures.Add($"{NameOf(names, a.PawnId)} hour {a.Hour}: {r.Message}");
            }

            string summary = $"Undid \"{frame.Label}\": restored {restored} work setting(s).";
            if (failures.Count > 0) summary += " Failures: " + string.Join("; ", failures);
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = summary,
                InstructionId = Transcript.CurrentInstructionId
            });
            return summary;
        }

        private static string NameOf(Dictionary<int, string> names, int pawnId)
        {
            return names.TryGetValue(pawnId, out string name) ? name : ("[" + pawnId + "]");
        }

        internal RimLLMChatOptions BuildRunOptions()
        {
            var options = new RimLLMChatOptions
            {
                Tools = _registry.Select(_permissions(), Mode),
                ToolMode = ChatToolMode.Auto,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens
            };
            ApplyReasoning(options, Reasoning);
            string model = PreferredModel;
            if (!string.IsNullOrWhiteSpace(model)) options.ModelId = model.Trim();
            // Run 層覆寫 agent 層的核心提示詞：擴充包內容變更後下一句立即生效，不必重建會話。
            string instructions = null;
            if (_instructions != null)
            {
                string composed = _instructions();
                if (!string.IsNullOrWhiteSpace(composed)) instructions = composed;
            }
            if (Mode == AgentMode.Ask)
            {
                // 詢問模式：命令工具根本不在清單裡，跟模型講清楚免得它道歉說類別被關掉。
                // 註記來自 AgentPack/Prompts/AskMode.md；缺檔就不加，模式本身的工具限制不受影響。
                string note = _askNote?.Invoke();
                if (!string.IsNullOrWhiteSpace(note))
                {
                    instructions = (instructions ?? AgentSystemPrompt.FallbackPreamble) + "\n\n" + note.Trim();
                }
            }
            if (instructions != null) options.Instructions = instructions;
            return options;
        }

        /// <summary>Auto 什麼都不設（框架補預設）；Off 走框架的 DisableReasoning；其餘交給 MEAI 的 ReasoningEffort。</summary>
        private static void ApplyReasoning(RimLLMChatOptions options, ReasoningLevel level)
        {
            switch (level)
            {
                case ReasoningLevel.Off:
                    options.DisableReasoning = true;
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

        /// <summary>迴圈中間的 assistant 訊息可能夾雜「讓我看看…」之類的文字；玩家要的是最後那則。</summary>
        private static string FinalAssistantText(AgentResponse response)
        {
            ChatMessage last = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text));
            return last != null ? last.Text.Trim() : (response.Text ?? string.Empty).Trim();
        }

        private static bool IsToolsStripped(AgentResponse response)
        {
            return (response.RawRepresentation as ChatResponse)?.WereToolsStripped() == true;
        }

        internal static AgentErrorKind Classify(Exception ex)
        {
            if (ex is OperationCanceledException) return AgentErrorKind.Cancelled;

            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (!(current is RimLLMException rim)) continue;
                switch (rim.Error)
                {
                    case LLMError.InvalidKey:
                    case LLMError.QuotaExceeded:
                    case LLMError.ProviderOffline:
                    case LLMError.ModelNotFound:
                        return AgentErrorKind.Configuration;
                    case LLMError.Timeout:
                    case LLMError.RateLimit:
                    case LLMError.NetworkError:
                        return AgentErrorKind.Transient;
                    case LLMError.ContextWindowExceeded:
                        return AgentErrorKind.ContextTooLong;
                    case LLMError.Cancelled:
                        return AgentErrorKind.Cancelled;
                    default:
                        return AgentErrorKind.Unknown;
                }
            }
            return AgentErrorKind.Unknown;
        }
    }
}
