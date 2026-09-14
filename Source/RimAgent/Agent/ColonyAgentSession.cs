using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using RimAgent.Game;
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
        public const int MaxOutputTokens = 2000;

        private readonly ChatClientAgent _agent;
        private readonly AgentToolRegistry _registry;
        private readonly Func<ToolPermissions> _permissions;
        private readonly IChatClient _inner;
        private readonly IGameWorld _world;
        private AgentSession _session;
        private int _busy;
        private int _maxSteps;

        /// <param name="innerClient">框架交出的 IChatClient（測試時為假 client）；這裡再包上工具迴圈。</param>
        /// <param name="permissions">每次送出時讀取的權限，讓設定頁的變更下一句立即生效。</param>
        /// <param name="requireConfirmation">「需確認」模式是否開啟；同樣每次送出時讀取。</param>
        public ColonyAgentSession(IChatClient innerClient, IGameWorld world, Func<ToolPermissions> permissions, int maxSteps, Func<bool> requireConfirmation = null)
        {
            if (innerClient == null) throw new ArgumentNullException(nameof(innerClient));
            if (world == null) throw new ArgumentNullException(nameof(world));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

            Transcript = new AgentTranscript();
            Undo = new WorkUndoStack();
            ConfirmGate = new ConfirmationGate(requireConfirmation ?? (() => false));
            _world = world;
            _registry = new AgentToolRegistry(world, Transcript, Undo, ConfirmGate);
            _inner = innerClient.AsMainThreadFunctionInvokingClient(maxSteps);
            _maxSteps = NormalizeSteps(maxSteps);
            // 建構時不綁 tools：每輪經 RunOptions 傳，權限變更下一句生效。
            // 系統提示走 Instructions（AF 正統）；CachedContext 留空，待實測 token 後再決定是否加回。
            _agent = new ChatClientAgent(_inner, new ChatClientAgentOptions
            {
                Name = "ColonyAgent",
                ChatOptions = new ChatOptions { Instructions = AgentSystemPrompt.Text },
                UseProvidedChatClientAsIs = true
            });
        }

        public AgentTranscript Transcript { get; }

        /// <summary>工作設定復原堆疊（工作優先級與時間表）。UI 的復原鍵經此操作。</summary>
        public WorkUndoStack Undo { get; }

        /// <summary>「需確認」模式的閘門：Work／Build／Combat 工具經此決定執行或預覽。</summary>
        public ConfirmationGate ConfirmGate { get; }

        private string _pendingConfirmation;

        /// <summary>上一句是否有被擋下的命令在等玩家確認（有則 UI 顯示確認鍵）。</summary>
        public bool HasPendingConfirmation => _pendingConfirmation != null;

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

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
        public async Task<string> SendAsync(string userText, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }

            string text = userText.Trim();
            int instructionId = Transcript.BeginInstruction();
            Transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = text, InstructionId = instructionId });
            Undo.BeginFrame();
            // 新指令取代舊的等待：之前沒按的確認自動作廢，玩家以最新一句為準。
            _pendingConfirmation = null;

            try
            {
                if (_session == null)
                {
                    _session = await _agent.CreateSessionAsync(cancellationToken).AsTask().ConfigureAwait(false);
                }

                AgentResponse response = await _agent
                    .RunAsync(text, _session, new ChatClientAgentRunOptions(BuildRunOptions()), cancellationToken)
                    .ConfigureAwait(false);

                string reply = FinalAssistantText(response);
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Assistant,
                    Text = reply,
                    InstructionId = instructionId,
                    ToolsStripped = IsToolsStripped(response)
                });
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
                // 失敗那句不保證完整（工具可能只跑了一半），不推進復原堆疊。
                Undo.DiscardFrame();
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
                Volatile.Write(ref _busy, 0);
            }
        }

        public void Clear()
        {
            if (IsBusy) throw new InvalidOperationException("Cannot clear while RimAgent is working.");
            // 丟棄 AF session（含歷史）；下一次 SendAsync 重建。復原堆疊跟著清掉：
            // 對話歸零後「上一句」已無所指，留著只會讓人困惑。等待中的確認一併作廢。
            _session = null;
            _pendingConfirmation = null;
            ConfirmGate.ResetBypass();
            Transcript.Clear();
            Undo.Clear();
        }

        /// <summary>
        /// 執行等待中的確認：把上一句原樣重送一次，本輪閘門放行，模型會重新下相同的工具呼叫
        /// 並真正執行。沒有等待中的確認回傳 null。
        /// </summary>
        public async Task<string> ConfirmAsync(CancellationToken cancellationToken = default)
        {
            string text = _pendingConfirmation;
            if (text == null) return null;
            ConfirmGate.BypassOnce();
            try
            {
                return await SendAsync(text, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ConfirmGate.ResetBypass();
            }
        }

        /// <summary>丟掉等待中的確認（玩家送新指令時由 SendAsync 自動呼叫；UI 的取消鍵也走這裡）。</summary>
        public void DiscardPendingConfirmation()
        {
            if (_pendingConfirmation == null) return;
            _pendingConfirmation = null;
            Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Pending confirmation discarded.",
                InstructionId = Transcript.CurrentInstructionId
            });
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
            return new RimLLMChatOptions
            {
                Tools = _registry.Select(_permissions()),
                ToolMode = ChatToolMode.Auto,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens
            };
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
