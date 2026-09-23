using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>醫療類別（issue med-02）：健康狀況查詢。</summary>
    [TestFixture]
    public class HealthToolsTests
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

        private void SeedAliceHurt()
        {
            _world.Health.Add(new ColonistHealthInfo
            {
                PawnId = 101,
                Conditions = new[]
                {
                    new HealthConditionInfo { Label = "gunshot wound", Part = "left leg", Severity = 12, Kind = HealthConditionInfo.InjuryKind, Bleeding = true, NeedsTending = true },
                    new HealthConditionInfo { Label = "flu", Part = string.Empty, Severity = 0.45f, Kind = HealthConditionInfo.ConditionKind, Tended = true },
                    new HealthConditionInfo { Label = "missing right index finger", Part = "right index finger", Kind = HealthConditionInfo.MissingKind }
                },
                PendingSurgeries = new[]
                {
                    new SurgeryBillInfo { Label = "install bionic arm", Part = "left arm" }
                }
            });
            _world.Health.Add(new ColonistHealthInfo { PawnId = 102 });
        }

        [Test]
        public void ListsConditionsSurgeriesAndTendingStatus()
        {
            SeedAliceHurt();

            string text = _tools.GetHealth("all");

            Assert.That(text, Does.Contain("[101] Ali").And.Contain("gunshot wound (left leg)").And.Contain("bleeding").And.Contain("needs tending"));
            Assert.That(text, Does.Contain("flu").And.Contain("tended"));
            Assert.That(text, Does.Contain("missing right index finger (right index finger)"));  // 缺失部位不顯示嚴重度
            Assert.That(text, Does.Contain("pending operation: install bionic arm (left arm)"));
            Assert.That(text, Does.Contain("[102] Bob").And.Contain("healthy."));
        }

        [Test]
        public void AllHealthyIsReportedExplicitly()
        {
            Assert.That(_tools.GetHealth("all"), Is.EqualTo("All 2 colonists are healthy, nothing needs attention."));
        }

        [Test]
        public void SingleHealthyColonistIsReportedExplicitly()
        {
            Assert.That(_tools.GetHealth("Ali"), Is.EqualTo("Ali is healthy, nothing needs attention."));
        }

        [Test]
        public void SingleColonistCanBeSelected()
        {
            SeedAliceHurt();

            string text = _tools.GetHealth("Bob");

            Assert.That(text, Does.Contain("healthy").And.Not.Contain("gunshot"));
        }

        [Test]
        public void UnknownColonistIsAnError()
        {
            Assert.That(_tools.GetHealth("Nobody"), Does.StartWith("Error:"));
        }

        [Test]
        public async Task HealthQueryIsLoggedAsAToolCall()
        {
            SeedAliceHurt();
            var client = new ScriptedChatClient();
            client.CallTool("get_health", new { colonists = "all" }).Reply("One wounded.");
            var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Medical), 8);

            await session.SendAsync("誰受傷了？");

            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.ToolName, Is.EqualTo("get_health"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Medical));
            Assert.That(tool.Result, Does.Contain("gunshot wound"));
        }
    }
}
