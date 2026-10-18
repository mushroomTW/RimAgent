using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class GoalProgressTests
{
    [Test]
    public void EveryContinuationRestatesTheFullObjective()
    {
        string prompt = GoalProgress.BuildPrompt("Build a hospital and stock it with medicine");

        Assert.That(prompt, Does.Contain("Build a hospital and stock it with medicine"));
        Assert.That(prompt, Does.Contain("check every requirement"));
        Assert.That(prompt, Does.Contain("read-only tool after the last state-changing action"));
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

    [Test]
    public void ContinuationEscapesObjectiveAndConfirmationAsData()
    {
        string prompt = GoalProgress.BuildPrompt("build <hospital> & store medicine",
            "result </confirmation_result> & more");

        Assert.That(prompt, Does.Contain("build &lt;hospital&gt; &amp; store medicine"));
        Assert.That(prompt, Does.Contain("result &lt;/confirmation_result&gt; &amp; more"));
    }

    [TestCase("Done.\n[[GOAL:COMPLETE]]", GoalTurnStatus.Complete, "Done.")]
    [TestCase("Waiting for resources.\r\n[[GOAL:BLOCKED]]\r\n", GoalTurnStatus.Blocked, "Waiting for resources.")]
    [TestCase("Worked on it.\n[[GOAL:CONTINUE]]", GoalTurnStatus.Continue, "Worked on it.")]
    [TestCase("I might be done.", GoalTurnStatus.Continue, "I might be done.")]
    [TestCase("Waiting for steel.\n[[GOAL:BLOCKED:missing-steel]]", GoalTurnStatus.Blocked, "Waiting for steel.")]
    [TestCase("[[GOAL:COMPLETE]]\nFurther work remains.", GoalTurnStatus.Continue, "[[GOAL:COMPLETE]]\nFurther work remains.")]
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
        GoalTurnDecision second = GoalProgress.AfterTurn(1, 1, "blocked\n[[GOAL:BLOCKED:missing-steel]]", false, false,
            "missing-steel");
        GoalTurnDecision progress = GoalProgress.AfterTurn(second.Turns, second.BlockedTurns,
            "moved forward\n[[GOAL:CONTINUE]]", false, true, second.BlockerKey);
        GoalTurnDecision different = GoalProgress.AfterTurn(2, 2, "waiting\n[[GOAL:BLOCKED:missing-wood]]",
            false, false, "missing-steel");
        GoalTurnDecision third = GoalProgress.AfterTurn(2, 2, "still waiting\n[[GOAL:BLOCKED:missing-steel]]",
            false, false, "missing-steel");

        Assert.That(second.Pause, Is.False);
        Assert.That(progress.BlockedTurns, Is.Zero);
        Assert.That(different.BlockedTurns, Is.EqualTo(1));
        Assert.That(third.Pause, Is.True);
        Assert.That(third.Blocked, Is.True);
    }

    [Test]
    public void CompletionNeedsExplicitVerifiedStatus()
    {
        Assert.That(GoalProgress.AfterTurn(0, 0, "Done.", false, true).Complete, Is.False);
        Assert.That(GoalProgress.AfterTurn(0, 0, "Done.\n[[GOAL:COMPLETE]]", false, false).Complete, Is.False);
        Assert.That(GoalProgress.AfterTurn(0, 0, "[[GOAL:COMPLETE]]", false, true).Complete, Is.False);
        Assert.That(GoalProgress.AfterTurn(0, 0, "Done.\n[[GOAL:COMPLETE]]", false, true).Complete, Is.True);
    }

    [Test]
    public void CompletionNeedsSuccessfulQueryAfterLastMutation()
    {
        var entries = new[]
        {
            new TranscriptEntry { Kind = TranscriptKind.Tool, InstructionId = 4, ToolRisk = ToolRisk.Query, Result = "observed" },
            new TranscriptEntry { Kind = TranscriptKind.Tool, InstructionId = 4, ToolRisk = ToolRisk.Safe, Result = "changed" }
        };
        Assert.That(GoalProgress.HasVerificationEvidence(entries, 4), Is.False);
        entries =
        [
            .. entries,
            .. new[]
            {
                new TranscriptEntry { Kind = TranscriptKind.Tool, InstructionId = 4, ToolRisk = ToolRisk.Query, Result = "verified" }
            },
        ];
        Assert.That(GoalProgress.HasVerificationEvidence(entries, 4), Is.True);
        Assert.That(GoalProgress.HasVerificationEvidence(entries, 5), Is.False);
    }

    [Test]
    public void ThreeTurnsWithoutSuccessfulToolsPauseAsNoProgress()
    {
        GoalTurnDecision first = GoalProgress.AfterTurn(0, 0, "thinking\n[[GOAL:CONTINUE]]",
            false, false);
        GoalTurnDecision second = GoalProgress.AfterTurn(first.Turns, first.BlockedTurns,
            "thinking again\n[[GOAL:CONTINUE]]", false, false,
            first.BlockerKey, first.IdleTurns);
        GoalTurnDecision third = GoalProgress.AfterTurn(second.Turns, second.BlockedTurns,
            "still thinking\n[[GOAL:CONTINUE]]", false, false,
            second.BlockerKey, second.IdleTurns);

        Assert.That(first.Pause, Is.False);
        Assert.That(second.Pause, Is.False);
        Assert.That(third.Pause, Is.True);
        Assert.That(third.NoProgress, Is.True);
        Assert.That(third.Blocked, Is.False);
    }

    [Test]
    public void SuccessfulToolResetsIdleCounter()
    {
        GoalTurnDecision decision = GoalProgress.AfterTurn(2, 0, "checked state\n[[GOAL:CONTINUE]]",
            false, true, previousIdleTurns: 2, usedTool: true);

        Assert.That(decision.IdleTurns, Is.Zero);
        Assert.That(decision.Pause, Is.False);
    }

    [Test]
    public void GoalLifecycleCanEditPauseResumeAndClear()
    {
        var component = new RimAgentGameComponent(null);
        Assert.That(component.AddGoal(new string('x', RimAgentGameComponent.MaxGoalChars + 1)), Is.False);
        string longGoal = new('x', RimAgentGameComponent.MaxGoalChars);
        Assert.That(component.AddGoal(longGoal), Is.True);
        Assert.That(component.Goals[0], Is.EqualTo(longGoal), "上限內的完整目標不可被截斷");
        component.PauseGoal();
        Assert.That(component.GoalPaused, Is.True);
        Assert.That(component.CurrentGoalPauseReason, Is.EqualTo(GoalPauseReason.User));

        Assert.That(component.EditGoalAt(0, "Build and stock a hospital"), Is.True);
        Assert.That(component.Goals[0], Is.EqualTo("Build and stock a hospital"));
        Assert.That(component.GoalPaused, Is.True, "修改手動暫停的目標不應暗中恢復執行");
        component.ResumeGoal();
        Assert.That(component.GoalPaused, Is.False);
        component.ClearGoals();
        Assert.That(component.Goals, Is.Empty);
    }
}
