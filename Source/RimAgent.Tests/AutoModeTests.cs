using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>自動模式：命令先預覽、問分類器，低風險直接做、高風險等確認；分類器出錯一律當高風險。</summary>
    [TestFixture]
    public class AutoModeTests
    {
        private FakeGameWorld _world;
        private ScriptedChatClient _agentClient;
        private ScriptedChatClient _classifierClient;
        private ToolPermissions _permissions;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _world.Traders.Add(new TraderInfo
            {
                Kind = TraderInfo.OrbitalKind, Id = "PassingShip_1", Name = "Starweaver", Faction = "Outlanders", Silver = 1200,
                Goods = new[] { new TradeGoodsInfo { DefName = "ComponentSpacer", Label = "spacer component", Count = 8, MarketValue = 55 } }
            });
            _world.Resources = new ResourceSnapshot { Items = new[] { new ResourceInfo { DefName = "Silver", Label = "silver", Count = 500 } } };
            _agentClient = new ScriptedChatClient();
            _classifierClient = new ScriptedChatClient();
            _permissions = new ToolPermissions { Observe = true, Work = true, Trade = true };
        }

        private ColonyAgentSession NewSession()
        {
            return new ColonyAgentSession(_agentClient, _world, () => _permissions, 8, () => AgentMode.Auto, null, null, _classifierClient);
        }

        [Test]
        public async Task LowRiskCommandRunsOnceWithoutConfirmation()
        {
            _agentClient.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 }).Reply("Done.");
            _classifierClient.Reply("{\"risk\": \"low\", \"reason\": \"small reversible change\"}");
            ColonyAgentSession session = NewSession();

            string reply = await session.SendAsync("set Ali's doctor to 1");

            Assert.That(reply, Is.EqualTo("Done."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "SetWorkPriority(101,Doctor,1)" }), "預覽段不可碰世界，執行段只跑一次");
            Assert.That(session.HasPendingConfirmation, Is.False);
            Assert.That(_classifierClient.CallCount, Is.EqualTo(1));
            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.RiskCheck, Is.EqualTo("low"));
            Assert.That(tool.Result, Does.StartWith("Set Ali's doctor priority 3 -> 1"));
        }

        [Test]
        public async Task HighRiskCommandWaitsForConfirmationThenRunsWithoutReclassifying()
        {
            _agentClient.CallTool("execute_trade", new { trader = "PassingShip_1", buys = "spacer component x4", sells = "", negotiator = "Ali" })
                        .Reply("I need your confirmation to buy 4 spacer components.");
            _classifierClient.Reply("{\"risk\": \"high\", \"reason\": \"spends silver\"}");
            ColonyAgentSession session = NewSession();

            string reply = await session.SendAsync("buy 4 spacer components");

            Assert.That(reply, Does.Contain("confirmation"));
            Assert.That(_world.Calls, Is.Empty);
            Assert.That(session.HasPendingConfirmation, Is.True);
            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.RiskCheck, Is.EqualTo("high"));
            Assert.That(tool.Result, Does.StartWith(ToolText.ConfirmationPrefix).And.Contain("risk check: spends silver"));

            string done = await session.ConfirmAsync();

            Assert.That(done, Is.EqualTo("Executed 1 confirmed command(s)."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "ExecuteTrade(PassingShip_1,101)" }));
            Assert.That(_classifierClient.CallCount, Is.EqualTo(1), "確認後的放行重送不再問分類器");
            Assert.That(session.HasPendingConfirmation, Is.False);
        }

        [Test]
        public async Task McpExecutionAlwaysWaitsForConfirmationAndSkipsClassifier()
        {
            _permissions.Mcp = true;
            _agentClient.CallTool("mcp_call_tool", new { server = "fetch", tool = "fetch", args = "{}" })
                        .Reply("Waiting for confirmation.");
            ColonyAgentSession session = NewSession();

            await session.SendAsync("call the MCP tool");

            Assert.That(session.HasPendingConfirmation, Is.True);
            Assert.That(_classifierClient.CallCount, Is.EqualTo(0));
            Assert.That(session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool).Result,
                Does.StartWith(ToolText.ConfirmationPrefix));
        }

        [Test]
        public async Task ClassifierFailureFailsClosed()
        {
            _agentClient.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 }).Reply("Waiting.");
            _classifierClient.Throw(new TimeoutException("provider timed out"));
            ColonyAgentSession session = NewSession();

            await session.SendAsync("set Ali's doctor to 1");

            Assert.That(_world.Calls, Is.Empty);
            Assert.That(session.HasPendingConfirmation, Is.True);
            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.RiskCheck, Is.EqualTo("unavailable"));
            Assert.That(tool.Result, Does.Contain("risk check unavailable"));
        }

        [Test]
        public async Task UnreadableVerdictFailsClosed()
        {
            _agentClient.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 }).Reply("Waiting.");
            _classifierClient.Reply("Sure, that looks fine to me!");
            ColonyAgentSession session = NewSession();

            await session.SendAsync("set Ali's doctor to 1");

            Assert.That(_world.Calls, Is.Empty);
            Assert.That(session.HasPendingConfirmation, Is.True);
        }

        [Test]
        public async Task QueriesAndInvalidCommandsSkipTheClassifier()
        {
            _agentClient.CallTool("list_colonists", new { })
                        .CallTool("set_work_priority", new { colonist = "Nobody", workType = "doctor", priority = 1 })
                        .Reply("No such colonist.");
            ColonyAgentSession session = NewSession();

            await session.SendAsync("who is here? set Nobody's doctor to 1");

            Assert.That(_classifierClient.CallCount, Is.EqualTo(0));
            Assert.That(_world.Calls, Is.Empty);
            Assert.That(session.HasPendingConfirmation, Is.False);
            Assert.That(session.Transcript.Snapshot().Last(e => e.Kind == TranscriptKind.Tool).Failed, Is.True);
        }

        [Test]
        public async Task ClassifierSeesInstructionArgumentsAndPreview()
        {
            _agentClient.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 }).Reply("Done.");
            _classifierClient.Reply("{\"risk\": \"low\"}");
            ColonyAgentSession session = NewSession();

            await session.SendAsync("make Ali the main doctor");

            IList<ChatMessage> sent = _classifierClient.ReceivedMessages.Single();
            Assert.That(sent[0].Role, Is.EqualTo(ChatRole.System));
            string question = sent[1].Text;
            Assert.That(question, Does.Contain("make Ali the main doctor").And.Contain("set_work_priority").And.Contain("priority=1").And.Contain("would set Ali's doctor priority 3 -> 1"));
            Assert.That(question, Does.Not.Contain(ToolText.ConfirmationPrefix));
            ChatOptions options = _classifierClient.ReceivedOptions.Single();
            Assert.That(options.Tools, Is.Null.Or.Empty, "分類器不帶工具");
            Assert.That(options.Temperature, Is.EqualTo(0f));
        }

        [TestCase("{\"risk\":\"high\",\"reason\":\"x\"}", true, "x")]
        [TestCase("Verdict: {\"risk\": \"LOW\", \"reason\": \"fine\"} — done", false, "fine")]
        [TestCase("{\"risk\":\"low\"}", false, "")]
        public void ParseAcceptsJsonAnywhereInTheReply(string text, bool risky, string reason)
        {
            RiskVerdict verdict = RiskClassifier.Parse(text);
            Assert.That(verdict, Is.Not.Null);
            Assert.That(verdict.Risky, Is.EqualTo(risky));
            Assert.That(verdict.Reason, Is.EqualTo(reason));
        }

        [TestCase("")]
        [TestCase("looks fine")]
        [TestCase("{\"risk\":\"medium\"}")]
        [TestCase("{\"reason\":\"no risk field\"}")]
        [TestCase("{not json")]
        public void ParseRejectsAnythingElse(string text)
        {
            Assert.That(RiskClassifier.Parse(text), Is.Null);
        }

        [Test]
        public void CancellationPropagatesInsteadOfFailingClosed()
        {
            var client = new ScriptedChatClient().Reply("{\"risk\":\"low\"}");
            var classifier = new RiskClassifier(client);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.ThrowsAsync<OperationCanceledException>(() => classifier.ClassifyAsync(new RiskQuestion(), cts.Token));
            }
        }
    }
}
