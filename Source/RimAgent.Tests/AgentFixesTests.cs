using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests;

/// <summary>Agent 領域的零碎修正回歸測試：快取、復原堆疊、風險類別、進度、記錄與存檔。</summary>
[TestFixture]
public class AgentFixesTests
{
    [Test]
    public void InstructionCacheStillUpdatesExistingKeyWhenFull()
    {
        var cache = new InstructionCache<int>();
        for (int i = 0; i < 128; i++) cache.Store("k" + i, i);

        cache.Store("k0", 99);
        cache.Store("new", 1);

        Assert.That(cache.TryGet("k0", out int updated), Is.True);
        Assert.That(updated, Is.EqualTo(99));
        Assert.That(cache.TryGet("new", out _), Is.False);
    }

    [Test]
    public void InstructionCacheAddOrUpdateIsAtomicUnderContention()
    {
        var cache = new InstructionCache<int>();

        Parallel.For(0, 200, _ => cache.AddOrUpdate("k", (exists, previous) => (exists ? previous : 0) + 1));

        Assert.That(cache.TryGet("k", out int value), Is.True);
        Assert.That(value, Is.EqualTo(200));
    }

    [Test]
    public void UndoFrameWithOnlyWorkPriorityToggleIsNotEmpty()
    {
        var stack = new WorkUndoStack();
        stack.BeginFrame();
        stack.RecordUseWorkPriorities(false, true);
        stack.EndFrame("toggle");

        Assert.That(stack.CanUndo, Is.True);
    }

    [Test]
    public void UndoFrameWithUnchangedWorkPriorityToggleIsEmpty()
    {
        var stack = new WorkUndoStack();
        stack.BeginFrame();
        // set_work_priorities 每次都會記錄手動優先級；前後相同代表沒動，不能留下空的復原步。
        stack.RecordUseWorkPriorities(true, true);
        stack.EndFrame("no-op");

        Assert.That(stack.CanUndo, Is.False);
    }

    [Test]
    public void BeginFrameKeepsRecordsOfUnclosedFrame()
    {
        var stack = new WorkUndoStack();
        stack.BeginFrame();
        stack.RecordPriority(1, "Growing", 3, 1);
        stack.BeginFrame();
        stack.EndFrame("second");

        UndoFrame kept = stack.Pop();
        Assert.That(kept, Is.Not.Null);
        Assert.That(kept.Priorities, Has.Count.EqualTo(1));
    }

    [TestCase("irreversible_colony_change")]
    [TestCase("external_side_effect")]
    [TestCase("data_exfiltration")]
    public async Task HighImpactKindForcesRiskyRegardlessOfProbability(string kind)
    {
        var service = new FakeDecisionService().Reply(0.05, 0.95, kind);
        var checker = new DecisionRiskChecker(service, null, () => "## risk\ncustom");

        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "do it",
            ToolName = "tool",
            Arguments = "a=1",
            Preview = "preview"
        }, CancellationToken.None);

        Assert.That(verdict.Risky, Is.True);
    }

    [Test]
    public async Task LowImpactKindStaysNotRiskyWithLowProbability()
    {
        var service = new FakeDecisionService().Reply(0.05, 0.95, "reversible_colony_change");
        var checker = new DecisionRiskChecker(service, null, () => "## risk\ncustom");

        RiskVerdict verdict = await checker.ClassifyAsync(new RiskQuestion
        {
            Instruction = "do it",
            ToolName = "tool",
            Arguments = "a=1",
            Preview = "preview"
        }, CancellationToken.None);

        Assert.That(verdict.Risky, Is.False);
    }

    [Test]
    public async Task DecisionFailureIsLoggedOncePerDistinctMessage()
    {
        var lines = new List<string>();
        Action<string> original = DecisionDiagnostics.Sink;
        DecisionDiagnostics.Sink = lines.Add;
        DecisionDiagnostics.Reset();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var service = new FakeDecisionService().Throw(new InvalidOperationException("boom"));
                var checker = new DecisionRiskChecker(service, null, () => "## risk\ncustom");
                await checker.ClassifyAsync(new RiskQuestion { Instruction = "x", ToolName = "t", Arguments = "a", Preview = "p" }, CancellationToken.None);
            }
        }
        finally
        {
            DecisionDiagnostics.Sink = original;
            DecisionDiagnostics.Reset();
        }

        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain("boom"));
    }

    [Test]
    public void KeylessBlockedRepliesAccumulateTowardsBlock()
    {
        GoalTurnDecision first = GoalProgress.AfterTurn(0, 0, "can't proceed\n[[GOAL:BLOCKED]]", false, false);
        GoalTurnDecision second = GoalProgress.AfterTurn(first.Turns, first.BlockedTurns,
            "still stuck, different words\n[[GOAL:BLOCKED]]", false, false, first.BlockerKey);

        Assert.That(second.BlockedTurns, Is.EqualTo(2));
    }

    [Test]
    public void GlobalMemoryFileSaveLeavesNoTempFilesAndSurvivesParallelWrites()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rimagent-gm-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "global.json");
        try
        {
            Parallel.For(0, 20, i => GlobalMemoryFile.Save(path,
                new List<AgentMemoryEntry> { new() { name = "global_a.md", content = "v" + i } }));

            Assert.That(File.Exists(path), Is.True);
            Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName), Is.EquivalentTo(new[] { "global.json" }));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public void RunLogCaptureTranscriptTwiceDoesNotDoubleCount()
    {
        var entries = new List<TranscriptEntry>
        {
            new() { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 1, Failed = true }
        };
        var record = new RunLogRecord();

        record.CaptureTranscript(entries, 1);
        record.CaptureTranscript(entries, 1);

        Assert.That(record.ToolCalls, Is.EqualTo(1));
        Assert.That(record.ToolFailures, Is.EqualTo(1));
        Assert.That(record.TranscriptSteps, Has.Count.EqualTo(1));
    }

    [Test]
    public void TranscriptSaveDataKeepsCancelledAndDuration()
    {
        var transcript = new AgentTranscript();
        transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", Failed = true, Cancelled = true, DurationMs = 1234 });

        TranscriptEntryData saved = TranscriptEntryData.FromTranscript(transcript).Single();
        TranscriptEntry restored = saved.ToEntry();

        Assert.That(restored.Cancelled, Is.True);
        Assert.That(restored.DurationMs, Is.EqualTo(1234));
    }

    [Test]
    public void AgentPackFrontmatterIgnoresDashesInsideValues()
    {
        AgentSkill skill = AgentPackLoader.ParseSkill("s", "---\nname: x\ndescription: a --- b\n---\nBody text");

        Assert.That(skill.Description, Is.EqualTo("a --- b"));
        Assert.That(skill.Body, Is.EqualTo("Body text"));
    }
}
