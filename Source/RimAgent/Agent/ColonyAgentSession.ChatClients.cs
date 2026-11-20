#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using RimAgent.Mcp;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Api;

namespace RimAgent.Agent;

public sealed partial class ColonyAgentSession
{
    /// <summary>記錄主模型最近一次回報的輸入 token；供應商未回報時維持未知。</summary>
    private sealed class TokenTrackingChatClient : DelegatingChatClient
    {
        private long _lastInputTokens = -1;
        private string _lastModel;
        public string LastModel => Volatile.Read(ref _lastModel);
        public void ResetModel() => Volatile.Write(ref _lastModel, null);
        public Action<ChatResponse> ResponseObserved { get; set; }

        public TokenTrackingChatClient(IChatClient innerClient) : base(innerClient) { }

        public long? LastInputTokens
        {
            get
            {
                long value = Interlocked.Read(ref _lastInputTokens);
                return value < 0 ? (long?)null : value;
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _lastInputTokens, -1);
            ResetModel();
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _lastModel, response?.ModelId);
            long? tokens = response?.Usage?.InputTokenCount;
            Interlocked.Exchange(ref _lastInputTokens, tokens.HasValue && tokens.Value >= 0 ? tokens.Value : -1);
            try { ResponseObserved?.Invoke(response); }
            catch { /* 選用紀錄失敗不影響模型回覆。 */ }
            return response;
        }
    }

    /// <summary>
    /// 記下工具迴圈最近一步實際使用的 ChatOptions。掛在迴圈內側：迴圈可能中途換成複本
    /// （ConversationId 改變、最後一輪剝除工具），每一步送出時記的都是當下那份。
    /// load_toolpack 在這一步的工具執行期間改它；迴圈下一步重讀（或複製）options.Tools，新清單就生效。
    /// </summary>
    private sealed class RunOptionsCapture : DelegatingChatClient
    {
        private ChatOptions _current;

        public RunOptionsCapture(IChatClient innerClient) : base(innerClient) { }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _current, options);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public void AddTools(IEnumerable<AITool> tools)
        {
            ChatOptions options = Volatile.Read(ref _current);
            if (options == null) return;
            var list = new List<AITool>(options.Tools ?? Array.Empty<AITool>());
            list.AddRange(tools.Except(list));
            options.Tools = list;
        }
    }

    /// <summary>
    /// 把送進壓縮器的訊息換成複本（連 AdditionalProperties 一起複製），原物件不被改動。
    /// </summary>
    /// <remarks>
    /// 補救 Microsoft.Agents.AI 1.28.0：<c>CompactionProvider</c> 掛在 ChatClientBuilder 層時，會把
    /// 這一輪新進的訊息直接改寫來源標記為 ChatHistory（就地寫入 <see cref="ChatMessage.AdditionalProperties"/>）。
    /// 那些正是 ChatClientAgent 稍後要存進歷史的同一批物件，而它只存非 ChatHistory 來源的請求訊息，
    /// 於是玩家這句指令從存量歷史消失。重現：CompactionTests.StoredHistoryIsTrimmedOnUserBoundaryPastTwiceTheLimit、
    /// ColonyAgentSessionTests.SecondInstructionCarriesTheWholeHistory（拿掉這層即失敗）。
    /// <see cref="ChatMessage.Clone"/> 是淺複製、字典共用，所以字典也要另複製。SDK 升級後重新驗證是否仍需要。
    /// </remarks>
    private sealed class MessageIsolatingChatClient : DelegatingChatClient
    {
        public MessageIsolatingChatClient(IChatClient innerClient) : base(innerClient) { }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
            => base.GetResponseAsync(Isolate(messages), options, cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
            => base.GetStreamingResponseAsync(Isolate(messages), options, cancellationToken);

        private static List<ChatMessage> Isolate(IEnumerable<ChatMessage> messages)
        {
            var copies = new List<ChatMessage>();
            foreach (ChatMessage message in messages)
            {
                ChatMessage copy = message.Clone();
                copy.AdditionalProperties = message.AdditionalProperties?.Clone();
                copies.Add(copy);
            }
            return copies;
        }
    }

    /// <summary>
    /// 等模型回覆期間把活動標成思考中，回覆後把思考內容寫進對話；主 Agent 與子代理的模型呼叫都經過它。
    /// 框架以 MEAI 原生的 <see cref="TextReasoningContent"/> 交還推理內容，顯示與否由呈現層決定。
    /// </summary>
    private sealed class ThinkingActivityClient : DelegatingChatClient
    {
        private readonly AgentActivityTracker _activity;
        private readonly AgentTranscript _transcript;

        /// <param name="transcript">思考內容寫入的對話；null 表示不記。</param>
        public ThinkingActivityClient(IChatClient innerClient, AgentActivityTracker activity, AgentTranscript transcript = null) : base(innerClient)
        {
            _activity = activity;
            _transcript = transcript;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ChatResponse response;
            using (_activity.Begin(AgentActivityKind.Thinking))
            {
                response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            RecordReasoning(response);
            return response;
        }

        private void RecordReasoning(ChatResponse response)
        {
            if (_transcript == null || response?.Messages == null) return;
            string reasoning = string.Join("\n\n", response.Messages
                .SelectMany(m => m.Contents)
                .OfType<TextReasoningContent>()
                .Select(r => r.Text?.Trim())
                .Where(t => !string.IsNullOrEmpty(t)));
            if (reasoning.Length == 0) return;
            _transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Reasoning,
                Text = reasoning,
                InstructionId = _transcript.CurrentInstructionId,
                Agent = _transcript.CurrentAgent
            });
        }
    }
}
