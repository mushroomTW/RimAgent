using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>時間類別（time issue 01）：查詢流速、切換暫停與加速。</summary>
    [TestFixture]
    public class TimeToolsTests
    {
        private FakeGameWorld _world;
        private TimeTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _tools = new TimeTools(_world);
        }

        [Test]
        public void TimePermissionOffUntilGrantedButSettingsDefaultOn()
        {
            Assert.That(new ToolPermissions().Allows(ToolCategory.Time), Is.False);
            Assert.That(new RimAgentSettings().ToPermissions().Allows(ToolCategory.Time), Is.True, "設定頁預設全開");
        }

        [Test]
        public void RegistryGatesTheTimeToolsByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Time)).OfType<AIFunction>().Count(t => t.Name == "set_time_speed"), Is.EqualTo(1));
            Assert.That(registry.Select(new ToolPermissions()).OfType<AIFunction>().Any(t => t.Name == "set_time_speed"), Is.False);
        }

        [Test]
        public void OverviewReportsCurrentTimeSpeed()
        {
            string text = new ObserveTools(_world).GetColonyOverview();

            Assert.That(text, Does.Contain("Time speed: ").And.Contain("(Normal)"));
        }

        [TestCase("Paused", "Paused")]
        [TestCase("暫停", "Paused")]
        [TestCase("fast", "Fast")]
        [TestCase("超快", "Superfast")]
        public void SetTimeSpeedSwitchesSpeed(string input, string expected)
        {
            string text = _tools.SetTimeSpeed(input);

            Assert.That(text, Does.StartWith("Time speed").And.Contain(expected));
            Assert.That(_world.CurrentTimeSpeed, Is.EqualTo(expected));
        }

        [Test]
        public void UnknownSpeedReportsError()
        {
            string text = _tools.SetTimeSpeed("lightspeed");

            Assert.That(text, Does.StartWith("Error:"));
            Assert.That(_world.CurrentTimeSpeed, Is.EqualTo("Normal"));
        }

        [Test]
        public async Task SetTimeSpeedIsLoggedAsAToolCall()
        {
            var client = new ScriptedChatClient();
            client.CallTool("set_time_speed", new { speed = "Paused" }).Reply("Done.");
            var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Time), 8);

            await session.SendAsync("先暫停遊戲");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("set_time_speed"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Time));
            Assert.That(tool.Result, Does.Contain("Paused"));
        }
    }
}
