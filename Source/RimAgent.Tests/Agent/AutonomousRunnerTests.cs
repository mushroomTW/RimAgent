using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Tests;

/// <summary>
/// 自主任務執行器：起跑與收尾的旗標、讀檔作廢、取消語彙基元，以及結束後回寫排程器。
/// 這些原本寫在 GameComponent 裡、直接讀遊戲時間，曾出過 CTS 為 null、停止後仍重試等問題。
/// </summary>
[TestFixture]
public class AutonomousRunnerTests
{
    private FakeGameWorld _world;
    private ScriptedChatClient _client;
    private AutonomousScheduler _scheduler;
    private AutonomousConfig _config;
    private List<string> _warnings;
    private int? _now;
    private AutonomousRunner _runner;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        _client = new ScriptedChatClient();
        _scheduler = new AutonomousScheduler();
        _config = new AutonomousConfig
        {
            Enabled = true,
            IntervalTicks = 15000,
            WindowTicks = 60000,
            CooldownTicks = AutonomousScheduler.CooldownTicks,
            OnFire = true
        };
        _warnings = [];
        _now = 0;
        _runner = new AutonomousRunner(_scheduler, () => _now, _warnings.Add);
    }

    private ColonyAgentSession NewSession()
    {
        return new ColonyAgentSession(_client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work), 8, () => AgentMode.Autonomous);
    }

    private static AutonomousSignals ActiveFire()
    {
        return new AutonomousSignals { FoodNutrition = 100f, ColonistCount = 1, HasActiveResearch = true, AnyWorkingPower = true, FireCount = 1 };
    }

    private AutonomousRunRequest Request(ColonyAgentSession session, AutonomousTrigger trigger = AutonomousTrigger.Fire, int tick = 0)
    {
        return new AutonomousRunRequest { Session = session, Trigger = trigger, Tick = tick, Config = _config, Instruction = "Put out the fire." };
    }

    /// <summary>一輪完整的自主任務：在 RunGuardedAsync 裡呼叫 RunTaskAsync，與 GameComponent 的用法相同。</summary>
    private async Task<(bool Completed, string Reply)> RunAsync(AutonomousRunRequest request)
    {
        (bool, string) outcome = default;
        await _runner.RunGuardedAsync(async (generation, token) => outcome = await _runner.RunTaskAsync(request, generation, token));
        return outcome;
    }

    [Test]
    public async Task RunningFlagCoversTheWholeBody()
    {
        var gate = new TaskCompletionSource();
        Task run = _runner.RunGuardedAsync((_, _) => gate.Task);

        Assert.That(_runner.IsRunning, Is.True, "起跑時就要同步標記，否則同一幀的檢查會再起一輪");
        gate.SetResult();
        await run;
        Assert.That(_runner.IsRunning, Is.False);
    }

    /// <summary>讀檔後新的一輪已經開始，舊的一輪收尾不能把它的旗標關掉。</summary>
    [Test]
    public async Task StaleRunDoesNotClearTheNextRunsFlag()
    {
        var oldGate = new TaskCompletionSource();
        var newGate = new TaskCompletionSource();
        Task oldRun = _runner.RunGuardedAsync((_, _) => oldGate.Task);
        _runner.Invalidate();
        Task newRun = _runner.RunGuardedAsync((_, _) => newGate.Task);

        oldGate.SetResult();
        await oldRun;
        Assert.That(_runner.IsRunning, Is.True);

        newGate.SetResult();
        await newRun;
        Assert.That(_runner.IsRunning, Is.False);
    }

    [Test]
    public void InvalidateCancelsTheRunningTokenAndMarksItStale()
    {
        int generation = -1;
        CancellationToken token = default;
        var gate = new TaskCompletionSource();
        _ = _runner.RunGuardedAsync((g, t) =>
        {
            generation = g;
            token = t;
            return gate.Task;
        });

        _runner.Invalidate();

        Assert.That(token.IsCancellationRequested, Is.True);
        Assert.That(_runner.IsCurrent(generation), Is.False);
        Assert.That(_runner.IsRunning, Is.False);
        gate.SetResult();
    }

    /// <summary>讀檔後還沒起跑過就按停止不能出錯；之後每一輪都拿到沒被取消的新 token。</summary>
    [Test]
    public async Task EachRunGetsAFreshTokenEvenAfterStop()
    {
        Assert.DoesNotThrow(_runner.Stop);
        CancellationToken first = default;
        await _runner.RunGuardedAsync((_, t) =>
        {
            first = t;
            _runner.Stop();
            return Task.CompletedTask;
        });

        CancellationToken second = default;
        await _runner.RunGuardedAsync((_, t) =>
        {
            second = t;
            return Task.CompletedTask;
        });

        Assert.That(first.IsCancellationRequested, Is.True);
        Assert.That(second.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task CompletedRunRecordsCooldownAndToolUse()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("Fire handled.");

        (bool completed, string reply) = await RunAsync(Request(NewSession()));

        Assert.That(completed, Is.True);
        Assert.That(reply, Is.EqualTo("Fire handled."));
        Assert.That(_scheduler.Runs, Is.EqualTo(1));
        Assert.That(_scheduler.ToolsUsed, Is.EqualTo(1));
        Assert.That(_runner.IsRunning, Is.False);
    }

    /// <summary>玩家按停止：該條件解除前不再重試；沒用到工具就不吃冷卻。</summary>
    [Test]
    public async Task StoppedRunWithoutToolsDismissesTheTriggerAndSkipsCooldown()
    {
        Assert.That(_scheduler.Evaluate(0, ActiveFire(), _config), Is.EqualTo(AutonomousTrigger.Fire));
        _client.Reply("Stopping.").Do(_runner.Stop);

        // SendAsync 可能吃掉取消、也可能擲出 OperationCanceledException；兩種都要憑 token 壓住重試。
        await RunAsync(Request(NewSession()));

        Assert.That(_scheduler.Runs, Is.EqualTo(0), "沒消耗工具配額就不吃冷卻");
        Assert.That(_scheduler.Evaluate(2500, ActiveFire(), _config), Is.EqualTo(AutonomousTrigger.None), "退避到了也不重試");
        Assert.That(_scheduler.TakeGiveUps(), Is.Empty, "玩家自己按的停止不發放棄通知");
    }

    [Test]
    public async Task StoppedRunThatUsedToolsStillCountsCooldown()
    {
        Assert.That(_scheduler.Evaluate(0, ActiveFire(), _config), Is.EqualTo(AutonomousTrigger.Fire));
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("Stopping.").Do(_runner.Stop);

        await RunAsync(Request(NewSession()));

        Assert.That(_scheduler.Runs, Is.EqualTo(1));
        Assert.That(_scheduler.ToolsUsed, Is.EqualTo(1));
        Assert.That(_scheduler.Evaluate(2500, ActiveFire(), _config), Is.EqualTo(AutonomousTrigger.None), "停止照樣壓住重試");
    }

    /// <summary>玩家在同一幀搶先送了指令：這次沒跑，不算一次自主任務。</summary>
    [Test]
    public async Task BusySessionDoesNotCountAsARun()
    {
        ColonyAgentSession session = NewSession();
        Assert.That(session.TryBeginExclusiveWork(), Is.True);

        (bool completed, _) = await RunAsync(Request(session));

        Assert.That(completed, Is.False);
        Assert.That(_client.CallCount, Is.EqualTo(0));
        Assert.That(_scheduler.Runs, Is.EqualTo(0));
        Assert.That(_warnings, Is.Empty);
    }

    /// <summary>任務途中讀檔：舊遊戲的結果不能改新遊戲的排程器。</summary>
    [Test]
    public async Task RunInvalidatedMidwayLeavesTheSchedulerUntouched()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("Done.").Do(_runner.Invalidate);

        (bool completed, _) = await RunAsync(Request(NewSession()));

        Assert.That(completed, Is.False);
        Assert.That(_scheduler.Runs, Is.EqualTo(0));
        Assert.That(_scheduler.ToolsUsed, Is.EqualTo(0));
    }

    [Test]
    public async Task RunStartedForAStaleGenerationDoesNothing()
    {
        ColonyAgentSession session = NewSession();
        (bool, string) outcome = default;
        await _runner.RunGuardedAsync(async (generation, token) =>
        {
            _runner.Invalidate();
            outcome = await _runner.RunTaskAsync(Request(session), generation, token);
        });

        Assert.That(outcome.Item1, Is.False);
        Assert.That(_client.CallCount, Is.EqualTo(0));
    }

    /// <summary>金鑰、額度等設定錯誤：暫停一個遊戲日並告知玩家，失敗照常吃冷卻。</summary>
    [Test]
    public async Task ConfigurationErrorPausesForADay()
    {
        _client.Throw(new RimLLMException(LLMError.InvalidKey, "bad key"));
        ColonyAgentSession session = NewSession();

        await RunAsync(Request(session, tick: 1000));

        Assert.That(_runner.HoldUntilTick, Is.EqualTo(1000 + AutonomousRunner.ConfigurationErrorHoldTicks));
        Assert.That(session.Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("paused for one in-game day")), Is.True);
        Assert.That(_scheduler.Runs, Is.EqualTo(1));
    }

    [Test]
    public async Task TransientErrorDoesNotPause()
    {
        _client.Throw(new RimLLMException(LLMError.Timeout, "slow"));

        await RunAsync(Request(NewSession(), tick: 1000));

        Assert.That(_runner.HoldUntilTick, Is.EqualTo(0));
    }

    /// <summary>重試退避從任務結束起算；任務期間沒有遊戲可讀時退回起跑 tick。</summary>
    [TestCase(5000, 5000)]
    [TestCase(null, 300)]
    public async Task RetryBackoffStartsFromTheFinishTick(int? finishTick, int expectedStart)
    {
        Assert.That(_scheduler.Evaluate(300, ActiveFire(), _config), Is.EqualTo(AutonomousTrigger.Fire));
        _client.Reply("Working on it.");
        _now = finishTick;

        await RunAsync(Request(NewSession(), tick: 300));

        Assert.That(_scheduler.AttemptOf(AutonomousTrigger.Fire), Is.EqualTo(1));
        List<AutonomousWatch> watch = [.. _scheduler.CaptureWatch()];
        Assert.That(watch.Single(w => w.Trigger == (int)AutonomousTrigger.Fire).LastTick, Is.EqualTo(expectedStart));
    }
}
