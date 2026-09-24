extern alias bclasync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>
    /// 上下文壓縮測試：自動折疊的觸發條件（純邏輯）與手動 /compact（假 client + 真 AF 策略）。
    /// </summary>
    [TestFixture]
    public class CompactionTests
    {
        private FakeGameWorld _world;
        private ScriptedChatClient _client;
        private ToolPermissions _permissions;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _client = new ScriptedChatClient();
            _permissions = ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work);
        }

#pragma warning disable MAAI001 // AF 1.21 壓縮 API 評估中；測試直接覆蓋觸發條件。
        [Test]
        public void AutoCompactDefaultsToEnabledAtSixty()
        {
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            Assert.That(session.AutoCompactEnabled, Is.True);
            Assert.That(session.AutoCompactThreshold, Is.EqualTo(60));
        }

        [Test]
        public void SyncAutoCompactUpdatesBothAndStatusReflectsHistory()
        {
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            session.SyncAutoCompact(false, 20);

            Assert.That(session.AutoCompactEnabled, Is.False);
            Assert.That(session.AutoCompactThreshold, Is.EqualTo(20));
            ColonyAgentSession.ContextStatus empty = session.GetContextStatus();
            Assert.That(empty.Messages, Is.EqualTo(0));
            Assert.That(empty.Threshold, Is.EqualTo(20));
            Assert.That(empty.UsageFraction, Is.EqualTo(0.0));
        }

        [Test]
        public async Task ContextStatusCountsHistory()
        {
            _client.Reply("ok");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            await session.SendAsync("hello");

            ColonyAgentSession.ContextStatus status = session.GetContextStatus();
            Assert.That(status.Messages, Is.EqualTo(session.History.Count).And.GreaterThan(0));
            Assert.That(status.UsageFraction, Is.EqualTo((double)status.Messages / status.Threshold));
            Assert.That(status.LastInputTokens, Is.Null, "供應商未回報時不得把訊息數當 token");
        }

        [Test]
        public async Task ContextStatusUsesProviderInputTokensAndClearsWithSession()
        {
            _client.ReplyWithUsage("ok", 12345).Reply("usage omitted");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);

            await session.SendAsync("hello");

            Assert.That(session.GetContextStatus().LastInputTokens, Is.EqualTo(12345));
            await session.SendAsync("again");
            Assert.That(session.GetContextStatus().LastInputTokens, Is.Null, "下一次未回報用量時不可沿用舊數字");
            session.Clear();
            Assert.That(session.GetContextStatus().LastInputTokens, Is.Null);
        }

        /// <summary>
        /// 行為測試：同樣的長對話，開關開啟時送給模型的歷史含折疊標記且舊工具結果被收起；
        /// 關閉時舊工具結果全文仍在。直接覆蓋 trigger 接線與開關語意。
        /// </summary>
        [Test]
        public async Task AutoFoldAppliesOnlyWhenEnabled([Values(true, false)] bool enabled)
        {
            _client.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 }).Reply("done 1")
                   .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 2 }).Reply("done 2")
                   .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 3 }).Reply("done 3")
                   .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 }).Reply("done 4")
                   .Reply("hello back");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            session.AutoCompactEnabled = enabled;
            session.AutoCompactThreshold = 10;
            await session.SendAsync("set doctor 1");
            await session.SendAsync("set doctor 2");
            await session.SendAsync("set doctor 3");
            await session.SendAsync("set doctor 1");
            await session.SendAsync("hello");

            IList<ChatMessage> lastRequest = _client.ReceivedMessages.Last();
            string forwarded = string.Join("\n", lastRequest.Select(m => m.Text ?? string.Empty));
            // 工具結果住在 FunctionResultContent 裡（.Text 看不到），用數量判斷是否被折疊；
            // 折疊後的摘要是 assistant 文字，內含 "[Tool Calls]" 標記。
            int resultCount = lastRequest.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            if (enabled)
            {
                Assert.That(forwarded, Does.Contain("[Tool Calls]"), "舊工具輸出應被折成摘要");
                Assert.That(resultCount, Is.LessThan(4), "較舊的工具結果不應再以全文出現");
            }
            else
            {
                Assert.That(forwarded, Does.Not.Contain("[Tool Calls]"), "關閉時不應折疊");
                Assert.That(resultCount, Is.EqualTo(4), "關閉時歷史應全文保留");
            }
        }
#pragma warning restore MAAI001

        [Test]
        public async Task CompactWithEmptyHistoryReturnsNullAndNotices()
        {
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);

            string result = await session.CompactAsync();

            Assert.That(result, Is.Null);
            TranscriptEntry notice = session.Transcript.Snapshot().Last();
            Assert.That(notice.Kind, Is.EqualTo(TranscriptKind.Notice));
            Assert.That(notice.Text, Does.Contain("Nothing to compact"));
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task CompactSummarizesOldHistoryAndKeepsLog()
        {
            for (int i = 0; i < 6; i++) _client.Reply("ok " + i);
            _client.Reply("Summary of the colony so far.");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            for (int i = 0; i < 6; i++) await session.SendAsync("instruction " + i);
            int before = session.History.Count;
            Assert.That(before, Is.GreaterThan(8), "歷史要夠長，摘要才有東西可折");

            string result = await session.CompactAsync();

            IReadOnlyList<ChatMessage> after = session.History;
            Assert.That(after.Count, Is.LessThan(before));
            Assert.That(after.Any(m => m.Text != null && m.Text.Contains("Summary of the colony so far.")), Is.True);
            Assert.That(result, Does.Contain(before + " -> " + after.Count));
            TranscriptEntry notice = session.Transcript.Snapshot().Last();
            Assert.That(notice.Kind, Is.EqualTo(TranscriptKind.Notice));
            // 可見對話紀錄不受影響：6 句指令的 User 條目都在。
            Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.User), Is.EqualTo(6));
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task CompactFailureKeepsHistoryAndLogsNotice()
        {
            // 歷史要夠長（超過保留下限），策略才會真的呼叫摘要 client 並觸發 Throw；
            // AF 策略吞掉摘要例外並原樣回傳，Compact 以「一則沒少」認出並誠實回報。
            for (int i = 0; i < 6; i++) _client.Reply("ok " + i);
            _client.Throw(new InvalidOperationException("summarizer down"));
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            for (int i = 0; i < 6; i++) await session.SendAsync("instruction " + i);
            int before = session.History.Count;
            Assert.That(before, Is.GreaterThan(8));
            int callsBefore = _client.CallCount;

            string result = await session.CompactAsync();

            Assert.That(_client.CallCount, Is.EqualTo(callsBefore + 1), "壓縮應呼叫摘要 client 一次");
            Assert.That(result, Is.Null);
            Assert.That(session.History.Count, Is.EqualTo(before), "失敗時歷史保持不變");
            TranscriptEntry notice = session.Transcript.Snapshot().Last();
            Assert.That(notice.Kind, Is.EqualTo(TranscriptKind.Notice));
            Assert.That(notice.Text, Does.Contain("Already compact"));
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task CompactWhileBusyThrows()
        {
            var gate = new GateChatClient();
            ColonyAgentSession session = new ColonyAgentSession(gate, _world, () => _permissions, 8);
            Task<string> running = session.SendAsync("hello");
            Assert.That(session.IsBusy, Is.True);

            Assert.ThrowsAsync<InvalidOperationException>(() => session.CompactAsync());

            gate.Release("done");
            Assert.That(await running, Is.EqualTo("done"));
            Assert.That(session.IsBusy, Is.False);
        }

        /// <summary>卡住不回的假 client：Release 之前 GetResponseAsync 永不完成，用來佔住 busy 旗標。</summary>
        private sealed class GateChatClient : IChatClient
        {
            private readonly TaskCompletionSource<ChatResponse> _gate = new TaskCompletionSource<ChatResponse>();

            public void Release(string text)
            {
                _gate.TrySetResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            }

            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                return _gate.Task;
            }

            public bclasync::System.Collections.Generic.IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("RimAgent does not stream.");
            }

            public object GetService(Type serviceType, object serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

            public void Dispose()
            {
            }
        }
    }
}
