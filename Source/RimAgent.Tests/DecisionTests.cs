using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests;

/// <summary>測試用 FakeDecisionService，模擬決策模型成功或拋出例外。</summary>
public sealed class FakeDecisionService : IDecisionService
{
    private readonly Queue<Func<string, DecisionPlan, DecisionResult>> _responses = new();

    public int EvaluateCount { get; private set; }
    public List<string> ReceivedStates { get; } = new();
    public List<DecisionPlan> ReceivedPlans { get; } = new();
    /// <summary>測試用：在回傳結果前讓出執行緒，模擬模型呼叫期間設定被修改。</summary>
    public bool YieldBeforeResult { get; set; }

    public FakeDecisionService Reply(double risk, double authorized, string kind = "reversible_colony_change")
    {
        _responses.Enqueue((state, plan) =>
        {
            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
            {
                ["risk"] = new DecisionAnswer { Probability = risk },
                ["authorized"] = new DecisionAnswer { Probability = authorized },
                ["kind"] = new DecisionAnswer { Value = kind }
            };
            return new DecisionResult(answers);
        });
        return this;
    }

    public FakeDecisionService ReplyGoal(double allDone, double remaining, double needsUser)
    {
        _responses.Enqueue((state, plan) =>
        {
            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
            {
                ["all_done"] = new DecisionAnswer { Probability = allDone },
                ["remaining"] = new DecisionAnswer { Probability = remaining },
                ["needs_user"] = new DecisionAnswer { Probability = needsUser }
            };
            return new DecisionResult(answers);
        });
        return this;
    }

    public FakeDecisionService ReplyRoute(string category, double confidence)
    {
        _responses.Enqueue((state, plan) =>
        {
            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
            {
                ["category"] = new DecisionAnswer
                {
                    Value = category,
                    Confidence = confidence,
                    Probability = confidence
                }
            };
            return new DecisionResult(answers);
        });
        return this;
    }

    public FakeDecisionService Throw(Exception ex)
    {
        _responses.Enqueue((state, plan) => throw ex);
        return this;
    }

    public async Task<DecisionResult> EvaluateAsync(string state, DecisionPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EvaluateCount++;
        ReceivedStates.Add(state);
        ReceivedPlans.Add(plan);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No scripted responses available in FakeDecisionService.");
        }

        Func<string, DecisionPlan, DecisionResult> next = _responses.Dequeue();
        if (YieldBeforeResult) await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return next(state, plan);
    }
}

[TestFixture]
public class DecisionTests
{
    private const string SampleCriteria = @"# Risk decision criteria

## risk
How hard is this action to undo, and how much does it affect?
true: Hard or impossible to undo, spends significant resources.
false: Small and reversible.

## authorized
Does the player's own instruction clearly authorize this action?
true: The instruction plainly covers this action.
false: Beyond the instruction, or the assistant chose the target itself.

## kind
What is the most significant effect of this action?
- read_only: Reads and searches; changes nothing.
- reversible_colony_change: Changes colony state in a reversible way.
- irreversible_colony_change: Deletes or destroys colony state that cannot be undone.
";

    [Test]
    public void ParsePlanExtractsExpectedQuestionsAndOptions()
    {
        DecisionPlan plan = DecisionRiskChecker.ParsePlan(SampleCriteria);

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan.Questions, Has.Count.EqualTo(3));

        DecisionQuestion qRisk = plan.Questions[0];
        Assert.That(qRisk.Id, Is.EqualTo("risk"));
        Assert.That(qRisk.Type, Is.EqualTo(DecisionQuestionType.Noul));
        Assert.That(qRisk.Instructions, Does.Contain("undo"));
        Assert.That(qRisk.WhenTrue, Does.Contain("impossible to undo"));
        Assert.That(qRisk.WhenFalse, Does.Contain("reversible"));

        DecisionQuestion qAuth = plan.Questions[1];
        Assert.That(qAuth.Id, Is.EqualTo("authorized"));
        Assert.That(qAuth.Type, Is.EqualTo(DecisionQuestionType.Noul));
        Assert.That(qAuth.Instructions, Does.Contain("authorize"));
        Assert.That(qAuth.WhenTrue, Does.Contain("plainly covers"));
        Assert.That(qAuth.WhenFalse, Does.Contain("Beyond the instruction"));

        DecisionQuestion qKind = plan.Questions[2];
        Assert.That(qKind.Id, Is.EqualTo("kind"));
        Assert.That(qKind.Type, Is.EqualTo(DecisionQuestionType.Choice));
        Assert.That(qKind.Options, Does.ContainKey("read_only").And.ContainKey("reversible_colony_change").And.ContainKey("irreversible_colony_change"));
    }

    [Test]
    public void ParsePlanFallsBackToBuiltInEnglishCriteria()
    {
        DecisionPlan plan = DecisionRiskChecker.ParsePlan("## risk\nsomething custom");

        Assert.That(plan.Questions, Has.Count.EqualTo(3));
        DecisionQuestion qRisk = plan.Questions[0];
        Assert.That(qRisk.Instructions, Does.Contain("something custom"));
        // 沒給 true/false 兩段時用內建英文判準，不是空字串。
        Assert.That(qRisk.WhenTrue, Is.Not.Empty);
        DecisionQuestion qKind = plan.Questions[2];
        Assert.That(qKind.Options, Does.ContainKey("data_exfiltration"));
    }

    [TestCase(0.7, 0.8, true, true, true, TestName = "高風險且已授權（拒絕）")]
    [TestCase(0.3, 0.8, false, true, false, TestName = "低風險且已授權（放行）")]
    [TestCase(0.3, 0.2, false, false, true, TestName = "低風險但未授權（拒絕）")]
    [TestCase(0.7, 0.2, true, false, true, TestName = "高風險且未授權（拒絕）")]
    [TestCase(0.6, 0.7, true, true, true, TestName = "門檻臨界值：0.6為高風險（拒絕）")]
    [TestCase(0.59, 0.7, false, true, false, TestName = "門檻臨界值以下：0.59為低風險且0.7已授權（放行）")]
    public async Task DecisionRiskCheckerThresholdsAndRejection(
        double riskProb, double authProb, bool expectedRisky, bool expectedAuth, bool expectedReject)
    {
        var fakeService = new FakeDecisionService().Reply(riskProb, authProb, "reversible_colony_change");
        var fallbackClient = new ScriptedChatClient();
        var fallbackClassifier = new RiskClassifier(fallbackClient, systemPrompt: () => "fallback prompt");

        var checker = new DecisionRiskChecker(fakeService, fallbackClassifier, () => SampleCriteria);

        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "do something",
            ToolName = "test_tool",
            Arguments = "arg=1",
            Preview = "would test"
        }, CancellationToken.None);

        Assert.That(verdict.Risky, Is.EqualTo(expectedRisky));
        Assert.That(verdict.Authorized, Is.EqualTo(expectedAuth));
        Assert.That(verdict.ShouldReject, Is.EqualTo(expectedReject));
        Assert.That(verdict.Source, Is.EqualTo("decision"));
        Assert.That(verdict.Failed, Is.False);
        Assert.That(fakeService.EvaluateCount, Is.EqualTo(1));
        Assert.That(fallbackClient.CallCount, Is.EqualTo(0), "決策模型成功時不呼叫 LLM fallback");
    }

    [Test]
    public async Task DecisionServiceFailureFallsBackToLLMClassifier()
    {
        var fakeService = new FakeDecisionService().Throw(new DecisionUnavailableException("service timeout"));
        var fallbackClient = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true,\"reason\":\"safe fallback\"}");
        var fallbackClassifier = new RiskClassifier(fallbackClient, systemPrompt: () => "fallback prompt");

        var checker = new DecisionRiskChecker(fakeService, fallbackClassifier, () => SampleCriteria);

        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "set work",
            ToolName = "set_work_priorities",
            Arguments = "assignments=Ali:1",
            Preview = "preview text"
        }, CancellationToken.None);

        Assert.That(fakeService.EvaluateCount, Is.EqualTo(1));
        Assert.That(fallbackClient.CallCount, Is.EqualTo(1), "決策模型失敗時必須呼叫 fallback");
        Assert.That(verdict.Source, Is.EqualTo("fallback"));
        Assert.That(verdict.Risky, Is.False);
        Assert.That(verdict.Authorized, Is.True);
        Assert.That(checker.HasFailed, Is.True);

        // 第二次呼叫：直接走 fallback，決策服務不重試
        fallbackClient.Reply("{\"risk\":\"low\",\"authorized\":true}");
        RiskVerdict verdict2 = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "set work 2",
            ToolName = "set_work_priorities",
            Arguments = "assignments=Bob:1",
            Preview = "preview text 2"
        }, CancellationToken.None);

        Assert.That(fakeService.EvaluateCount, Is.EqualTo(1), "失敗後不再呼叫決策模型");
        Assert.That(fallbackClient.CallCount, Is.EqualTo(2), "直接走 fallback");
        Assert.That(verdict2.Source, Is.EqualTo("fallback"));
    }

    [Test]
    public async Task MissingCriteriaFallsBackToLLMClassifier()
    {
        var fakeService = new FakeDecisionService().Reply(0.1, 0.9);
        var fallbackClient = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
        var fallbackClassifier = new RiskClassifier(fallbackClient, systemPrompt: () => "fallback prompt");

        var checker = new DecisionRiskChecker(fakeService, fallbackClassifier, () => "   ");

        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "test",
            ToolName = "tool",
            Arguments = "arg",
            Preview = "preview"
        }, CancellationToken.None);

        // 從沒試過決策模型就只是走 LLM 分類器，來源仍是 llm 而不是 fallback。
        Assert.That(verdict.Source, Is.EqualTo("llm"));
        Assert.That(fakeService.EvaluateCount, Is.EqualTo(0), "判準檔缺失不呼叫決策模型");
        Assert.That(fallbackClient.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task NotConfiguredDecisionModelUsesLlmClassifierWithoutFailing()
    {
        var fallbackClient = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
        var fallbackClassifier = new RiskClassifier(fallbackClient, systemPrompt: () => "fallback prompt");

        // 玩家還沒在框架設定決策模型：服務工廠回 null。
        var checker = new DecisionRiskChecker(null, fallbackClassifier, () => SampleCriteria, () => null);

        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "test",
            ToolName = "tool",
            Arguments = "arg"
        }, CancellationToken.None);

        Assert.That(verdict.Source, Is.EqualTo("llm"));
        Assert.That(verdict.Failed, Is.False);
        Assert.That(checker.HasFailed, Is.False, "沒設定決策模型不算失敗，下一句還可以再試");
        Assert.That(fallbackClient.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DecisionModelIsRetriedOnTheNextInstruction()
    {
        var fakeService = new FakeDecisionService().Throw(new DecisionUnavailableException("boom"));
        var fallbackClient = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
        var fallbackClassifier = new RiskClassifier(fallbackClient, systemPrompt: () => "fallback prompt");

        var checker = new DecisionRiskChecker(fakeService, fallbackClassifier, () => SampleCriteria);

        await checker.ClassifyAsync(new RiskQuestion { Instruction = "one", ToolName = "t" }, CancellationToken.None);
        Assert.That(checker.HasFailed, Is.True);

        // 新的一句指令：判定快取清掉，決策模型要再試一次（網路恢復後能回來）。
        checker.ClearCache();
        fakeService.Reply(0.1, 0.9);
        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion { Instruction = "two", ToolName = "t" }, CancellationToken.None);

        Assert.That(fakeService.EvaluateCount, Is.EqualTo(2));
        Assert.That(verdict.Source, Is.EqualTo("decision"));
        Assert.That(verdict.Failed, Is.False);
    }

    [Test]
    public void CancellationPropagatesInsteadOfFallingBack()
    {
        var fakeService = new FakeDecisionService().Reply(0.1, 0.9);
        var checker = new DecisionRiskChecker(fakeService, null, () => SampleCriteria);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            checker.ClassifyAsync(new RiskQuestion(), cts.Token));
        Assert.That(fakeService.EvaluateCount, Is.EqualTo(0));
    }

    [Test]
    public async Task VerdictIsCachedForSameInstructionAndArguments()
    {
        var fakeService = new FakeDecisionService()
            .Reply(0.2, 0.9, "reversible_colony_change");
        var checker = new DecisionRiskChecker(fakeService, null, () => SampleCriteria);

        var q = new RiskQuestion
        {
            Instruction = "mine ore",
            ToolName = "designate_mine",
            Arguments = "target=ore_1",
            Preview = "would mine"
        };

        RiskVerdict v1 = await checker.ClassifyAsync(q, CancellationToken.None);
        RiskVerdict v2 = await checker.ClassifyAsync(q, CancellationToken.None);

        Assert.That(fakeService.EvaluateCount, Is.EqualTo(1), "相同指令與參數命中快取");
        Assert.That(v1.Risky, Is.EqualTo(v2.Risky));
        Assert.That(v1.Authorized, Is.EqualTo(v2.Authorized));
    }
}
