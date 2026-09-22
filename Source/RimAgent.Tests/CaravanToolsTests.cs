using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>交易類別（caravan issue 01）：組織商隊外出。</summary>
    [TestFixture]
    public class CaravanToolsTests
    {
        private FakeGameWorld _world;
        private TradeTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _tools = new TradeTools(_world);
        }

        [Test]
        public void RegistryGatesTheCaravanToolByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(new ToolPermissions { Trade = true }).OfType<AIFunction>().Any(t => t.Name == "form_caravan"), Is.True);
            Assert.That(registry.Select(new ToolPermissions { Trade = false }).OfType<AIFunction>().Any(t => t.Name == "form_caravan"), Is.False);
        }

        [Test]
        public void FormsCaravanWithNamedMembers()
        {
            string text = _tools.FormCaravan("Ali, Bob", "N");

            Assert.That(text, Is.EqualTo("Formed a caravan with Ali, Bob, heading north."));
            Assert.That(_world.Calls.Single(), Does.StartWith("FormCaravan(101+102,N)"));
        }

        [Test]
        public void FullHeadingNameIsAccepted()
        {
            string text = _tools.FormCaravan("Ali", "south-west");

            Assert.That(text, Is.EqualTo("Formed a caravan with Ali, heading south-west."));
        }

        [Test]
        public void AllIsRefusedWithoutTouchingTheWorld()
        {
            string text = _tools.FormCaravan("all", "N");

            Assert.That(text, Does.StartWith("Error:").And.Contain("explicitly"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void UnknownHeadingReportsError()
        {
            string text = _tools.FormCaravan("Ali", "up");

            Assert.That(text, Does.StartWith("Error:").And.Contain("unknown heading"));
        }

        [Test]
        public void UnknownColonistReportsError()
        {
            string text = _tools.FormCaravan("Nobody", "N");

            Assert.That(text, Does.StartWith("Error:"));
            Assert.That(_world.Calls, Is.Empty);
        }

        [Test]
        public void DraftedMemberMustUndraftFirst()
        {
            _world.Colonists[0].Drafted = true;

            string text = _tools.FormCaravan("Ali, Bob", "E");

            Assert.That(text, Does.StartWith("Error:").And.Contain("drafted"));
        }

        [Test]
        public void DownedMemberIsRefused()
        {
            _world.Colonists[0].Downed = true;

            string text = _tools.FormCaravan("Ali, Bob", "E");

            Assert.That(text, Does.StartWith("Error:").And.Contain("downed"));
        }

        [Test]
        public async Task FormCaravanIsLoggedAsAToolCall()
        {
            var client = new ScriptedChatClient();
            var classifier = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
            client.CallTool("form_caravan", new { colonists = "Ali, Bob", heading = "NE" }).Reply("Done.");
            var session = new ColonyAgentSession(
                client,
                _world,
                () => new ToolPermissions { Observe = true, Trade = true },
                8,
                () => AgentMode.Auto,
                classifierClient: classifier);

            await session.SendAsync("Ali 跟 Bob 往東北組商隊出發");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("form_caravan"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Trade));
            Assert.That(tool.Result, Does.Contain("Ali, Bob").And.Contain("north-east"));
        }
    }
}
