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
        private AgentSession _session;
        private int _busy;

        /// <param name="innerClient">框架交出的 IChatClient（測試時為假 client）；這裡再包上工具迴圈。</param>
        /// <param name="permissions">每次送出時讀取的權限，讓設定頁的變更下一句立即生效。</param>
        public ColonyAgentSession(IChatClient innerClient, IGameWorld world, Func<ToolPermissions> permissions, int maxSteps)
        {
            if (innerClient == null) throw new ArgumentNullException(nameof(innerClient));
            if (world == null) throw new ArgumentNullException(nameof(world));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

            Transcript = new AgentTranscript();
            _registry = new AgentToolRegistry(world, Transcript);
            IChatClient inner = innerClient.AsMainThreadFunctionInvokingClient(maxSteps);
            // 建構時不綁 tools：每輪經 RunOptions 傳，權限變更下一句生效。
            // 系統提示走 Instructions（AF 正統）；CachedContext 留空，待實測 token 後再決定是否加回。
            _agent = new ChatClientAgent(inner, new ChatClientAgentOptions
            {
                Name = "ColonyAgent",
                ChatOptions = new ChatOptions { Instructions = AgentSystemPrompt.Text },
                UseProvidedChatClientAsIs = true
            });
        }

        public AgentTranscript Transcript { get; }

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

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
            Transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = text });

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
                    ToolsStripped = IsToolsStripped(response)
                });
                return reply;
            }
            catch (Exception ex)
            {
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Error,
                    ErrorKind = Classify(ex),
                    Text = ex.Message
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
            // 丟棄 AF session（含歷史）；下一次 SendAsync 重建。
            _session = null;
            Transcript.Clear();
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
