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
        private const int HistoryCompactionMessageThreshold = 60;
        private const int HistoryCompactionPreservedGroups = 6;

        private readonly ChatClientAgent _agent;
        private readonly AgentToolRegistry _registry;
        private readonly Func<ToolPermissions> _permissions;
        private readonly IChatClient _inner;
        private readonly IGameWorld _world;
        private readonly Func<string> _instructions;
        private AgentSession _session;
        private string _serializedSession;
        private int _busy;
        private int _maxSteps;

        /// <param name="innerClient">框架交出的 IChatClient（測試時為假 client）；這裡再包上工具迴圈。</param>
        /// <param name="permissions">每次送出時讀取的權限，讓設定頁的變更下一句立即生效。</param>
        /// <param name="mode">執行模式；同樣每次送出時讀取。null 視為 <see cref="AgentMode.Autonomous"/>（命令全放行；排程器不在這層）。</param>
        /// <param name="instructions">每句送出時組裝的完整系統提示詞；null 則沿用核心提示詞。</param>
        /// <param name="mcp">MCP 管理器；null 則用停用的預設管理器。</param>
        /// <param name="classifierClient">自動模式分類器用的 client；null 則與 innerClient 共用（測試可另給假 client）。</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "DI constructor with optional dependencies for testing")]
        public ColonyAgentSession(IChatClient innerClient, IGameWorld world, Func<ToolPermissions> permissions, int maxSteps, Func<AgentMode> mode = null, Func<string> instructions = null, McpManager mcp = null, IChatClient classifierClient = null)
        {
            if (innerClient == null) throw new ArgumentNullException(nameof(innerClient));
            if (world == null) throw new ArgumentNullException(nameof(world));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

            Transcript = new AgentTranscript();
            Undo = new WorkUndoStack();
            ConfirmGate = new ConfirmationGate(mode ?? (() => AgentMode.Autonomous));
            _world = world;
            _instructions = instructions;
            _registry = new AgentToolRegistry(world, Transcript, Undo, ConfirmGate, mcp, new RiskClassifier(classifierClient ?? innerClient));
            _inner = innerClient.AsMainThreadFunctionInvokingClient(maxSteps);
            _maxSteps = NormalizeSteps(maxSteps);
            // 建構時不綁 tools：每輪經 RunOptions 傳，權限變更下一句生效。
            // 系統提示走 Instructions（AF 正統）；CachedContext 留空，待實測 token 後再決定是否加回。
#pragma warning disable MAAI001 // AF 1.21 將壓縮 API 標記為評估中；已鎖定相依版本並以回歸測試保護。
            _agent = new ChatClientAgent(_inner, new ChatClientAgentOptions
            {
                Name = "ColonyAgent",
                ChatOptions = new ChatOptions { Instructions = AgentSystemPrompt.Text },
                // 只折疊較舊的工具輸出，保留最近的對話、使用者意圖與工具呼叫。
                // 這在一般遊戲過程中避免歷史無限成長，而不改變近期上下文。
                AIContextProviders = new[]
                {
                    new CompactionProvider(new ToolResultCompactionStrategy(
                        CompactionTriggers.MessagesExceed(HistoryCompactionMessageThreshold),
                        HistoryCompactionPreservedGroups))
                },
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

        /// <summary>上一句是否有被擋下的命令在等玩家確認（有則 UI 顯示確認鍵）。</summary>
        public bool HasPendingConfirmation => _pendingConfirmation != null;

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        /// <summary>模型思考強度；每句指令組請求時讀取，改完下一句生效。由 GameComponent 從設定同步。</summary>
        public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Auto;

        /// <summary>目前每句指令的工具呼叫上限。設定頁變更後由 GameComponent 經此同步，不必重讀存檔。</summary>
        public int MaxSteps => Volatile.Read(ref _maxSteps);

        /// <summary>
        /// 把設定頁的步數上限同步進工具迴圈（框架的 FunctionInvokingChatClient 持有的是可寫屬性，
        /// 重設它即可，AF session 的歷史不受影響）。值相同時不做事，每幀呼叫也無妨。
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
            int? maxToolSteps = null)
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }

            string text = userText.Trim();
            int instructionId = Transcript.BeginInstruction();
            Transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = text, InstructionId = instructionId, Autonomous = autonomous });
            Undo.BeginFrame();
            // 新指令取代舊的等待：之前沒按的確認自動作廢，玩家以最新一句為準。
            _pendingConfirmation = null;
            ConfirmGate.DiscardPendingApprovals();
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
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Assistant,
                    Text = reply,
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
                ConfirmGate.EndToolBudget();
                ApplyRunStepLimit(null);
                Volatile.Write(ref _busy, 0);
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
            _pendingConfirmation = null;
            ConfirmGate.DiscardPendingApprovals();
            Transcript.Clear();
            Undo.Clear();
        }

        /// <summary>
        /// 執行等待中的確認：執行預覽當下捕捉的原工具呼叫與引數，不再重送模型。
        /// 沒有等待中的確認回傳 null。
        /// </summary>
        public async Task<string> ConfirmAsync(CancellationToken cancellationToken = default)
        {
            string text = _pendingConfirmation;
            if (text == null || !ConfirmGate.HasPendingApprovals) return null;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }
            try
            {
                Undo.BeginFrame();
                _world.TakeNewGameErrors();
                int executed = await ConfirmGate.ApprovePendingAsync(cancellationToken).ConfigureAwait(false);
                Undo.EndFrame(text + " (confirmed)");
                _pendingConfirmation = null;
                string summary = $"Executed {executed} confirmed command(s).";
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Notice,
                    Text = summary,
                    InstructionId = Transcript.CurrentInstructionId
                });
                return summary;
            }
            catch (Exception ex)
            {
                Undo.EndFrame(text + " (confirmation failed)");
                _pendingConfirmation = null;
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
                Volatile.Write(ref _busy, 0);
            }
        }

        /// <summary>丟掉等待中的確認（玩家送新指令時由 SendAsync 自動呼叫；UI 的取消鍵也走這裡）。</summary>
        public void DiscardPendingConfirmation()
        {
            if (_pendingConfirmation == null) return;
            _pendingConfirmation = null;
            ConfirmGate.DiscardPendingApprovals();
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Pending confirmation discarded.",
                InstructionId = Transcript.CurrentInstructionId
            });
        }

        /// <summary>供 GameComponent 在存檔時保存 AF 的模型歷史與工具呼叫內容。</summary>
        public string SerializeForSave()
        {
            if (_session == null || IsBusy) return null;
            try
            {
                JsonElement document = _agent.SerializeSessionAsync(_session, null, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                return document.GetRawText();
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
            _session = null;
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
                instructions = (instructions ?? AgentSystemPrompt.Text) + AgentSystemPrompt.AskModeNote;
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
