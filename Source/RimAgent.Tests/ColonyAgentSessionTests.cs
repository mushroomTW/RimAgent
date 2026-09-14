using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Tests
{
    /// <summary>
    /// 整合測試：假 IChatClient + 真工具 + 假世界。從兩條接縫外側驅動，只斷言可觀察結果。
    /// </summary>
    [TestFixture]
    public class ColonyAgentSessionTests
    {
        private FakeGameWorld _world;
        private ScriptedChatClient _client;
        private ToolPermissions _permissions;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _client = new ScriptedChatClient();
            _permissions = new ToolPermissions { Observe = true, Work = true, Build = false, Combat = false };
        }

        private ColonyAgentSession NewSession(int maxSteps = 8)
        {
            return new ColonyAgentSession(_client, _world, () => _permissions, maxSteps);
        }

        [Test]
        public async Task ToolCallIsExecutedAgainstWorldAndLogged()
        {
            _client.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
                   .Reply("Done: Ali's doctor priority is now 1.");
            ColonyAgentSession session = NewSession();

            string reply = await session.SendAsync("把 Ali 的醫療設成最高");

            Assert.That(reply, Is.EqualTo("Done: Ali's doctor priority is now 1."));
            Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));

            IReadOnlyList<TranscriptEntry> transcript = session.Transcript.Snapshot();
            Assert.That(transcript.Select(e => e.Kind), Is.EqualTo(new[] { TranscriptKind.User, TranscriptKind.Tool, TranscriptKind.Assistant }));
            TranscriptEntry tool = transcript[1];
            Assert.That(tool.ToolName, Is.EqualTo("set_work_priority"));
            Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Work));
            Assert.That(tool.Arguments, Does.Contain("colonist=Ali").And.Contain("priority=1"));
            Assert.That(tool.Result, Is.EqualTo("Set Ali's doctor priority 3 -> 1."));
            Assert.That(tool.Failed, Is.False);

            // 第二次呼叫模型時，歷史要含工具呼叫與結果。
            Assert.That(_client.CallCount, Is.EqualTo(2));
            IList<ChatMessage> secondCall = _client.ReceivedMessages[1];
            Assert.That(secondCall.Any(m => m.Contents.OfType<FunctionCallContent>().Any()), Is.True);
            Assert.That(secondCall.Any(m => m.Contents.OfType<FunctionResultContent>().Any()), Is.True);
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task ToolErrorTextIsMarkedFailedAndFedBackToModel()
        {
            _client.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 9 })
                   .Reply("That priority is invalid.");
            ColonyAgentSession session = NewSession();

            await session.SendAsync("set doctor to 9");

            TranscriptEntry tool = session.Transcript.Snapshot()[1];
            Assert.That(tool.Failed, Is.True);
            Assert.That(tool.Result, Does.StartWith("Error:"));
            FunctionResultContent fedBack = _client.ReceivedMessages[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
            Assert.That(fedBack.Result?.ToString(), Does.Contain("Error:"));
        }

        [Test]
        public async Task DisabledCategoriesAreNotOffered()
        {
            _client.Reply("ok");
            await NewSession().SendAsync("hello");

            ChatOptions options = _client.ReceivedOptions[0];
            List<string> names = options.Tools.OfType<AIFunction>().Select(t => t.Name).ToList();
            Assert.That(names, Does.Contain("get_colony_overview").And.Contain("set_work_priority"));
            Assert.That(names, Does.Not.Contain("designate_area").And.Not.Contain("draft"));
            Assert.That(options.ToolMode, Is.EqualTo(ChatToolMode.Auto));
        }

        [Test]
        public async Task PermissionChangesApplyToTheNextRequest()
        {
            _client.Reply("ok").Reply("ok");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("one");
            _permissions.Build = true;
            await session.SendAsync("two");

            Assert.That(_client.ReceivedOptions[0].Tools.OfType<AIFunction>().Any(t => t.Name == "place_blueprint"), Is.False);
            Assert.That(_client.ReceivedOptions[1].Tools.OfType<AIFunction>().Any(t => t.Name == "place_blueprint"), Is.True);
        }

        [Test]
        public async Task SystemPromptGoesToInstructionsAndIsStable()
        {
            _client.Reply("a").Reply("b");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("one");
            await session.SendAsync("two");

            // 系統提示走 ChatOptions.Instructions（AF 正統），不再經 CachedContext。
            // AF 的 CreateConfiguredChatOptions 對 run 端 ChatOptions 做 Clone()，
            // 底層收到的是基底 ChatOptions（RimLLMChatOptions 的欄位全在 AdditionalProperties，本就穿透）。
            string first = _client.ReceivedOptions[0].Instructions;
            string second = _client.ReceivedOptions[1].Instructions;
            Assert.That(first, Is.EqualTo(AgentSystemPrompt.Text));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(_client.ReceivedOptions[0].AdditionalProperties, Is.Null.Or.Empty);
        }

        [Test]
        public async Task MaxStepsBoundsARunawayLoop()
        {
            _client.CallTool("get_colony_overview", null); // 腳本用完會重複最後一則：模型永遠要求再呼叫工具
            ColonyAgentSession session = NewSession(maxSteps: 3);

            Assert.DoesNotThrowAsync(async () => await session.SendAsync("loop forever"));

            Assert.That(_client.CallCount, Is.LessThanOrEqualTo(4));
            Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool), Is.LessThanOrEqualTo(3));
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task StrippedToolsAreFlaggedOnTheReply()
        {
            _client.ReplyWithToolsStripped("I cannot use tools right now.");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("do something");

            TranscriptEntry reply = session.Transcript.Snapshot().Last();
            Assert.That(reply.Kind, Is.EqualTo(TranscriptKind.Assistant));
            Assert.That(reply.ToolsStripped, Is.True);
        }

        [TestCase(LLMError.QuotaExceeded, AgentErrorKind.Configuration)]
        [TestCase(LLMError.InvalidKey, AgentErrorKind.Configuration)]
        [TestCase(LLMError.ProviderOffline, AgentErrorKind.Configuration)]
        [TestCase(LLMError.Timeout, AgentErrorKind.Transient)]
        [TestCase(LLMError.RateLimit, AgentErrorKind.Transient)]
        [TestCase(LLMError.NetworkError, AgentErrorKind.Transient)]
        [TestCase(LLMError.ContextWindowExceeded, AgentErrorKind.ContextTooLong)]
        [TestCase(LLMError.ContentFilter, AgentErrorKind.Unknown)]
        public async Task FrameworkErrorsAreClassifiedAndTheUserMessageIsRolledBack(LLMError error, AgentErrorKind expected)
        {
            _client.Throw(new RimLLMException(error, "boom"));
            ColonyAgentSession session = NewSession();

            string reply = await session.SendAsync("anything");

            Assert.That(reply, Is.Null);
            TranscriptEntry last = session.Transcript.Snapshot().Last();
            Assert.That(last.Kind, Is.EqualTo(TranscriptKind.Error));
            Assert.That(last.ErrorKind, Is.EqualTo(expected));
            Assert.That(last.Text, Does.Contain("boom"));
            Assert.That(session.History, Is.Empty, "失敗的請求不該留下沒有回覆的使用者訊息");
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task FailedTurnLeavesNoTraceInLaterHistory()
        {
            // 失敗那一輪的使用者訊息不進 AF session：靠 AF ChatHistoryProvider 在例外時略過儲存，
            // 而非自己的回滾碼。下一句成功時，底層收到的歷史不該含有失敗那句。
            _client.Throw(new RimLLMException(LLMError.Timeout, "boom")).Reply("recovered");
            ColonyAgentSession session = NewSession();

            Assert.That(await session.SendAsync("failed question"), Is.Null);
            Assert.That(await session.SendAsync("next question"), Is.EqualTo("recovered"));

            IList<ChatMessage> secondCall = _client.ReceivedMessages[1];
            Assert.That(secondCall.Count(m => m.Role == ChatRole.User), Is.EqualTo(1));
            Assert.That(secondCall.Single(m => m.Role == ChatRole.User).Text, Is.EqualTo("next question"));
            Assert.That(secondCall.SelectMany(m => m.Contents).OfType<TextContent>().Any(c => c.Text.Contains("failed question")), Is.False);
        }

        [Test]
        public async Task CancellationIsReportedAsCancelled()
        {
            _client.Reply("never");
            ColonyAgentSession session = NewSession();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            string reply = await session.SendAsync("stop me", cts.Token);

            Assert.That(reply, Is.Null);
            Assert.That(session.Transcript.Snapshot().Last().ErrorKind, Is.EqualTo(AgentErrorKind.Cancelled));
            Assert.That(session.History, Is.Empty);
        }

        [Test]
        public async Task SecondInstructionCarriesTheWholeHistory()
        {
            _client.CallTool("get_colony_overview", null).Reply("Two colonists.").Reply("Sure.");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("status?");
            await session.SendAsync("thanks");

            IList<ChatMessage> thirdCall = _client.ReceivedMessages[2];
            Assert.That(thirdCall.Count(m => m.Role == ChatRole.User), Is.EqualTo(2));
            Assert.That(thirdCall.Any(m => m.Contents.OfType<FunctionResultContent>().Any()), Is.True);
            Assert.That(thirdCall.Last().Text, Is.EqualTo("thanks"));
        }

        [Test]
        public async Task ClearForgetsHistoryAndTranscript()
        {
            _client.Reply("hi").Reply("again");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("one");
            session.Clear();
            await session.SendAsync("two");

            Assert.That(_client.ReceivedMessages[1].Count(m => m.Role == ChatRole.User), Is.EqualTo(1));
            Assert.That(session.Transcript.Snapshot().Select(e => e.Kind), Is.EqualTo(new[] { TranscriptKind.User, TranscriptKind.Assistant }));
        }

        [Test]
        public async Task UnknownToolNameDoesNotCrashTheSession()
        {
            _client.CallTool("fly_to_the_moon", new { speed = 9 }).Reply("I can't do that.");
            ColonyAgentSession session = NewSession();

            string reply = await session.SendAsync("go");

            Assert.That(reply, Is.EqualTo("I can't do that."));
            Assert.That(session.Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Tool), Is.False);
        }

        [Test]
        public async Task UpdatedMaxStepsApplyWithoutReloading()
        {
            _client.CallTool("get_colony_overview", null); // 腳本用完會重複最後一則：模型永遠要求再呼叫工具
            ColonyAgentSession session = NewSession(maxSteps: 8);
            session.UpdateMaxSteps(2);

            Assert.That(session.MaxSteps, Is.EqualTo(2));
            Assert.DoesNotThrowAsync(async () => await session.SendAsync("loop forever"));

            Assert.That(_client.CallCount, Is.LessThanOrEqualTo(3));
            Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool), Is.LessThanOrEqualTo(2));
            Assert.That(session.IsBusy, Is.False);
        }

        [Test]
        public async Task EntriesOfOneInstructionShareAnInstructionId()
        {
            _client.CallTool("get_colony_overview", null).Reply("Two colonists.").Reply("Sure.");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("status?");
            await session.SendAsync("thanks");

            IReadOnlyList<TranscriptEntry> transcript = session.Transcript.Snapshot();
            // 第一句：User + Tool + Assistant 同為 1；第二句：User + Assistant 同為 2。
            Assert.That(transcript[0].InstructionId, Is.EqualTo(1));
            Assert.That(transcript[1].InstructionId, Is.EqualTo(1));
            Assert.That(transcript[2].InstructionId, Is.EqualTo(1));
            Assert.That(transcript[3].InstructionId, Is.EqualTo(2));
            Assert.That(transcript[4].InstructionId, Is.EqualTo(2));
        }

        [Test]
        public async Task UndoRestoresWorkPriority()
        {
            _client.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
                   .Reply("Done.");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("set doctor to 1");
            Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));

            string summary = session.UndoLast();

            Assert.That(summary, Does.Contain("restored 1"));
            Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(3));
            Assert.That(session.Undo.CanUndo, Is.False);
            Assert.That(session.UndoLast(), Is.Null);
            Assert.That(session.Transcript.Snapshot().Last().Kind, Is.EqualTo(TranscriptKind.Notice));
        }

        [Test]
        public async Task UndoRestoresTimetable()
        {
            _client.CallTool("set_schedule", new { colonists = "Ali", startHour = 22, endHour = 23, assignment = "sleep" })
                   .Reply("Done.");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("Ali sleeps at 22");
            Assert.That(_world.Colonists[0].Timetable[22], Is.EqualTo("Sleep"));

            string summary = session.UndoLast();

            Assert.That(summary, Does.Contain("restored 1"));
            Assert.That(_world.Colonists[0].Timetable[22], Is.EqualTo("Anything"));
        }

        [Test]
        public async Task ReadOnlyInstructionLeavesNothingToUndo()
        {
            _client.CallTool("get_colony_overview", null).Reply("Two colonists.");
            ColonyAgentSession session = NewSession();
            await session.SendAsync("status?");

            Assert.That(session.Undo.CanUndo, Is.False);
            Assert.That(session.UndoLast(), Is.Null);
        }

        [Test]
        public void TranscriptSaveRoundTripKeepsKindsAndInstructionIds()
        {
            var transcript = new AgentTranscript();
            transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = "hi", InstructionId = 2 });
            transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Tool,
                ToolName = "draft",
                ToolCategory = ToolCategory.Combat,
                Arguments = "colonists=all",
                Result = "Drafted 2.",
                InstructionId = 2
            });

            List<TranscriptEntryData> data = TranscriptEntryData.FromTranscript(transcript);

            Assert.That(data.Count, Is.EqualTo(2));
            TranscriptEntry back = data[1].ToEntry();
            Assert.That(back.Kind, Is.EqualTo(TranscriptKind.Tool));
            Assert.That(back.ToolName, Is.EqualTo("draft"));
            Assert.That(back.ToolCategory, Is.EqualTo(ToolCategory.Combat));
            Assert.That(back.Result, Is.EqualTo("Drafted 2."));
            Assert.That(back.InstructionId, Is.EqualTo(2));
        }

        [Test]
        public void TranscriptSaveTruncatesLongFieldsAndKeepsTheTail()
        {
            var transcript = new AgentTranscript();
            for (int i = 0; i < TranscriptEntryData.MaxSavedEntries + 10; i++)
            {
                transcript.Add(new TranscriptEntry { Kind = TranscriptKind.User, Text = "x" + i, InstructionId = i });
            }
            transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, Result = new string('x', 3000) });

            List<TranscriptEntryData> data = TranscriptEntryData.FromTranscript(transcript);

            Assert.That(data.Count, Is.EqualTo(TranscriptEntryData.MaxSavedEntries));
            Assert.That(data[data.Count - 1].result.Length, Is.EqualTo(TranscriptEntryData.MaxFieldChars + 1));
            Assert.That(data[0].ToEntry().InstructionId, Is.EqualTo(11));
        }

        [Test]
        public async Task ConfirmationModeBlocksThenExecutesOnConfirm()
        {
            _client.CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
                   .Reply("I will set Ali's doctor priority to 1.")
                   .CallTool("set_work_priority", new { colonist = "Ali", workType = "doctor", priority = 1 })
                   .Reply("Done.");
            var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => true);

            string preview = await session.SendAsync("set doctor to 1");

            Assert.That(preview, Does.Contain("set Ali's doctor"));
            Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority,
                Is.EqualTo(3), "被擋下那次不可碰世界");
            Assert.That(session.HasPendingConfirmation, Is.True);
            Assert.That(session.Undo.CanUndo, Is.False, "預覽沒有改世界，不該留下可復原的框");
            Assert.That(session.Transcript.Snapshot().Last().Kind, Is.EqualTo(TranscriptKind.Notice));

            string done = await session.ConfirmAsync();

            Assert.That(done, Is.EqualTo("Done."));
            Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));
            Assert.That(session.HasPendingConfirmation, Is.False);
        }

        [Test]
        public async Task ConfirmWithoutPendingReturnsNull()
        {
            Assert.That(await NewSession().ConfirmAsync(), Is.Null);
        }

        [Test]
        public void ComposedInstructionsOverrideCorePerRun()
        {
            var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, null, () => "COMPOSED PROMPT");

            Assert.That(session.BuildRunOptions().Instructions, Is.EqualTo("COMPOSED PROMPT"));
        }

        [Test]
        public void DefaultSessionLeavesRunInstructionsUnset()
        {
            ColonyAgentSession session = NewSession();

            Assert.That(session.BuildRunOptions().Instructions, Is.Null);
        }

        [Test]
        public void RegistryExposesAllTools()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());
            Assert.That(registry.All.Count, Is.EqualTo(45));
            Assert.That(registry.Select(new ToolPermissions { Observe = true, Work = false, Build = false, Combat = false }).Count, Is.EqualTo(9));
            Assert.That(registry.Select(new ToolPermissions { Observe = false, Work = false, Build = false, Combat = false }), Is.Empty);
        }

        [Test]
        public void EveryToolHasADescriptionAndDescribedParameters()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());
            foreach (AIFunction tool in registry.All.OfType<AIFunction>())
            {
                Assert.That(tool.Description, Is.Not.Empty, tool.Name);
                string schema = tool.JsonSchema.ToString();
                Assert.That(schema, Does.Contain("\"type\""), tool.Name);
                bool hasParameters = schema.Contains("\"properties\"") && !schema.Contains("\"properties\":{}");
                if (hasParameters)
                {
                    Assert.That(schema, Does.Contain("\"description\""), $"{tool.Name} 的參數缺少說明");
                }
            }
        }
    }
}
