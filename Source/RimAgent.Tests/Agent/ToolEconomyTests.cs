using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class ToolUsageStatsTests
{
    private static TranscriptEntry Tool(string name, bool failed = false, string result = "ok", string args = "{}", bool cancelled = false,
        ToolCategory category = ToolCategory.Observe, string agent = "") => new()
    {
        Kind = TranscriptKind.Tool,
        ToolName = name,
        Failed = failed,
        Cancelled = cancelled,
        Result = failed ? ToolText.Error(result) : result,
        Arguments = args,
        ToolCategory = category,
        Agent = agent,
    };

    [Test]
    public void CountsCallsFailuresAndCancellationsPerTool()
    {
        var stats = new ToolUsageStats(path: null);

        stats.Record([Tool("lookup_def"), Tool("lookup_def", failed: true), Tool("build", cancelled: true)], ToolPacks.PackOf);

        Assert.That(stats.Instructions, Is.EqualTo(1));
        Assert.That(stats.Counter("lookup_def").Calls, Is.EqualTo(2));
        Assert.That(stats.Counter("lookup_def").Failures, Is.EqualTo(1));
        Assert.That(stats.Counter("build").Cancellations, Is.EqualTo(1));
    }

    [Test]
    public void PersistsAcrossInstances()
    {
        string path = Path.Combine(Path.GetTempPath(), "rimagent-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new ToolUsageStats(path).Record([Tool("get_colonists")], ToolPacks.PackOf);
            Assert.That(new ToolUsageStats(path).Counter("get_colonists").Calls, Is.EqualTo(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void PacksUsedInHalfOfRecentInstructionsArePreloaded()
    {
        var stats = new ToolUsageStats(path: null);
        for (int i = 0; i < ToolUsageStats.MinRecentForPreload - 1; i++)
        {
            stats.Record(i % 2 == 0 ? [Tool("set_medical_care", category: ToolCategory.Medical)] : [Tool("get_colonists")], ToolPacks.PackOf);
        }
        Assert.That(stats.FrequentPacks(), Is.Empty, "too few instructions to judge");

        stats.Record([Tool("get_colonists", category: ToolCategory.Combat)], ToolPacks.PackOf);

        Assert.That(stats.FrequentPacks(), Is.EqualTo(new[] { "medical" }));
    }

    [Test]
    public void SubagentToolsAndToolFreeInstructionsDoNotDrivePreload()
    {
        var stats = new ToolUsageStats(path: null);
        for (int i = 0; i < ToolUsageStats.RecentWindow; i++)
        {
            stats.Record([Tool("set_medical_care", category: ToolCategory.Medical, agent: "medic"), Tool("get_colonists")], ToolPacks.PackOf);
        }
        Assert.That(stats.FrequentPacks(), Is.Empty, "packs only apply to the main agent");

        var chatty = new ToolUsageStats(path: null);
        for (int i = 0; i < ToolUsageStats.MinRecentForPreload; i++)
        {
            chatty.Record([Tool("set_medical_care", category: ToolCategory.Medical)], ToolPacks.PackOf);
            chatty.Record([], ToolPacks.PackOf);
        }
        Assert.That(chatty.FrequentPacks(), Is.EqualTo(new[] { "medical" }), "instructions without tool calls do not dilute the share");
    }

    [Test]
    public void FailureFixedLaterBecomesALessonAfterThreeInstructions()
    {
        var stats = new ToolUsageStats(path: null);
        TranscriptEntry[] instruction =
        [
            Tool("lookup_def", failed: true, result: "no def named 'Wall 3'", args: "{\"name\":\"Wall\"}"),
            Tool("lookup_def", args: "{\"name\":\"Wall\",\"type\":\"ThingDef\"}"),
        ];
        for (int i = 0; i < ToolUsageStats.LessonConfirmations - 1; i++) stats.Record(instruction, ToolPacks.PackOf);
        Assert.That(stats.LessonsPrompt(), Is.Null);

        stats.Record(instruction, ToolPacks.PackOf);

        string prompt = stats.LessonsPrompt();
        Assert.That(prompt, Does.Contain("lookup_def"));
        Assert.That(prompt, Does.Contain("no def named 'Wall #'"));
        Assert.That(prompt, Does.Contain("\"type\":\"ThingDef\""));
    }

    [Test]
    public void FailureWithoutLaterSuccessIsNotALesson()
    {
        var stats = new ToolUsageStats(path: null);
        for (int i = 0; i < 5; i++) stats.Record([Tool("build", failed: true, result: "blocked")], ToolPacks.PackOf);
        Assert.That(stats.LessonsPrompt(), Is.Null);
    }

    [Test]
    public void LessonIsRetiredWhenTheFailureKeepsHappening()
    {
        var stats = new ToolUsageStats(path: null);
        TranscriptEntry[] learned = [Tool("lookup_def", failed: true, result: "bad"), Tool("lookup_def")];
        for (int i = 0; i < ToolUsageStats.LessonConfirmations; i++) stats.Record(learned, ToolPacks.PackOf);
        Assert.That(stats.LessonsPrompt(), Is.Not.Null);

        for (int i = 0; i < ToolUsageStats.LessonRetireRecurrences; i++) stats.Record([Tool("lookup_def", failed: true, result: "bad")], ToolPacks.PackOf);

        Assert.That(stats.LessonsPrompt(), Is.Null);
    }

    [Test]
    public void SubagentFailuresAreNotLessons()
    {
        var stats = new ToolUsageStats(path: null);
        TranscriptEntry[] sub = [Tool("lookup_def", failed: true, result: "bad", agent: "scout"), Tool("lookup_def", agent: "scout")];
        for (int i = 0; i < 4; i++) stats.Record(sub, ToolPacks.PackOf);
        Assert.That(stats.LessonsPrompt(), Is.Null);
    }
}

[TestFixture]
public class FailureLoopGuardTests
{
    private readonly List<string> _notices = [];

    private FailureLoopGuard Guard(double? stuck) => new((_, _) => Task.FromResult(stuck), _notices.Add);

    [SetUp]
    public void Clear() => _notices.Clear();

    [Test]
    public async Task RedirectsOnceAtTheThresholdWhenTheJudgeAgrees()
    {
        FailureLoopGuard guard = Guard(0.9);
        guard.Reset("build a wall");

        string[] notes = new string[FailureLoopGuard.Threshold * 2];
        for (int i = 0; i < notes.Length; i++) notes[i] = await guard.ObserveAsync("build", ToolText.Error("cell (" + i + ", 4) is blocked"), CancellationToken.None);

        Assert.That(notes.Take(FailureLoopGuard.Threshold - 1), Is.All.Null);
        Assert.That(notes[FailureLoopGuard.Threshold - 1], Does.Contain("<loop_guard>"));
        Assert.That(notes.Skip(FailureLoopGuard.Threshold), Is.All.Null, "at most one redirect per instruction");
        Assert.That(_notices, Has.Count.EqualTo(1), "still stuck after the redirect: tell the player");
    }

    [Test]
    public async Task NoRedirectWhenTheJudgeSaysProgress()
    {
        FailureLoopGuard guard = Guard(0.2);
        guard.Reset("x");
        string note = null;
        for (int i = 0; i < FailureLoopGuard.Threshold; i++) note = await guard.ObserveAsync("build", ToolText.Error("blocked"), CancellationToken.None);
        Assert.That(note, Is.Null);
    }

    [Test]
    public async Task AsksTheJudgeOnlyOncePerFailure()
    {
        int asked = 0;
        var guard = new FailureLoopGuard((_, _) => { asked++; return Task.FromResult<double?>(0.2); }, _notices.Add);
        guard.Reset("x");
        for (int i = 0; i < FailureLoopGuard.Threshold * 3; i++) await guard.ObserveAsync("build", ToolText.Error("blocked"), CancellationToken.None);
        Assert.That(asked, Is.EqualTo(1));
    }

    [Test]
    public async Task RedirectsWithoutAJudge()
    {
        var guard = new FailureLoopGuard(judge: null, _notices.Add);
        guard.Reset("x");
        string note = null;
        for (int i = 0; i < FailureLoopGuard.Threshold; i++) note = await guard.ObserveAsync("build", ToolText.Error("blocked"), CancellationToken.None);
        Assert.That(note, Does.Contain("<loop_guard>"));
    }

    [Test]
    public async Task DifferentErrorsAndSuccessesDoNotCount()
    {
        FailureLoopGuard guard = Guard(0.9);
        guard.Reset("x");
        string[] results = [ToolText.Error("blocked"), "ok", ToolText.Error("no material"), ToolText.Error("blocked"), "ok"];
        foreach (string r in results) Assert.That(await guard.ObserveAsync("build", r, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task ResetStartsANewInstruction()
    {
        FailureLoopGuard guard = Guard(0.9);
        guard.Reset("first");
        for (int i = 0; i < FailureLoopGuard.Threshold - 1; i++) await guard.ObserveAsync("build", ToolText.Error("blocked"), CancellationToken.None);
        guard.Reset("second");
        Assert.That(await guard.ObserveAsync("build", ToolText.Error("blocked"), CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task LoggingFunctionAppendsTheRedirectToTheMainAgentsToolResult()
    {
        var guard = new FailureLoopGuard(judge: null, _notices.Add);
        guard.Reset("x");
        AIFunction inner = AIFunctionFactory.Create(() => ToolText.Error("blocked"), "build");
        var tool = new LoggingAIFunction(inner, new AgentTranscript(), new FakeGameWorld(), ToolCategory.Observe, ToolRisk.Query, gate: null, loopGuard: guard);

        object last = null;
        for (int i = 0; i < FailureLoopGuard.Threshold; i++) last = await tool.InvokeAsync([]);

        Assert.That(last?.ToString(), Does.StartWith(ToolText.ErrorPrefix + "blocked"));
        Assert.That(last?.ToString(), Does.Contain("<loop_guard>"));
    }
}
