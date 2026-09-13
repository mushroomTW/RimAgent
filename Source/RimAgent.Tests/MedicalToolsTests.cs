using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>醫療類別（issue med-01）：權限開關與醫療等級調整。</summary>
    [TestFixture]
    public class MedicalToolsTests
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
        public void MedicalIsOffByDefault()
        {
            Assert.That(new ToolPermissions().Medical, Is.False);
            Assert.That(new RimAgentSettings().ToPermissions().Medical, Is.False);
        }

        [Test]
        public void RegistryGatesTheMedicalToolByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(new ToolPermissions { Medical = true }).OfType<AIFunction>().Any(t => t.Name == "set_medical_care"), Is.True);
            Assert.That(registry.Select(new ToolPermissions { Medical = false }).OfType<AIFunction>().Any(t => t.Name == "set_medical_care"), Is.False);
        }

        [Test]
        public void SetMedicalCareReportsPreviousAndNewValue()
        {
            string text = _tools.SetMedicalCare("Ali", "Best");

            Assert.That(text, Does.Contain("Ali").And.Contain("doctor care only, no medicine").And.Contain("best"));
            Assert.That(_world.Colonists[0].MedicalCare, Is.EqualTo("Best"));
        }

        [Test]
        public void UnknownLevelListsNoFabrication()
        {
            string text = _tools.SetMedicalCare("Ali", "godlike");

            Assert.That(text, Does.StartWith("Error:"));
            Assert.That(_world.Colonists[0].MedicalCare, Is.EqualTo("NoMeds"));
        }

        [Test]
        public async Task SetMedicalCareIsLoggedAsAToolCall()
        {
            var client = new ScriptedChatClient();
            client.CallTool("set_medical_care", new { colonist = "Ali", level = "HerbalOrWorse" }).Reply("Done.");
            var session = new ColonyAgentSession(client, _world, () => new ToolPermissions { Observe = true, Medical = true }, 8);

            await session.SendAsync("Ali 只用草藥");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("set_medical_care"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Medical));
            Assert.That(tool.Result, Does.Contain("Ali").And.Contain("herbal medicine or worse"));
        }
    }
}
