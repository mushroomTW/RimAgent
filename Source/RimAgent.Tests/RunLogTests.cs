using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
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
                .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
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
            Assert.That(captured.TranscriptSteps.Any(e => e.Kind == "Tool" && e.Tool == "set_work_priority"
                && e.Result.Contains("priority")), Is.True);
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
                .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
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
                .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
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
}
