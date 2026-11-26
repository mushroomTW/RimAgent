using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.InGameTests;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class RunLogTests
{
    private string _folder;

    [SetUp]
    public void CreateFolderName()
    {
        _folder = Path.Combine(Path.GetTempPath(), "RimAgentRunLogTests-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void DeleteFolder()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static JsonElement[] ReadLines(string path) => [.. ReadShared(path)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement)];

    /// <summary>紀錄進行中檔案仍開著寫；File.ReadAllText 不允許別人持有寫入而開不起來。</summary>
    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    private static JsonElement[] Index(RunLogWriter writer) => ReadLines(writer.IndexPath);

    private static JsonElement[] Events(RunLogWriter writer, JsonElement indexLine) =>
        ReadLines(Path.Combine(writer.Folder, Str(indexLine, "file")));

    private ColonyAgentSession Session(ScriptedChatClient client, FakeGameWorld world = null, Func<AgentMode> mode = null)
    {
        return new ColonyAgentSession(client, world ?? new FakeGameWorld(), () => ToolPermissions.All, 8, mode)
        {
            RunLoggingEnabled = () => true,
            RunLogs = new RunLogWriter(_folder)
        };
    }

    private static FakeGameWorld WorldWithAlice()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        return world;
    }

    [Test]
    public void OldestRunsAreTrimmedWithTheirFiles()
    {
        Assert.That(new RunLogWriter(_folder).MaxRuns, Is.EqualTo(50_000));
        var writer = new RunLogWriter(_folder) { MaxRuns = 200 };
        for (int i = 0; i < 203; i++)
        {
            writer.Start(new RunLogHeader { Source = "player", Instruction = "run-" + i.ToString(CultureInfo.InvariantCulture) }).End("finished");
        }

        // 第 201 筆觸發裁切到 198 筆，之後兩筆只附加。
        JsonElement[] index = Index(writer);
        Assert.That(index, Has.Length.EqualTo(200));
        Assert.That(Str(index[0], "text"), Is.EqualTo("run-3"));
        Assert.That(Str(index[^1], "text"), Is.EqualTo("run-202"));
        Assert.That(Directory.GetFiles(writer.Folder, "*-player.jsonl", SearchOption.AllDirectories), Has.Length.EqualTo(200));
        Assert.That(index.All(line => File.Exists(Path.Combine(writer.Folder, Str(line, "file")))), Is.True);
    }

    [Test]
    public void TrimNeverDeletesOutsideTheRunsFolder()
    {
        var writer = new RunLogWriter(_folder) { MaxRuns = 1 };
        Directory.CreateDirectory(writer.Folder);
        string outside = Path.Combine(_folder, "keep.txt");
        File.WriteAllText(outside, "x");
        File.WriteAllText(writer.IndexPath, "{\"t\":\"end\",\"file\":\"../keep.txt\"}\n");

        writer.Start(new RunLogHeader { Source = "player" }).End("finished");

        Assert.That(Index(writer), Has.Length.EqualTo(1));
        Assert.That(File.Exists(outside), Is.True);
    }

    [Test]
    public async Task RoutedReasoningIsRecorded()
    {
        var decision = new FakeDecisionService();
        decision.ReplyRoute("high", 0.95);
        var session = new ColonyAgentSession(new ScriptedChatClient().Reply("ok").Reply("ok"), new FakeGameWorld(), () => ToolPermissions.All, 8,
            router: new DecisionReasoningRouter(decision, settingsProvider: () => new RimAgentSettings()))
        {
            RunLoggingEnabled = () => true,
            RunLogs = new RunLogWriter(_folder),
            ReasoningRouting = true
        };

        await session.SendAsync("build a sandstone wall");
        session.ReasoningRouting = false;
        session.Reasoning = ReasoningLevel.Low;
        await session.SendAsync("build a sandstone wall");

        JsonElement[] index = Index(session.RunLogs);
        Assert.That(index.Select(l => Str(l, "reasoning")), Is.EqualTo((string[])["High", "Low"]));
        JsonElement routed = Events(session.RunLogs, index[0]).Single(e => Str(e, "t") == "reasoning");
        Assert.That(routed.GetProperty("auto").GetBoolean(), Is.True);
        Assert.That(routed.GetProperty("confidence").GetDouble(), Is.EqualTo(0.95).Within(0.001));
    }

    [Test]
    public void NotesDoNotEvictRuns()
    {
        var writer = new RunLogWriter(_folder) { MaxRuns = 3 };
        writer.Start(new RunLogHeader { Source = "player" }).End("finished");
        for (int i = 0; i < 10; i++) writer.Note(new RunLogHeader { Source = "system_one" }, "ok");

        Assert.That(Index(writer).Select(l => Str(l, "t")), Is.EqualTo((string[])["end", "note", "note", "note"]));
    }

    [Test]
    public void TornIndexLineDoesNotSwallowTheNextRecord()
    {
        var writer = new RunLogWriter(_folder);
        Directory.CreateDirectory(writer.Folder);
        File.WriteAllText(writer.IndexPath, "{\"t\":\"end\",\"trunc");

        writer.Note(new RunLogHeader { Source = "system_one" }, "ok");

        Assert.That(Str(JsonDocument.Parse(File.ReadLines(writer.IndexPath).Last()).RootElement, "outcome"), Is.EqualTo("ok"));
    }

    [Test]
    public void BadArgumentsAndTickFailureDoNotLoseTheRun()
    {
        var writer = new RunLogWriter(_folder) { GameTick = () => throw new InvalidOperationException("tearing down") };
        RunLog log = writer.Start(new RunLogHeader { Source = "player", InstructionId = 1 });
        log.AddEntry(new TranscriptEntry
        {
            Kind = TranscriptKind.Tool,
            ToolName = "t",
            InstructionId = 1,
            Arguments = "x=NaN",
            ArgumentValues = new Dictionary<string, object> { ["x"] = double.NaN }
        });
        log.End("finished");

        Assert.That(Str(Events(writer, Index(writer).Single())[1], "args"), Is.EqualTo("x=NaN"));
    }

    [Test]
    public void CrashedSummaryHasTheSameCountsAsAFinishedOne()
    {
        var first = new RunLogWriter(_folder);
        RunLog log = first.Start(new RunLogHeader
        {
            Source = "autonomous",
            InstructionId = 1,
            Instruction = new string('x', 500),
            Autonomous = new AutonomousRunInfo { Trigger = "Alert", Attempt = 2 }
        });
        log.AddModelResponse(string.Empty, new ChatResponse(new ChatMessage(ChatRole.Assistant, "s"))
        {
            ModelId = "m1",
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }
        });
        log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 1, Failed = true });
        log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 1, Failed = true, Cancelled = true });
        log.Abort();
        var next = new RunLogWriter(_folder);
        next.Start(new RunLogHeader { Source = "player" }).End("finished");

        JsonElement line = Index(next).Single(l => Str(l, "outcome") == "crashed");
        Assert.That((string[])["calls", "fail", "cancel", "steps", "in", "out", "attempt"], Is.All.Matches<string>(n => line.TryGetProperty(n, out _)));
        Assert.That(line.GetProperty("calls").GetInt32(), Is.EqualTo(2));
        Assert.That(line.GetProperty("fail").GetInt32(), Is.EqualTo(1));
        Assert.That(line.GetProperty("cancel").GetInt32(), Is.EqualTo(1));
        Assert.That(line.GetProperty("in").GetInt64(), Is.EqualTo(10));
        Assert.That(line.GetProperty("models")[0].GetString(), Is.EqualTo("m1"));
        Assert.That(Str(line, "trigger"), Is.EqualTo("Alert"));
        Assert.That(Str(line, "text"), Has.Length.LessThan(200));
    }

    [Test]
    public void MalformedEventLinesDoNotBlockCrashRecovery()
    {
        var first = new RunLogWriter(_folder);
        RunLog log = first.Start(new RunLogHeader { Source = "player", InstructionId = 1 });
        log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 1 });
        log.Abort();
        string active = Directory.GetFiles(Path.Combine(first.Folder, "active")).Single();
        File.AppendAllText(active, "{\"t\":\"tool\"}\n{\"t\":\"tool\",\"call\":\"x\"}\n{\"t\":5}\n");

        var next = new RunLogWriter(_folder);
        next.Start(new RunLogHeader { Source = "player" }).End("finished");

        Assert.That(Index(next).Single(l => Str(l, "outcome") == "crashed").GetProperty("calls").GetInt32(), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(Path.Combine(next.Folder, "active")), Is.Empty);
    }

    [Test]
    public async Task TurningLoggingOffMidRunStopsWriting()
    {
        FakeGameWorld world = WorldWithAlice();
        var client = new ScriptedChatClient().CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("secret");
        ColonyAgentSession session = Session(client, world, () => AgentMode.Autonomous);
        session.RunLoggingEnabled = () => world.Calls.Count == 0;

        await session.SendAsync("go");

        JsonElement line = Index(session.RunLogs).Single();
        Assert.That(Str(line, "outcome"), Is.EqualTo("recording_disabled"));
        Assert.That(Events(session.RunLogs, line).Select(e => Str(e, "t")), Is.EqualTo((string[])["run", "reasoning", "model", "end"]));
    }

    [Test]
    public async Task FailureBeforeTheRunStartsLeavesNoOpenLog()
    {
        ColonyAgentSession session = Session(new ScriptedChatClient().Reply("ok"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync("go", onAccepted: () => throw new InvalidOperationException()));

        Assert.That(Directory.Exists(session.RunLogs.Folder), Is.False);
    }

    [Test]
    public async Task RawArgumentsAreReleasedOnceLogged()
    {
        foreach (bool enabled in (bool[])[true, false])
        {
            var client = new ScriptedChatClient().CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("ok");
            ColonyAgentSession session = Session(client, WorldWithAlice(), () => AgentMode.Autonomous);
            session.RunLoggingEnabled = () => enabled;
            await session.SendAsync("go");

            Assert.That(session.Transcript.Snapshot().Select(e => e.ArgumentValues), Is.All.Null);
        }
    }

    [Test]
    public void ChineseAndQuotesAreWrittenReadable()
    {
        var writer = new RunLogWriter(_folder);
        writer.Start(new RunLogHeader { Source = "player", Instruction = "蓋一間\"三人房\"" }).End("finished");

        Assert.That(File.ReadAllText(writer.IndexPath), Does.Contain("蓋一間\\\"三人房\\\"").And.Not.Contain("\\u"));
    }

    [Test]
    public async Task RunLogLinksToolsToModelStepsWithStructuredArguments()
    {
        var client = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .ReplyWithUsage("Done.", 1234);
        ColonyAgentSession session = Session(client, WorldWithAlice(), () => AgentMode.Autonomous);

        await session.SendAsync("set Ali's doctor priority to 1");

        JsonElement line = Index(session.RunLogs).Single();
        Assert.That(Str(line, "outcome"), Is.EqualTo("finished"));
        Assert.That(line.GetProperty("in").GetInt64(), Is.EqualTo(1234));
        Assert.That(line.GetProperty("calls").GetInt32(), Is.EqualTo(1));
        JsonElement[] events = Events(session.RunLogs, line);
        Assert.That(events.Select(e => Str(e, "t")), Is.EqualTo((string[])["run", "reasoning", "model", "tool", "model", "end"]), string.Join(Environment.NewLine, events));
        JsonElement tool = events[3];
        Assert.That(tool.GetProperty("n").GetInt32(), Is.EqualTo(events[2].GetProperty("n").GetInt32()));
        Assert.That(Str(tool.GetProperty("args"), "assignments"), Is.EqualTo("Ali:doctor=1"));
        Assert.That(tool.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(Str(tool, "result"), Does.Contain("priority"));
    }

    [Test]
    public async Task AutonomousRunInfoIsWrittenOnlyForTheRunThatCarriesIt()
    {
        ColonyAgentSession session = Session(new ScriptedChatClient().Reply("ok").Reply("ok"));
        var info = new AutonomousRunInfo
        {
            Trigger = nameof(AutonomousTrigger.Alert),
            Attempt = 2,
            GameTick = 12345,
            Signals = new AutonomousSignals { ColonistCount = 3, AlertCount = 2 }
        };

        // 只帶脈絡、不帶 autonomous 旗標：紀錄仍要標成自主任務，不能出現「player＋自主脈絡」的矛盾。
        await session.SendAsync("autonomous task", autonomousInfo: info);
        await session.SendAsync("player instruction");

        JsonElement[] index = Index(session.RunLogs);
        Assert.That(index, Has.Length.EqualTo(2));
        Assert.That(Str(index[0], "src"), Is.EqualTo("autonomous"));
        Assert.That(Str(index[0], "trigger"), Is.EqualTo("Alert"));
        Assert.That(index[0].GetProperty("attempt").GetInt32(), Is.EqualTo(2));
        JsonElement autonomous = Events(session.RunLogs, index[0])[0].GetProperty("autonomous");
        Assert.That(autonomous.GetProperty("gameTick").GetInt32(), Is.EqualTo(12345));
        Assert.That(autonomous.GetProperty("signals").GetProperty("alertCount").GetInt32(), Is.EqualTo(2));
        Assert.That(Events(session.RunLogs, index[1])[0].TryGetProperty("autonomous", out _), Is.False, "玩家指令不帶自主脈絡");
    }

    [Test]
    public void LongRunKeepsEveryStepAndSeparatesCancellations()
    {
        var writer = new RunLogWriter(_folder);
        RunLog log = writer.Start(new RunLogHeader { Source = "player", InstructionId = 3 });
        for (int i = 0; i < 100; i++)
        {
            log.AddModelResponse(string.Empty, new ChatResponse(new ChatMessage(ChatRole.Assistant, "step")) { ModelId = "m1" });
            log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 3, Failed = i < 5, DurationMs = 12 });
        }
        log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "wait_for_time", InstructionId = 3, Failed = true, Cancelled = true });
        log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "other", InstructionId = 4, Failed = true });
        log.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Reasoning, Text = "thinking", InstructionId = 3 });
        log.End("finished");
        log.End("finished");

        JsonElement line = Index(writer).Single();
        Assert.That(line.GetProperty("calls").GetInt32(), Is.EqualTo(101));
        Assert.That(line.GetProperty("fail").GetInt32(), Is.EqualTo(5));
        Assert.That(line.GetProperty("cancel").GetInt32(), Is.EqualTo(1));
        Assert.That(line.GetProperty("models").EnumerateArray().Single().GetString(), Is.EqualTo("m1"));
        JsonElement[] events = Events(writer, line);
        Assert.That(events, Has.Length.EqualTo(203), "run＋100 model＋101 tool＋end，早期步驟不丟、思考不收");
        Assert.That(events[^2].GetProperty("cancelled").GetBoolean(), Is.True);
    }

    [Test]
    public void ToolUpdateReplacesTheSameCall()
    {
        var writer = new RunLogWriter(_folder);
        RunLog log = writer.Start(new RunLogHeader { Source = "player", InstructionId = 1 });
        var entry = new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "run_subagent", InstructionId = 1, Result = "running…" };
        log.AddEntry(entry);
        entry.Result = "Error: no";
        entry.Failed = true;
        log.UpdateTool(entry);
        log.End("finished");

        JsonElement line = Index(writer).Single();
        Assert.That(line.GetProperty("calls").GetInt32(), Is.EqualTo(1));
        Assert.That(line.GetProperty("fail").GetInt32(), Is.EqualTo(1));
        string report = RunLogStats.Format(File.ReadLines(writer.IndexPath), file => File.ReadLines(Path.Combine(writer.Folder, file)));
        Assert.That(report, Does.Contain("run_subagent (1/1)"), "統計只算最後一行");
    }

    [Test]
    public void CrashedRunIsVisibleImmediatelyAndIndexedOnNextStart()
    {
        var first = new RunLogWriter(_folder) { ModVersion = "v1" };
        RunLog crashed = first.Start(new RunLogHeader { Source = "system_one", Instruction = "before crash", InstructionId = 1 });
        crashed.AddEntry(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "t", InstructionId = 1 });
        string active = Directory.GetFiles(Path.Combine(first.Folder, "active")).Single();
        Assert.That(ReadLines(active), Has.Length.EqualTo(2), "還沒結束就已寫到磁碟");

        // 還開著：視為另一個遊戲程序正在寫，不能搬走。
        new RunLogWriter(_folder).Start(new RunLogHeader { Source = "player" }).End("finished");
        Assert.That(File.Exists(active), Is.True);

        crashed.Abort();
        File.AppendAllText(active, "{\"t\":\"tool\",\"trunc");
        var next = new RunLogWriter(_folder);
        next.Start(new RunLogHeader { Source = "player" }).End("finished");

        JsonElement line = Index(next).Single(l => Str(l, "outcome") == "crashed");
        Assert.That(Str(line, "text"), Is.EqualTo("before crash"));
        Assert.That(Str(line, "ver"), Is.EqualTo("v1"));
        Assert.That(line.GetProperty("calls").GetInt32(), Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(next.Folder, Str(line, "file"))), Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(next.Folder, "active")), Is.Empty);
    }

    [Test]
    public async Task LoggingOffProducesNoRunRecord()
    {
        ColonyAgentSession session = Session(new ScriptedChatClient().Reply("ok"));
        session.RunLoggingEnabled = () => false;

        await session.SendAsync("hello");

        Assert.That(Directory.Exists(_folder), Is.False);
    }

    [Test]
    public async Task LogWriteFailureDoesNotBreakTheAgentRun()
    {
        // 資料夾路徑被同名檔案占住，建不了資料夾。
        await File.WriteAllTextAsync(_folder, "x");
        try
        {
            ColonyAgentSession session = Session(new ScriptedChatClient().Reply("ok"));

            string reply = await session.SendAsync("hello");

            Assert.That(reply, Is.EqualTo("ok"));
            Assert.That(session.IsBusy, Is.False);
            Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Notice
                && e.Text.Contains("Run log could not be written", StringComparison.Ordinal)), Is.EqualTo(1));
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Test]
    public async Task ArchiveFailureMidRunDoesNotBreakTheAgentRun()
    {
        ColonyAgentSession session = Session(new ScriptedChatClient().Reply("ok").Reply("ok"));
        await session.SendAsync("first");
        // 索引被別的程式獨占：結尾寫不進索引。
        await using (new FileStream(session.RunLogs.IndexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.That(await session.SendAsync("second"), Is.EqualTo("ok"));
        }

        Assert.That(session.Transcript.Snapshot().Count(e => e.Text.Contains("Run log could not be written", StringComparison.Ordinal)), Is.EqualTo(1));
        // 留在 active，下次啟動收尾時補進索引，結果仍是原本的。
        var next = new RunLogWriter(_folder);
        next.Start(new RunLogHeader { Source = "player" }).End("finished");
        Assert.That(Index(next).Count(l => Str(l, "text") == "second" && Str(l, "outcome") == "finished"), Is.EqualTo(1));
    }

    [Test]
    public async Task ConfirmationExecutionGetsItsOwnLogRecord()
    {
        var client = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .Reply("Waiting for confirmation.");
        ColonyAgentSession session = Session(client, WorldWithAlice(), () => AgentMode.Confirm);

        await session.SendAsync("set doctor priority");
        await session.ConfirmAsync();

        JsonElement[] index = Index(session.RunLogs);
        Assert.That(index.Select(l => Str(l, "outcome")), Is.EqualTo((string[])["finished", "confirmed"]));
        JsonElement tool = Events(session.RunLogs, index[1]).Single(e => Str(e, "t") == "tool");
        Assert.That(Str(tool.GetProperty("args"), "assignments"), Is.EqualTo("Ali:doctor=1"));
        Assert.That(Str(tool, "result").StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal), Is.False);
    }

    [Test]
    public async Task DiscardedConfirmationIsRecorded()
    {
        FakeGameWorld world = WorldWithAlice();
        var client = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .Reply("Waiting for confirmation.");
        ColonyAgentSession session = Session(client, world, () => AgentMode.Confirm);

        await session.SendAsync("set doctor priority");
        session.DiscardPendingConfirmation();

        JsonElement[] index = Index(session.RunLogs);
        Assert.That(index.Select(l => Str(l, "outcome")), Is.EqualTo((string[])["finished", "discarded"]));
        Assert.That(world.Calls, Is.Empty);
    }
}
