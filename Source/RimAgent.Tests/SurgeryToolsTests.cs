using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>醫療類別（issue med-03）：手術排程。</summary>
    [TestFixture]
    public class SurgeryToolsTests
    {
        private FakeGameWorld _world;
        private MedicalTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _tools = new MedicalTools(_world);
        }

        [Test]
        public void RegistryGatesTheSurgeryToolByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(new ToolPermissions { Medical = true }).OfType<AIFunction>().Any(t => t.Name == "schedule_surgery"), Is.True);
            Assert.That(registry.Select(new ToolPermissions { Medical = false }).OfType<AIFunction>().Any(t => t.Name == "schedule_surgery"), Is.False);
        }

        [Test]
        public void SchedulesSurgeryWithExplicitPart()
        {
            string text = _tools.ScheduleSurgery("Ali", "remove body part", "left lung");

            Assert.That(text, Is.EqualTo("Scheduled remove body part on Ali (left lung)."));
            ColonistHealthInfo health = _world.Health.Single(h => h.PawnId == 101);
            SurgeryBillInfo bill = health.PendingSurgeries.Single();
            Assert.That(bill.Label, Is.EqualTo("remove body part"));
            Assert.That(bill.Part, Is.EqualTo("left lung"));
        }

        [Test]
        public void EmptyPartAllowedWhenSurgeryNeedsNoPart()
        {
            string text = _tools.ScheduleSurgery("Ali", "administer anesthetic", string.Empty);

            Assert.That(text, Is.EqualTo("Scheduled administer anesthetic on Ali."));
        }

        [Test]
        public void UnknownSurgeryReportsErrorWithoutScheduling()
        {
            string text = _tools.ScheduleSurgery("Ali", "brain transplant", "head");

            Assert.That(text, Does.StartWith("Error:"));
            Assert.That(_world.Health.SelectMany(h => h.PendingSurgeries), Is.Empty);
        }

        [Test]
        public void UnknownColonistReportsError()
        {
            string text = _tools.ScheduleSurgery("Nobody", "remove body part", "left lung");

            Assert.That(text, Does.StartWith("Error:"));
        }

        [Test]
        public void DuplicateScheduleReportsError()
        {
            _tools.ScheduleSurgery("Ali", "remove body part", "left lung");

            string text = _tools.ScheduleSurgery("Ali", "remove body part", "left lung");

            Assert.That(text, Does.StartWith("Error:").And.Contain("already scheduled"));
            Assert.That(_world.Health.Single(h => h.PawnId == 101).PendingSurgeries.Count, Is.EqualTo(1));
        }

        [Test]
        public void LockedSurgeryReportsMissingResearch()
        {
            _world.LockedSurgeries.Add("InstallArtificialBodyPart");

            string text = _tools.ScheduleSurgery("Ali", "install artificial body part", "right arm");

            Assert.That(text, Does.StartWith("Error:").And.Contain("research"));
            Assert.That(_world.Health.SelectMany(h => h.PendingSurgeries), Is.Empty);
        }

        [Test]
        public async Task ScheduleSurgeryIsLoggedAsAToolCall()
        {
            var client = new ScriptedChatClient();
            client.CallTool("schedule_surgery", new { colonist = "Ali", surgery = "remove body part", part = "left lung" }).Reply("Done.");
            var session = new ColonyAgentSession(client, _world, () => new ToolPermissions { Observe = true, Medical = true }, 8);

            await session.SendAsync("Ali 左肺 Gallstone 排手術");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("schedule_surgery"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Medical));
            Assert.That(tool.Result, Does.Contain("Ali").And.Contain("left lung"));
        }
    }
}
