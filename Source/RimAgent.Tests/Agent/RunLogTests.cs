using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class RunLogTests
{
    [Test]
    public void WriterKeepsAtMostFiveHundredRecentRuns()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RimAgentRunLogTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new RunLogWriter(folder);
            for (int i = 0; i < RunLogWriter.MaxRuns + 3; i++)
            {
                writer.Append(new RunLogRecord { Instruction = "run-" + i });
            }

            // 第 501 筆觸發裁切：一次裁到 TrimToRuns 筆（含新的那筆），之後兩筆只附加。
            string[] lines = File.ReadAllLines(writer.FilePath);
            Assert.That(lines, Has.Length.EqualTo(RunLogWriter.TrimToRuns + 2));
            Assert.That(lines[0], Does.Contain("\"run-" + (RunLogWriter.MaxRuns + 1 - RunLogWriter.TrimToRuns) + "\""));
            Assert.That(lines.Last(), Does.Contain("run-502"));
            Assert.That(new FileInfo(writer.FilePath).Length, Is.LessThanOrEqualTo(RunLogWriter.MaxFileBytes));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Test]
    public async Task EnabledRunLogCapturesModelStepsAndToolResult()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        var client = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .ReplyWithUsage("Done.", 1234);
        var session = new ColonyAgentSession(client, world,
            () => ToolPermissions.For(ToolCategory.Work), 8);
        RunLogRecord captured = null;
        session.RunLoggingEnabled = () => true;
        session.RunLogSink = record => captured = record;

        await session.SendAsync("set Ali's doctor priority to 1");

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured.ModelSteps, Has.Count.EqualTo(2));
        Assert.That(captured.ModelSteps.Last().InputTokens, Is.EqualTo(1234));
        Assert.That(captured.TranscriptSteps.Any(e => e.Kind == "Tool" && e.Tool == "set_work_priorities"
            && e.Result.Contains("priority")), Is.True);
    }

    [Test]
    public async Task AutonomousRunInfoIsWrittenOnlyForTheRunThatCarriesIt()
    {
        var client = new ScriptedChatClient().Reply("ok").Reply("ok");
        var session = new ColonyAgentSession(client, new FakeGameWorld(), () => ToolPermissions.All, 8);
        var records = new List<RunLogRecord>();
        session.RunLoggingEnabled = () => true;
        session.RunLogSink = records.Add;
        var info = new AutonomousRunInfo
        {
            Trigger = AutonomousTrigger.BedShortage.ToString(),
            Attempt = 2,
            GameTick = 12345,
            Signals = new AutonomousSignals { ColonistCount = 3, BedlessCount = 2 }
        };

        // 只帶脈絡、不帶 autonomous 旗標：紀錄仍要標成自主任務，不能出現「player＋自主脈絡」的矛盾。
        await session.SendAsync("autonomous task", autonomousInfo: info);
        await session.SendAsync("player instruction");

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0].Source, Is.EqualTo("autonomous"));
        string json = System.Text.Json.JsonSerializer.Serialize(records[0]);
        Assert.That(json, Does.Contain("\"Trigger\":\"BedShortage\""));
        Assert.That(json, Does.Contain("\"Attempt\":2"));
        Assert.That(json, Does.Contain("\"GameTick\":12345"));
        Assert.That(json, Does.Contain("\"BedlessCount\":2"));
        Assert.That(records[1].Autonomous, Is.Null, "玩家指令不帶自主脈絡");
    }

    [Test]
    public void LongRunKeepsTheEndOfBothListsAndCountsWhatWasDropped()
    {
        var record = new RunLogRecord();
        var entries = new List<TranscriptEntry>();
        for (int i = 0; i < 50; i++)
        {
            entries.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t" + i, InstructionId = 7 });
            record.AddModelResponse(new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "step " + i)));
        }
        entries.Add(new TranscriptEntry { Kind = TranscriptKind.Assistant, Text = "final", InstructionId = 7 });

        record.CaptureTranscript(entries, 7);

        Assert.That(record.TranscriptSteps.Last().Text, Is.EqualTo("final"), "結尾的回覆不能被截掉");
        Assert.That(record.TranscriptSteps, Has.Count.EqualTo(40));
        Assert.That(record.OmittedTranscriptSteps, Is.EqualTo(11));
        Assert.That(record.ModelSteps.Last().Text, Is.EqualTo("step 49"));
        Assert.That(record.OmittedModelSteps, Is.EqualTo(34));
    }

    [Test]
    public void ToolCountsCoverEveryCallAndSeparateCancellations()
    {
        // 計數要涵蓋被截掉的早期步驟；玩家停止造成的取消不算工具本身失敗（運行紀錄中 wait_for_time 的失敗全是這種）。
        var record = new RunLogRecord();
        var entries = new List<TranscriptEntry>();
        for (int i = 0; i < 45; i++)
        {
            entries.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 3, Failed = i < 5, DurationMs = 12 });
        }
        entries.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "wait_for_time", InstructionId = 3, Failed = true, Cancelled = true });
        entries.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "other", InstructionId = 4, Failed = true });

        record.CaptureTranscript(entries, 3);

        Assert.That(record.ToolCalls, Is.EqualTo(46));
        Assert.That(record.ToolFailures, Is.EqualTo(5));
        Assert.That(record.ToolCancellations, Is.EqualTo(1));
        Assert.That(record.TranscriptSteps.Last().Cancelled, Is.True);
        Assert.That(record.TranscriptSteps.First().DurationMs, Is.EqualTo(12));
    }

    [Test]
    public void ModelIdAndModVersionAreRecorded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "RimAgentRunLogTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var record = new RunLogRecord();
            record.AddModelResponse(new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "hi")) { ModelId = "test-model" });
            var writer = new RunLogWriter(folder) { ModVersion = "2026.11.14.1" };

            writer.Append(record);

            string line = File.ReadAllLines(writer.FilePath).Single();
            Assert.That(line, Does.Contain("\"Model\":\"test-model\"").And.Contain("\"ModVersion\":\"2026.11.14.1\""));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Test]
    public async Task LoggingOffProducesNoRunRecord()
    {
        var client = new ScriptedChatClient().Reply("ok");
        var session = new ColonyAgentSession(client, new FakeGameWorld(), () => ToolPermissions.All, 8);
        int writes = 0;
        session.RunLoggingEnabled = () => false;
        session.RunLogSink = _ => writes++;

        await session.SendAsync("hello");

        Assert.That(writes, Is.Zero);
    }

    [Test]
    public async Task LogWriteFailureDoesNotBreakTheAgentRun()
    {
        var client = new ScriptedChatClient().Reply("ok");
        var session = new ColonyAgentSession(client, new FakeGameWorld(), () => ToolPermissions.All, 8);
        session.RunLoggingEnabled = () => true;
        session.RunLogSink = _ => throw new IOException("disk full");

        string reply = await session.SendAsync("hello");

        Assert.That(reply, Is.EqualTo("ok"));
        Assert.That(session.IsBusy, Is.False);
        Assert.That(session.Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Notice
            && e.Text.Contains("Run log could not be written")), Is.True);
    }

    [Test]
    public async Task ConfirmationExecutionGetsItsOwnLogRecord()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        var client = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .Reply("Waiting for confirmation.");
        var session = new ColonyAgentSession(client, world,
            () => ToolPermissions.For(ToolCategory.Work), 8, () => AgentMode.Confirm);
        var records = new List<RunLogRecord>();
        session.RunLoggingEnabled = () => true;
        session.RunLogSink = records.Add;

        await session.SendAsync("set doctor priority");
        await session.ConfirmAsync();

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[0].Outcome, Is.EqualTo("finished"));
        Assert.That(records[1].Outcome, Is.EqualTo("confirmed"));
        Assert.That(records[1].TranscriptSteps.Any(e => e.Kind == "Tool"
            && !e.Result.StartsWith(ToolText.ConfirmationPrefix)), Is.True);
    }

    [Test]
    public async Task DiscardedConfirmationIsRecorded()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        var client = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .Reply("Waiting for confirmation.");
        var session = new ColonyAgentSession(client, world,
            () => ToolPermissions.For(ToolCategory.Work), 8, () => AgentMode.Confirm);
        var records = new List<RunLogRecord>();
        session.RunLoggingEnabled = () => true;
        session.RunLogSink = records.Add;

        await session.SendAsync("set doctor priority");
        session.DiscardPendingConfirmation();

        Assert.That(records, Has.Count.EqualTo(2));
        Assert.That(records[1].Outcome, Is.EqualTo("discarded"));
        Assert.That(world.Calls, Is.Empty);
    }
}
