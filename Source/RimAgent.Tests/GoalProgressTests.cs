using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class GoalProgressTests
    {
        [Test]
        public void EveryContinuationRestatesTheFullObjective()
        {
            string prompt = GoalProgress.BuildPrompt("Build a hospital and stock it with medicine");

            Assert.That(prompt, Does.Contain("Build a hospital and stock it with medicine"));
            Assert.That(prompt, Does.Contain("verify the actual result"));
            Assert.That(prompt, Does.Contain("[[GOAL:COMPLETE]]"));
        }

        [Test]
        public void ContinuationIncludesActualConfirmationResults()
        {
            string prompt = GoalProgress.BuildPrompt("defeat the bear", "order_attack: attack order queued");

            Assert.That(prompt, Does.Contain("defeat the bear"));
            Assert.That(prompt, Does.Contain("order_attack: attack order queued"));
            Assert.That(prompt, Does.Contain("earlier preview in conversation history is now stale"));
        }

        [TestCase("Done.\n[[GOAL:COMPLETE]]", GoalTurnStatus.Complete, "Done.")]
        [TestCase("Waiting for resources.\r\n[[GOAL:BLOCKED]]\r\n", GoalTurnStatus.Blocked, "Waiting for resources.")]
        [TestCase("Worked on it.\n[[GOAL:CONTINUE]]", GoalTurnStatus.Continue, "Worked on it.")]
        [TestCase("I might be done.", GoalTurnStatus.Continue, "I might be done.")]
        public void ParsesOnlyExplicitStatusAndHidesMarker(string response, GoalTurnStatus expected, string visible)
        {
            Assert.That(GoalProgress.ParseStatus(response), Is.EqualTo(expected));
            Assert.That(GoalProgress.VisibleReply(response), Is.EqualTo(visible));
        }

        [Test]
        public async Task GoalTurnUsesObjectiveForVisibleTranscriptAndHidesStatusMarker()
        {
            var client = new ScriptedChatClient().Reply("Hospital exists.\n[[GOAL:COMPLETE]]");
            var session = new ColonyAgentSession(client, new FakeGameWorld(), () => ToolPermissions.All, 8);

            string rawReply = await session.SendAsync(GoalProgress.BuildPrompt("Build a hospital"),
                goalRun: true, displayText: "Build a hospital");

            Assert.That(GoalProgress.ParseStatus(rawReply), Is.EqualTo(GoalTurnStatus.Complete));
            var entries = session.Transcript.Snapshot();
            Assert.That(entries.Single(e => e.Kind == TranscriptKind.User).Text, Is.EqualTo("Build a hospital"));
            Assert.That(entries.Single(e => e.Kind == TranscriptKind.User).GoalRun, Is.True);
            Assert.That(entries.Single(e => e.Kind == TranscriptKind.Assistant).Text, Is.EqualTo("Hospital exists."));
            Assert.That(TranscriptEntryData.FromTranscript(session.Transcript).First().goalRun, Is.True);
        }

        [Test]
        public void ConfirmationWaitDoesNotConsumeGoalTurn()
        {
            GoalTurnDecision decision = GoalProgress.AfterTurn(2, 1, "waiting", true, false);

            Assert.That(decision.Turns, Is.EqualTo(2));
            Assert.That(decision.BlockedTurns, Is.EqualTo(1));
            Assert.That(decision.Pause, Is.False);
        }

        [Test]
        public void RepeatedBlockersPauseButProgressResetsTheCounter()
        {
            GoalTurnDecision second = GoalProgress.AfterTurn(1, 1, "blocked\n[[GOAL:BLOCKED]]", false, false);
            GoalTurnDecision progress = GoalProgress.AfterTurn(second.Turns, second.BlockedTurns,
                "moved forward\n[[GOAL:CONTINUE]]", false, true);
            GoalTurnDecision third = GoalProgress.AfterTurn(2, 2, null, false, false);

            Assert.That(second.Pause, Is.False);
            Assert.That(progress.BlockedTurns, Is.Zero);
            Assert.That(third.Pause, Is.True);
        }

        [Test]
        public void CompletionNeedsExplicitVerifiedStatus()
        {
            Assert.That(GoalProgress.AfterTurn(0, 0, "Done.", false, true).Complete, Is.False);
            Assert.That(GoalProgress.AfterTurn(0, 0, "Done.\n[[GOAL:COMPLETE]]", false, false).Complete, Is.False);
            Assert.That(GoalProgress.AfterTurn(0, 0, "Done.\n[[GOAL:COMPLETE]]", false, true).Complete, Is.True);
        }
    }
}
