using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace RimAgent.Agent
{
    /// <summary>記錄主模型最近一次回報的輸入 token；供應商未回報時維持未知。</summary>
    internal sealed class TokenUsageChatClient : DelegatingChatClient
    {
        private long _lastInputTokens = -1;
        public Action<ChatResponse> ResponseObserved { get; set; }

        public TokenUsageChatClient(IChatClient innerClient) : base(innerClient)
        {
        }

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
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            long? tokens = response?.Usage?.InputTokenCount;
            Interlocked.Exchange(ref _lastInputTokens, tokens.HasValue && tokens.Value >= 0 ? tokens.Value : -1);
            try { ResponseObserved?.Invoke(response); }
            catch { /* 選用紀錄失敗不影響模型回覆。 */ }
            return response;
        }
    }
}
