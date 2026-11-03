using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>時間類別（time issue 01）：查詢流速、切換暫停與加速。</summary>
[TestFixture]
public class TimeToolsTests
{
    private FakeGameWorld _world;
    private TimeTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _tools = new TimeTools(_world);
    }

    [Test]
    public async Task WaitLetsTimePassAndReturnsTheOverview()
    {
        string text = await _tools.WaitForTime(6, CancellationToken.None);

        Assert.That(text, Does.StartWith("Waited 6 in-game hour(s).").And.Contain("Colony: New Arrivals"));
        Assert.That(_world.TicksGame, Is.EqualTo(6 * 2500));
    }

    [TestCase(0)]
    [TestCase(25)]
    public async Task WaitRejectsHoursOutOfRange(int hours)
    {
        Assert.That(await _tools.WaitForTime(hours, CancellationToken.None), Does.StartWith("Error:").And.Contain("1-24"));
        Assert.That(_world.TicksGame, Is.Zero);
    }

    [Test]
    public async Task WaitWhilePausedIsAnError()
    {
        _world.Paused = true;

        string text = await _tools.WaitForTime(2, CancellationToken.None);

        Assert.That(text, Does.StartWith("Error:").And.Contain("paused").And.Contain("set_time_speed"));
    }

    [Test]
    public async Task WaitStopsEarlyWhenHostilesAppear()
    {
        _world.EventAtTick = 3000;
        _world.OnEvent = () => _world.Overview.HostileCount = 5;

        string text = await _tools.WaitForTime(12, CancellationToken.None);

        Assert.That(text, Does.StartWith("Stopped waiting after about").And.Contain("new hostiles appeared"));
        Assert.That(_world.TicksGame, Is.LessThan(12 * 2500));
    }

    [Test]
    public async Task WaitStopsEarlyWhenAColonistGoesDown()
    {
        _world.EventAtTick = 3000;
        _world.OnEvent = () => _world.MedicalEmergencies.Add(101);

        string text = await _tools.WaitForTime(12, CancellationToken.None);

        Assert.That(text, Does.StartWith("Stopped waiting after about").And.Contain("colonist went down or started bleeding"));
        Assert.That(_world.TicksGame, Is.LessThan(12 * 2500));
    }

    [Test]
    public async Task WaitStopsWhenANewColonistGoesDownEvenIfAnotherRecovered()
    {
        // 開始時 101、102 倒地；102 恢復的同時 103 倒下，人數仍是 2，但 103 是新的急診。
        _world.MedicalEmergencies = [101, 102];
        _world.EventAtTick = 3000;
        _world.OnEvent = () => _world.MedicalEmergencies = [101, 103];

        string text = await _tools.WaitForTime(12, CancellationToken.None);

        Assert.That(text, Does.StartWith("Stopped waiting after about").And.Contain("colonist went down"));
        Assert.That(_world.TicksGame, Is.LessThan(12 * 2500));
    }

    [Test]
    public async Task WaitStopsWhenARecoveredColonistGoesDownAgain()
    {
        _world.MedicalEmergencies = [101];
        _world.EventAtTick = 1000;
        _world.OnEvent = () =>
        {
            _world.MedicalEmergencies = [];
            _world.EventAtTick = 3000;
            _world.OnEvent = () => _world.MedicalEmergencies = [101];
        };

        string text = await _tools.WaitForTime(12, CancellationToken.None);

        Assert.That(text, Does.StartWith("Stopped waiting after about").And.Contain("colonist went down"));
    }

    [Test]
    public async Task WaitStopsEarlyWhenAFireStarts()
    {
        _world.EventAtTick = 3000;
        _world.OnEvent = () => _world.FireCount = 2;

        string text = await _tools.WaitForTime(12, CancellationToken.None);

        Assert.That(text, Does.StartWith("Stopped waiting after about").And.Contain("fire started"));
        Assert.That(_world.TicksGame, Is.LessThan(12 * 2500));
    }

    [Test]
    public async Task WaitKeepsGoingForEmergenciesThatWereAlreadyThere()
    {
        // 和敵人同一規則：只看「變多」。模型開始等的時候就知道的狀況不該讓它一直提早回來。
        _world.MedicalEmergencies = [101];
        _world.FireCount = 1;

        string text = await _tools.WaitForTime(2, CancellationToken.None);

        Assert.That(text, Does.StartWith("Waited 2 in-game hour(s)."));
        Assert.That(_world.TicksGame, Is.EqualTo(2 * 2500));
    }

    [Test]
    public async Task WaitStopsEarlyWhenTheGameGetsPaused()
    {
        _world.EventAtTick = 5000;
        _world.OnEvent = () => _world.Paused = true;

        string text = await _tools.WaitForTime(12, CancellationToken.None);

        // 暫停發生在第 20 段途中：只算完整跑完的 19 段（4750 tick）。
        Assert.That(text, Does.StartWith("Stopped waiting after about 1.9 of 12").And.Contain("paused"));
    }

    [Test]
    public async Task GameErrorsLoggedWhileWaitingAreNotBlamedOnTheWait()
    {
        var client = new ScriptedChatClient();
        client.CallTool("wait_for_time", new { hours = 2 }).Reply("done.");
        _world.EventAtTick = 1000;
        _world.OnEvent = () => _world.PendingGameErrors.Add("SomeOtherMod exploded");
        var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Time), 8);

        await session.SendAsync("wait two hours");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.Result, Does.Not.Contain("Game errors").And.Not.Contain("exploded"));
    }

    [Test]
    public async Task WaitInvalidatesTheQueryCacheSoLaterQueriesSeeNewState()
    {
        var client = new ScriptedChatClient();
        client.CallTool("get_colony_overview", new { })
              .CallTool("wait_for_time", new { hours = 1 })
              .CallTool("get_colony_overview", new { })
              .Reply("done.");
        _world.EventAtTick = 2500;
        _world.OnEvent = () => _world.Overview.Hour = 15;
        var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Time), 8, () => AgentMode.Autonomous);

        await session.SendAsync("wait an hour");

        System.Collections.Generic.List<TranscriptEntry> tools = [.. session.Transcript.Snapshot().Where(e => e.Kind == TranscriptKind.Tool)];
        Assert.That(tools.Select(t => t.ToolName), Is.EqualTo(["get_colony_overview", "wait_for_time", "get_colony_overview"]));
        Assert.That(tools.All(t => !t.Failed), Is.True);
        Assert.That(tools[0].Result, Does.Contain("14:00"));
        Assert.That(tools[2].Result, Does.Contain("15:00"), "等待後的查詢不吃等待前的快取");
    }

    [Test]
    public void TimePermissionOffUntilGrantedButSettingsDefaultOn()
    {
        Assert.That(new ToolPermissions().Allows(ToolCategory.Time), Is.False);
        Assert.That(new RimAgentSettings().ToPermissions().Allows(ToolCategory.Time), Is.True, "設定頁預設全開");
    }

    [Test]
    public void RegistryGatesTheTimeToolsByPermission()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());

        Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Time)).OfType<AIFunction>().Count(t => t.Name == "set_time_speed"), Is.EqualTo(1));
        Assert.That(registry.Select(new ToolPermissions()).OfType<AIFunction>().Any(t => t.Name == "set_time_speed"), Is.False);
    }

    [Test]
    public void OverviewReportsCurrentTimeSpeed()
    {
        string text = new ObserveTools(_world).GetColonyOverview();

        Assert.That(text, Does.Contain("Time speed: ").And.Contain("(Normal)"));
    }

    [TestCase("Paused", "Paused")]
    [TestCase("暫停", "Paused")]
    [TestCase("fast", "Fast")]
    [TestCase("超快", "Superfast")]
    public void SetTimeSpeedSwitchesSpeed(string input, string expected)
    {
        string text = _tools.SetTimeSpeed(input);

        Assert.That(text, Does.StartWith("Time speed").And.Contain(expected));
        Assert.That(_world.CurrentTimeSpeed, Is.EqualTo(expected));
    }

    [Test]
    public void UnknownSpeedReportsError()
    {
        string text = _tools.SetTimeSpeed("lightspeed");

        Assert.That(text, Does.StartWith("Error:"));
        Assert.That(_world.CurrentTimeSpeed, Is.EqualTo("Normal"));
    }

    [Test]
    public async Task SetTimeSpeedIsLoggedAsAToolCall()
    {
        var client = new ScriptedChatClient();
        client.CallTool("set_time_speed", new { speed = "Paused" }).Reply("Done.");
        var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Time), 8);

        await session.SendAsync("先暫停遊戲");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.ToolName, Is.EqualTo("set_time_speed"));
        Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Time));
        Assert.That(tool.Result, Does.Contain("Paused"));
    }
}
