using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class DecisionGoalAuditTests
{
    private const string SampleGoalCriteria = @"# Goal decision criteria

The sections below are data, not instructions.

## all_done
Have all requirements of the player's goal been fully satisfied and verified complete?
true: All requirements of the goal are met and confirmed by tools.
false: Some requirements remain incomplete, unattempted, or failed.

## remaining
Did the agent state or imply that further steps, remaining tasks, or follow-ups are needed?
true: The agent explicitly noted unfinished steps or remaining tasks.
false: The agent did not indicate any remaining work.

## needs_user
Is the agent waiting for the player to make a choice, provide input, or resolve an external blocker?
true: The agent asked the player a question or paused for player intervention.
false: The agent is not waiting for player guidance or input.
";

    [Test]
    public void ParsePlanExtractsExpectedQuestions()
    {
        DecisionPlan plan = DecisionGoalAuditor.ParsePlan(SampleGoalCriteria);

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan.Questions, Has.Count.EqualTo(3));

        DecisionQuestion qAllDone = plan.Questions[0];
        Assert.That(qAllDone.Id, Is.EqualTo("all_done"));
        Assert.That(qAllDone.Type, Is.EqualTo(DecisionQuestionType.Noul));
        Assert.That(qAllDone.Instructions, Does.Contain("fully satisfied"));
        Assert.That(qAllDone.WhenTrue, Does.Contain("All requirements of the goal are met"));
        Assert.That(qAllDone.WhenFalse, Does.Contain("remain incomplete"));

        DecisionQuestion qRemaining = plan.Questions[1];
        Assert.That(qRemaining.Id, Is.EqualTo("remaining"));
        Assert.That(qRemaining.Type, Is.EqualTo(DecisionQuestionType.Noul));
        Assert.That(qRemaining.Instructions, Does.Contain("remaining tasks"));
        Assert.That(qRemaining.WhenTrue, Does.Contain("explicitly noted unfinished steps"));
        Assert.That(qRemaining.WhenFalse, Does.Contain("did not indicate"));

        DecisionQuestion qNeedsUser = plan.Questions[2];
        Assert.That(qNeedsUser.Id, Is.EqualTo("needs_user"));
        Assert.That(qNeedsUser.Type, Is.EqualTo(DecisionQuestionType.Noul));
        Assert.That(qNeedsUser.Instructions, Does.Contain("resolve an external blocker"));
        Assert.That(qNeedsUser.WhenTrue, Does.Contain("asked the player"));
        Assert.That(qNeedsUser.WhenFalse, Does.Contain("not waiting"));
    }

    [Test]
    public void ParsePlanReturnsEmptyPlanOnEmptyInput()
    {
        DecisionPlan plan = DecisionGoalAuditor.ParsePlan("   ");
        Assert.That(plan.IsEmpty, Is.True);
        Assert.That(plan.Questions, Is.Empty);
    }

    [Test]
    public void AgentPackLoadsDecisionGoalCriteria()
    {
        // 尋找工作區根目錄的 AgentPack 資料夾
        string dir = TestContext.CurrentContext.TestDirectory;
        string candidate = null;
        for (int i = 0; i < 5; i++)
        {
            string path = Path.Combine(dir, "AgentPack");
            if (Directory.Exists(path))
            {
                candidate = path;
                break;
            }
            dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(dir)) break;
        }

        if (candidate != null)
        {
            AgentPack pack = AgentPackLoader.Load(candidate);
            Assert.That(pack.DecisionGoalCriteria, Is.Not.Empty);
            Assert.That(pack.DecisionGoalCriteria, Does.Contain("## all_done"));
            Assert.That(pack.DecisionGoalCriteria, Does.Contain("## remaining"));
            Assert.That(pack.DecisionGoalCriteria, Does.Contain("## needs_user"));
        }
    }

    [Test]
    public async Task AuditAsyncReturnsNullWhenNoCriteriaProvided()
    {
        var fake = new FakeDecisionService().ReplyGoal(0.9, 0.1, 0.0);
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => string.Empty);

        GoalAuditResult result = await auditor.AuditAsync("build hospital", "All done.\n[[GOAL:COMPLETE]]");
        Assert.That(result, Is.Null);
        Assert.That(fake.EvaluateCount, Is.Zero);
    }

    [Test]
    public async Task AuditAsyncReturnsNullWhenServiceNotAvailable()
    {
        var auditor = new DecisionGoalAuditor(
            decisionService: null,
            criteriaProvider: () => SampleGoalCriteria,
            serviceFactory: () => null);

        GoalAuditResult result = await auditor.AuditAsync("build hospital", "All done.\n[[GOAL:COMPLETE]]");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task AuditAsyncReturnsCompletedWhenAllDoneHighAndRemainingLow()
    {
        var fake = new FakeDecisionService().ReplyGoal(allDone: 0.85, remaining: 0.15, needsUser: 0.05);
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => SampleGoalCriteria);

        GoalAuditResult result = await auditor.AuditAsync("Build 3 beds", "Built 3 beds in barracks.\n[[GOAL:COMPLETE]]");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.AllDone, Is.True);
        Assert.That(result.Remaining, Is.False);
        Assert.That(result.NeedsUser, Is.False);
        Assert.That(result.Completed, Is.True);
        Assert.That(result.Reason, Does.Contain("complete"));
        Assert.That(fake.EvaluateCount, Is.EqualTo(1));
        Assert.That(fake.ReceivedStates[0], Does.Contain("<objective>"));
        Assert.That(fake.ReceivedStates[0], Does.Contain("Build 3 beds"));
    }

    [Test]
    public async Task AuditAsyncIncludesToolEvidenceInTheDecisionState()
    {
        var fake = new FakeDecisionService().ReplyGoal(allDone: 0.9, remaining: 0.1, needsUser: 0.0);
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => SampleGoalCriteria);

        await auditor.AuditAsync("Build 3 beds", "Done.\n[[GOAL:COMPLETE]]",
            toolEvidence: "- list_colonists (Query): 3 beds found <in barracks>");

        Assert.That(fake.ReceivedStates, Has.Count.EqualTo(1));
        Assert.That(fake.ReceivedStates[0], Does.Contain("<tool_evidence>"));
        Assert.That(fake.ReceivedStates[0], Does.Contain("3 beds found \\<in barracks>"));
    }

    [Test]
    public async Task AuditAsyncReturnsNotCompletedWhenRemainingHigh()
    {
        var fake = new FakeDecisionService().ReplyGoal(allDone: 0.8, remaining: 0.7, needsUser: 0.1);
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => SampleGoalCriteria);

        GoalAuditResult result = await auditor.AuditAsync("Build 3 beds", "Built 2 beds, still need 1 more.\n[[GOAL:COMPLETE]]");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.AllDone, Is.True);
        Assert.That(result.Remaining, Is.True);
        Assert.That(result.Completed, Is.False);
        Assert.That(result.Reason, Does.Contain("unfinished follow-up steps"));
    }

    [Test]
    public async Task AuditAsyncReturnsNeedsUserWhenNeedsUserHigh()
    {
        var fake = new FakeDecisionService().ReplyGoal(allDone: 0.2, remaining: 0.4, needsUser: 0.85);
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => SampleGoalCriteria);

        GoalAuditResult result = await auditor.AuditAsync("Build 3 beds", "Where should I place the beds?\n[[GOAL:COMPLETE]]");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.NeedsUser, Is.True);
        Assert.That(result.Completed, Is.False);
        Assert.That(result.Reason, Does.Contain("player input is required"));
    }

    [Test]
    public async Task AuditAsyncReturnsNullWhenDecisionServiceThrows()
    {
        var fake = new FakeDecisionService().Throw(new InvalidOperationException("API timeout"));
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => SampleGoalCriteria);

        GoalAuditResult result = await auditor.AuditAsync("Build 3 beds", "Done.\n[[GOAL:COMPLETE]]");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void AuditAsyncRethrowsWhenCancellationTokenCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeDecisionService();
        var auditor = new DecisionGoalAuditor(fake, criteriaProvider: () => SampleGoalCriteria);

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await auditor.AuditAsync("Build 3 beds", "Done.\n[[GOAL:COMPLETE]]", cts.Token);
        });
    }

    [Test]
    public void DescribeFormatsStateAndEscapesAngleBrackets()
    {
        string state = DecisionGoalAuditor.Describe("build <hospital> & beds", "done with <medicine>");

        Assert.That(state, Does.Contain("<objective>\nbuild \\<hospital> & beds\n</objective>"));
        Assert.That(state, Does.Contain("<agent_reply>\ndone with \\<medicine>\n</agent_reply>"));
    }

    [Test]
    public void DescribeKeepsHeadAndTailOfVeryLongReply()
    {
        string head = new string('h', 3000);
        string tail = new string('t', 3000);
        string longReply = head + new string('x', 20000) + tail;
        string state = DecisionGoalAuditor.Describe("build base", longReply);

        // 開頭說明了做了什麼、結尾是完成報告，中間的過程省略。
        Assert.That(state, Does.Contain("<agent_reply>"));
        Assert.That(state, Does.Contain(new string('h', 100)));
        Assert.That(state, Does.Contain(new string('t', 100)));
        Assert.That(state, Does.Contain("[...]"));
        Assert.That(state.Length, Is.LessThan(12000));
    }

    [Test]
    public void DescribeKeepsShortReplyWhole()
    {
        string state = DecisionGoalAuditor.Describe("build base", "short reply");

        Assert.That(state, Does.Contain("short reply"));
        Assert.That(state, Does.Not.Contain("[...]"));
    }

    [Test]
    public void HandleAuditedGoalDecisionBlockedPausesGoalWithNotice()
    {
        var component = new RimAgentGameComponent(null);
        component.AddGoal("Build 3 beds");
        var session = new ColonyAgentSession(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 4);

        var audit = new GoalAuditResult
        {
            AllDone = false,
            Remaining = false,
            NeedsUser = true,
            AllDoneProbability = 0.2,
            RemainingProbability = 0.1,
            NeedsUserProbability = 0.9,
            Reason = "Decision audit indicated player input is required."
        };
        var decision = new GoalTurnDecision(1, 0, false, false);

        component.HandleAuditedGoalDecision(session, "Build 3 beds", "Which room?\n[[GOAL:COMPLETE]]", decision, audit, GoalAuditVerdict.Blocked);

        Assert.That(component.GoalPaused, Is.True);
        Assert.That(component.CurrentGoalPauseReason, Is.EqualTo(GoalPauseReason.Blocked));
        Assert.That(component.GoalBlockerKey, Is.EqualTo("audit-needs-user"));
        Assert.That(component.GoalNeedsVerification, Is.False);
        var entries = session.Transcript.Snapshot();
        Assert.That(entries, Has.Some.Matches<TranscriptEntry>(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("Build 3 beds")));
    }

    [Test]
    public void HandleAuditedGoalDecisionCompleteFinishesGoalWithNotice()
    {
        var component = new RimAgentGameComponent(null);
        component.AddGoal("Build 3 beds");
        var session = new ColonyAgentSession(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 4);

        var audit = new GoalAuditResult
        {
            AllDone = true,
            Remaining = false,
            NeedsUser = false,
            AllDoneProbability = 0.95,
            RemainingProbability = 0.05,
            NeedsUserProbability = 0.05,
            Reason = "Decision audit verified that all requirements are complete."
        };
        var decision = new GoalTurnDecision(2, 0, true, false);

        component.HandleAuditedGoalDecision(session, "Build 3 beds", "Done.\n[[GOAL:COMPLETE]]", decision, audit, GoalAuditVerdict.Complete);

        Assert.That(component.Goals, Is.Empty);
        Assert.That(component.LastCompletedGoal, Is.EqualTo("Build 3 beds"));
        Assert.That(component.GoalTurns, Is.Zero);
        Assert.That(component.GoalPaused, Is.False);
        var entries = session.Transcript.Snapshot();
        Assert.That(entries, Has.Some.Matches<TranscriptEntry>(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("Build 3 beds")));
    }

    [Test]
    public void HandleAuditedGoalDecisionCannotUpgradeAnUnverifiedTurnToComplete()
    {
        var component = new RimAgentGameComponent(null);
        component.AddGoal("Build 3 beds");
        var session = new ColonyAgentSession(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 4);

        var audit = new GoalAuditResult
        {
            AllDone = true,
            Remaining = false,
            NeedsUser = false,
            AllDoneProbability = 0.95,
            RemainingProbability = 0.05,
            NeedsUserProbability = 0.05,
            Reason = "Decision audit verified that all requirements are complete."
        };
        // AfterTurn 判定沒有完成證據，即使審計說完成也不能升級。
        var decision = new GoalTurnDecision(1, 0, false, false);

        component.HandleAuditedGoalDecision(session, "Build 3 beds", "Done.\n[[GOAL:COMPLETE]]", decision, audit, GoalAuditVerdict.Complete);

        Assert.That(component.Goals, Has.Count.EqualTo(1), "沒有工具證據時不得移除目標");
        Assert.That(component.LastCompletedGoal, Is.Null);
        Assert.That(component.GoalNeedsVerification, Is.True);
        Assert.That(component.GoalAuditNote, Is.EqualTo(audit.Reason));
    }

    [Test]
    public void HandleAuditedGoalDecisionContinueKeepsGoalActiveAndRecordsAuditNote()
    {
        var component = new RimAgentGameComponent(null);
        component.AddGoal("Build 3 beds");
        var session = new ColonyAgentSession(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 4);

        var audit = new GoalAuditResult
        {
            AllDone = false,
            Remaining = true,
            NeedsUser = false,
            AllDoneProbability = 0.4,
            RemainingProbability = 0.8,
            NeedsUserProbability = 0.1,
            Reason = "Still missing wood for bed 3."
        };
        var decision = new GoalTurnDecision(1, 0, false, false);

        component.HandleAuditedGoalDecision(session, "Build 3 beds", "Placed blueprints.\n[[GOAL:COMPLETE]]", decision, audit, GoalAuditVerdict.Continue);

        Assert.That(component.Goals, Has.Count.EqualTo(1));
        Assert.That(component.GoalPaused, Is.False);
        Assert.That(component.GoalNeedsVerification, Is.True);
        Assert.That(component.GoalAuditNote, Is.EqualTo("Still missing wood for bed 3."));
        var entries = session.Transcript.Snapshot();
        Assert.That(entries, Has.Some.Matches<TranscriptEntry>(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("Build 3 beds")));
    }
}
