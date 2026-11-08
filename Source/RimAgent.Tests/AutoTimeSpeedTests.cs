using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests;

[TestFixture]
public class AutoTimeSpeedTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeService(string value, double confidence) : IDecisionService
    {
        public Task<DecisionResult> EvaluateAsync(string state, DecisionPlan plan, CancellationToken cancellationToken = default)
        {
            var answers = new Dictionary<string, DecisionAnswer>
            {
                ["speed"] = new DecisionAnswer { Value = value, Confidence = confidence }
            };
            return Task.FromResult(new DecisionResult(answers));
        }
    }

    [Test]
    public void NoDecisionModelSpeedsUpToFastWhenSafe()
    {
        var auto = new AutoTimeSpeed();
        Assert.That(auto.Step("Normal", true, T0, () => "state"), Is.EqualTo("Fast"));
        Assert.That(auto.Step("Fast", true, T0.AddSeconds(3), () => "state"), Is.Null);
    }

    [Test]
    public void UnsafeDropsFastBackToNormalAndLeavesNormalAlone()
    {
        var auto = new AutoTimeSpeed();
        Assert.That(auto.Step("Superfast", false, T0, () => "state"), Is.EqualTo("Normal"));
        Assert.That(auto.Step("Normal", false, T0.AddSeconds(3), () => "state"), Is.Null);
    }

    [Test]
    public void PausedIsNeverTouched()
    {
        var auto = new AutoTimeSpeed();
        Assert.That(auto.Step("Paused", true, T0, () => "state"), Is.Null);
        Assert.That(auto.Step("Paused", false, T0, () => "state"), Is.Null);
    }

    [Test]
    public void PlayerChangeStartsBackoffButSafetyStillSlowsDown()
    {
        var auto = new AutoTimeSpeed();
        Assert.That(auto.Step("Normal", true, T0, () => "state"), Is.EqualTo("Fast"));
        // 玩家把速度改回正常：這段時間不再加速。
        Assert.That(auto.Step("Normal", true, T0.AddSeconds(3), () => "state"), Is.Null);
        Assert.That(auto.Step("Normal", true, T0.AddMinutes(4), () => "state"), Is.Null);
        Assert.That(auto.Step("Normal", true, T0.AddMinutes(6), () => "state"), Is.EqualTo("Fast"));
        // 退讓期間玩家開到超加速，出事了仍要降速。
        Assert.That(auto.Step("Superfast", true, T0.AddMinutes(6).AddSeconds(3), () => "state"), Is.Null);
        Assert.That(auto.Step("Superfast", false, T0.AddMinutes(6).AddSeconds(6), () => "state"), Is.EqualTo("Normal"));
    }

    [Test]
    public void DecisionAnswerIsUsedOnTheNextStep()
    {
        var auto = new AutoTimeSpeed(() => new FakeService("superfast", 0.9));
        // 假服務同步完成，答案在同一次呼叫內就緒；真服務在背景完成，要到下一次呼叫才生效。
        Assert.That(auto.Step("Normal", true, T0, () => "state"), Is.EqualTo("Superfast"));
        Assert.That(auto.Desired, Is.EqualTo("Superfast"));
        Assert.That(auto.Step("Superfast", true, T0.AddSeconds(3), () => "state"), Is.Null);
    }

    [Test]
    public void ParseFallsBackToFastWhenUnsure()
    {
        var low = new DecisionResult(new Dictionary<string, DecisionAnswer>
        {
            ["speed"] = new DecisionAnswer { Value = "superfast", Confidence = 0.2 }
        });
        Assert.That(AutoTimeSpeed.Parse(low), Is.EqualTo("Fast"));
        Assert.That(AutoTimeSpeed.Parse(new DecisionResult()), Is.EqualTo("Fast"));
        Assert.That(AutoTimeSpeed.Parse(null), Is.EqualTo("Fast"));
    }

    [Test]
    public void IsSafeNeedsNoHostilesFiresOrEmergencies()
    {
        Assert.That(AutoTimeSpeed.IsSafe(0, 0, 0), Is.True);
        Assert.That(AutoTimeSpeed.IsSafe(1, 0, 0), Is.False);
        Assert.That(AutoTimeSpeed.IsSafe(0, 1, 0), Is.False);
        Assert.That(AutoTimeSpeed.IsSafe(0, 0, 1), Is.False);
    }
}
