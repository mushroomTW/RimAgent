using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>
    /// 工具包裝層的兩道世界守衛：遊戲沒在進行時不碰工具直接回錯誤；
    /// 工具回傳後把這段期間遊戲記錄的錯誤附在結果後面。
    /// </summary>
    [TestFixture]
    public class ToolGuardTests
    {
        private FakeGameWorld _world;
        private AgentTranscript _transcript;
        private AgentToolRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _transcript = new AgentTranscript();
            _registry = new AgentToolRegistry(_world, _transcript, null, new ConfirmationGate(() => AgentMode.Autonomous));
        }

        private AIFunction Tool(string name)
        {
            return _registry.All.OfType<AIFunction>().Single(t => t.Name == name);
        }

        [Test]
        public async Task NotReadyGameBlocksEveryToolWithoutTouchingWorld()
        {
            _world.NotReadyReason = "no game is being played";

            string query = (await Tool("get_colony_overview").InvokeAsync(new AIFunctionArguments()))?.ToString();
            string command = (await Tool("draft").InvokeAsync(new AIFunctionArguments(new Dictionary<string, object> { ["colonists"] = "Ali", ["drafted"] = true })))?.ToString();

            Assert.That(query, Does.StartWith(ToolText.ErrorPrefix).And.Contain("no game is being played"));
            Assert.That(command, Does.StartWith(ToolText.ErrorPrefix));
            Assert.That(_world.Calls, Is.Empty);
            Assert.That(_transcript.Snapshot().Where(e => e.Kind == TranscriptKind.Tool), Has.Exactly(2).Items.And.All.Property("Failed").True);
        }

        [Test]
        public async Task ReadyGameRunsToolsNormally()
        {
            _world.NotReadyReason = null;
            string text = (await Tool("get_colony_overview").InvokeAsync(new AIFunctionArguments()))?.ToString();
            Assert.That(text, Does.StartWith("Colony: New Arrivals"));
        }

        [Test]
        public async Task GameErrorsDuringCallAreAppendedToResult()
        {
            _world.PendingGameErrors.Add("Exception in Blueprint.SpawnSetup: NullReferenceException");
            _world.PendingGameErrors.Add("(x3) Tried to set priority on null pawn");

            string text = (await Tool("draft").InvokeAsync(new AIFunctionArguments(new Dictionary<string, object> { ["colonists"] = "Ali", ["drafted"] = true })))?.ToString();

            Assert.That(text, Does.StartWith("Drafted"));
            Assert.That(text, Does.Contain("Game errors logged during this call (2):"));
            Assert.That(text, Does.Contain("- Exception in Blueprint.SpawnSetup: NullReferenceException"));
            Assert.That(text, Does.Contain("- (x3) Tried to set priority on null pawn"));
            Assert.That(_world.PendingGameErrors, Is.Empty, "錯誤取走後不會再附到下一次呼叫");
            Assert.That(_transcript.Snapshot().Last(e => e.Kind == TranscriptKind.Tool).Result, Does.Contain("Game errors logged"));
        }

        [Test]
        public async Task WithoutGameErrorsResultIsUntouched()
        {
            string text = (await Tool("get_colony_overview").InvokeAsync(new AIFunctionArguments()))?.ToString();
            Assert.That(text, Does.Not.Contain("Game errors"));
        }

        [Test]
        public async Task MoreThanFiveErrorsAreCapped()
        {
            for (int i = 1; i <= 7; i++) _world.PendingGameErrors.Add("error " + i);

            string text = (await Tool("get_colony_overview").InvokeAsync(new AIFunctionArguments()))?.ToString();

            Assert.That(text, Does.Contain("(7):").And.Contain("- error 5").And.Contain("- ... and 2 more"));
            Assert.That(text, Does.Not.Contain("- error 6"));
        }

        [Test]
        public async Task SendAsyncDiscardsErrorsLoggedBeforeTheInstruction()
        {
            _world.PendingGameErrors.Add("stale error from an hour ago");
            var client = new ScriptedChatClient().Reply("done");
            var session = new ColonyAgentSession(client, _world, () => ToolPermissions.All, 5);

            await session.SendAsync("hi");

            Assert.That(_world.PendingGameErrors, Is.Empty);
        }
    }
}
