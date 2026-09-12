using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent
{
    /// <summary>
    /// 一場遊戲期間的對話：訊息歷史、行動紀錄與「送出一句指令」。
    /// 多輪工具呼叫全部交給 MEAI 的 FunctionInvokingChatClient（透過框架的主線程包裝），
    /// 這裡只負責組 options、收回覆、把結果寫進 transcript。
    /// 同一時間只允許一個請求在飛。
    /// </summary>
    public sealed class AgentSession
    {
        public const string ModId = "greenmushroom.rimagent";
        public const float Temperature = 0.3f;
        public const int MaxOutputTokens = 2000;

        private readonly IChatClient _client;
        private readonly AgentToolRegistry _registry;
        private readonly Func<ToolPermissions> _permissions;
        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private int _busy;

        /// <param name="innerClient">框架交出的 IChatClient（測試時為假 client）；這裡再包上工具迴圈。</param>
        /// <param name="permissions">每次送出時讀取的權限，讓設定頁的變更立即生效。</param>
        public AgentSession(IChatClient innerClient, IGameWorld world, Func<ToolPermissions> permissions, int maxSteps)
        {
            if (innerClient == null) throw new ArgumentNullException(nameof(innerClient));
            if (world == null) throw new ArgumentNullException(nameof(world));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

            Transcript = new AgentTranscript();
            _registry = new AgentToolRegistry(world, Transcript);
            _client = innerClient.AsMainThreadFunctionInvokingClient(maxSteps);
        }

        public AgentTranscript Transcript { get; }

        public bool IsBusy => Volatile.Read(ref _busy) == 1;

        /// <summary>送給模型的完整歷史（含工具呼叫與結果），供測試檢查脈絡是否延續。</summary>
        public IReadOnlyList<ChatMessage> History => _history;

        /// <summary>
        /// 送出一句玩家指令並等待 agent 做完。回傳最後一則 assistant 文字。
        /// 失敗時把錯誤寫進 transcript、移除這次的使用者訊息（保持歷史成對），並回傳 null。
        /// </summary>
        public async Task<string> SendAsync(string userText, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("RimAgent is still working on the previous instruction.");
            }

            var userMessage = new ChatMessage(ChatRole.User, userText.Trim());
            Transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = userMessage.Text });
            _history.Add(userMessage);

            try
            {
                ChatResponse response = await _client
                    .GetResponseAsync(_history, BuildOptions(), cancellationToken)
                    .ConfigureAwait(false);

                _history.AddRange(response.Messages);
                string reply = FinalAssistantText(response);
                Transcript.Add(new TranscriptEntry
                {
                    Kind = TranscriptKind.Assistant,
                    Text = reply,
                    ToolsStripped = response.WereToolsStripped()
                });
                return reply;
            }
            catch (Exception ex)
            {
                _history.Remove(userMessage);
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
            _history.Clear();
            Transcript.Clear();
        }

        internal ChatOptions BuildOptions()
        {
            return new RimLLMChatOptions
            {
                Tools = _registry.Select(_permissions()),
                ToolMode = ChatToolMode.Auto,
                CachedContext = AgentSystemPrompt.Text,
                Temperature = Temperature,
                MaxOutputTokens = MaxOutputTokens
            };
        }

        /// <summary>迴圈中間的 assistant 訊息可能夾雜「讓我看看…」之類的文字；玩家要的是最後那則。</summary>
        private static string FinalAssistantText(ChatResponse response)
        {
            ChatMessage last = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text));
            return last != null ? last.Text.Trim() : (response.Text ?? string.Empty).Trim();
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
