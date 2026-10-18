using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Tests;

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
        _permissions = ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work);
    }

    private ColonyAgentSession NewSession(int maxSteps = 8)
    {
        return new ColonyAgentSession(_client, _world, () => _permissions, maxSteps);
    }

    [Test]
    public async Task ToolCallIsExecutedAgainstWorldAndLogged()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Reply("Done: Ali's doctor priority is now 1.");
        ColonyAgentSession session = NewSession();

        string reply = await session.SendAsync("把 Ali 的醫療設成最高");

        Assert.That(reply, Is.EqualTo("Done: Ali's doctor priority is now 1."));
        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));

        IReadOnlyList<TranscriptEntry> transcript = session.Transcript.Snapshot();
        Assert.That(transcript.Select(e => e.Kind), Is.EqualTo([TranscriptKind.User, TranscriptKind.Tool, TranscriptKind.Assistant]));
        TranscriptEntry tool = transcript[1];
        Assert.That(tool.ToolName, Is.EqualTo("set_work_priorities"));
        Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Work));
        Assert.That(tool.Arguments, Does.Contain("assignments=Ali:doctor=1"));
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
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=9" })
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
        List<string> names = [.. options.Tools.OfType<AIFunction>().Select(t => t.Name)];
        Assert.That(names, Does.Contain("get_colony_overview").And.Contain("set_work_priorities"));
        Assert.That(names, Does.Not.Contain("designate_area").And.Not.Contain("draft"));
        Assert.That(options.ToolMode, Is.EqualTo(ChatToolMode.Auto));
    }

    [Test]
    public async Task PermissionChangesApplyToTheNextRequest()
    {
        _client.Reply("ok").Reply("ok");
        ColonyAgentSession session = NewSession();
        await session.SendAsync("one");
        _permissions.Set(ToolCategory.Build, true);
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
        Assert.That(first, Is.EqualTo(AgentPromptComposer.FallbackPreamble));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(_client.ReceivedOptions[0].AdditionalProperties, Is.Null.Or.Empty);
    }

    [Test]
    public async Task UnlimitedStepConfigurationAllowsMoreThanFormerLimit()
    {
        for (int i = 0; i < 21; i++)
        {
            _client.CallTool("get_colony_overview", null);
        }
        _client.Reply("完成");
        ColonyAgentSession session = NewSession(maxSteps: int.MaxValue);

        string reply = await session.SendAsync("連續查詢");

        Assert.That(reply, Is.EqualTo("完成"));
        Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool), Is.EqualTo(21));
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
    public async Task PerRunStepLimitIsAppliedAndThenRestored()
    {
        _client.CallTool("get_colony_overview", null); // 腳本用完後持續要求同一工具。
        ColonyAgentSession session = NewSession(maxSteps: 4);

        await session.SendAsync("budgeted autonomous run", maxToolSteps: 1);
        int firstRunTools = session.Transcript.Snapshot()
            .Count(e => e.Kind == TranscriptKind.Tool && e.InstructionId == 1);

        await session.SendAsync("normal run");
        int secondRunTools = session.Transcript.Snapshot()
            .Count(e => e.Kind == TranscriptKind.Tool && e.InstructionId == 2);

        Assert.That(firstRunTools, Is.LessThanOrEqualTo(1));
        Assert.That(secondRunTools, Is.GreaterThan(1).And.LessThanOrEqualTo(4));
        Assert.That(session.MaxSteps, Is.EqualTo(4));
    }

    [Test]
    public async Task PerRunToolBudgetRejectsExtraCallsFromTheSameModelResponse()
    {
        _client.CallTools(
                   ("set_work_priorities", new { assignments = "Ali:doctor=1" }),
                   ("set_work_priorities", new { assignments = "Bob:doctor=1" }))
               .Reply("Done.");
        ColonyAgentSession session = NewSession(maxSteps: 4);

        await session.SendAsync("inspect twice", autonomous: true, maxToolSteps: 1);

        List<TranscriptEntry> tools = [.. session.Transcript.Snapshot().Where(e => e.Kind == TranscriptKind.Tool)];
        Assert.That(tools.Count(e => e.BudgetConsumed), Is.EqualTo(1));
        Assert.That(tools.Count(e => e.Failed && e.Result.Contains("budget exhausted")), Is.EqualTo(1));
        Assert.That(_world.Calls.Count(c => c.StartsWith("SetWorkPriority(")), Is.EqualTo(1));
    }

    [Test]
    public async Task HittingTheStepLimitAddsTheNoticeAndHidesRawToolCallText()
    {
        // 步數用完時 MEAI 會拿掉工具再要一次回覆；有些模型（Nemotron）就把工具呼叫寫成純文字。
        _client.CallTool("get_colony_overview", null)
               .CallTool("get_colony_overview", null)
               .Reply("<tool_call> <function=inspect_area> <parameter=x1> 124 </parameter> </function> </tool_call>");
        ColonyAgentSession session = NewSession(maxSteps: 2);

        await session.SendAsync("look around");

        IReadOnlyList<TranscriptEntry> entries = session.Transcript.Snapshot();
        TranscriptEntry reply = entries.Last(e => e.Kind == TranscriptKind.Assistant);
        Assert.That(reply.Text, Does.Not.Contain("<tool_call>"), "原始工具呼叫標記不能當成回覆給玩家看");
        Assert.That(reply.ToolsStripped, Is.False, "這不是供應商不支援工具");
        Assert.That(entries.Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("tool-step budget was reached")), Is.True);
    }

    [TestCase("I placed two beds. <tool_call> <function=inspect_area> </function> </tool_call>", "I placed two beds.")]
    [TestCase("Research started.\n[TOOL_CALLS][{\"name\":\"list_colonists\",\"arguments\":{}}]", "Research started.")]
    [TestCase("Done for now. {\"name\": \"inspect_area\", \"arguments\": {\"x1\": 1}}", "Done for now.")]
    [TestCase("<|python_tag|>{\"name\": \"list_colonists\", \"parameters\": {}}", "")]
    [TestCase("Everything is fine.", "Everything is fine.")]
    public void StripToolCallMarkupKeepsTheProseBeforeTheFirstCall(string text, string expected)
    {
        Assert.That(ColonyAgentSession.StripToolCallMarkup(text), Is.EqualTo(expected));
    }

    [Test]
    public async Task FinishingWithinTheStepLimitHasNoBudgetNotice()
    {
        _client.CallTool("get_colony_overview", null).Reply("All good.");
        ColonyAgentSession session = NewSession(maxSteps: 2);

        await session.SendAsync("look around");

        IReadOnlyList<TranscriptEntry> entries = session.Transcript.Snapshot();
        Assert.That(entries.Last(e => e.Kind == TranscriptKind.Assistant).Text, Is.EqualTo("All good."));
        Assert.That(entries.Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("tool-step budget")), Is.False);
    }

    [Test]
    public async Task StrippedToolsDoNotClaimTheStepBudgetWasReached()
    {
        _client.ReplyWithToolsStripped("I cannot use tools right now.");
        ColonyAgentSession session = NewSession();
        await session.SendAsync("do something");

        // 供應商不支援工具時，UI 會依 ToolsStripped 顯示正確原因；不該再多一條「步數用完」的錯誤說明。
        Assert.That(session.Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("tool-step budget")), Is.False);
    }

    [Test]
    public async Task StrippedToolsAreFlaggedOnTheReply()
    {
        _client.ReplyWithToolsStripped("I cannot use tools right now.");
        ColonyAgentSession session = NewSession();
        await session.SendAsync("do something");

        TranscriptEntry reply = session.Transcript.Snapshot().Last(e => e.Kind == TranscriptKind.Assistant);
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
    public async Task FallbackChainFailureIsClassifiedByItsInnerCause()
    {
        // 框架備援鏈全失敗：外層 Unknown 包著最後一次的 402（QuotaExceeded）。
        var inner = new RimLLMException(LLMError.QuotaExceeded, "Payment required");
        _client.Throw(new RimLLMException(LLMError.Unknown, "All fallback attempts failed. Last error: Payment required", inner));
        ColonyAgentSession session = NewSession();

        await session.SendAsync("anything");

        Assert.That(session.Transcript.Snapshot().Last().ErrorKind, Is.EqualTo(AgentErrorKind.Configuration));
        Assert.That(session.Transcript.ErrorKindOf(session.Transcript.CurrentInstructionId), Is.EqualTo(AgentErrorKind.Configuration),
            "自主任務據此暫停一個遊戲日");
    }

    [Test]
    public async Task SuccessfulInstructionHasNoErrorKind()
    {
        _client.Reply("ok");
        ColonyAgentSession session = NewSession();

        await session.SendAsync("anything");

        Assert.That(session.Transcript.ErrorKindOf(session.Transcript.CurrentInstructionId), Is.Null);
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
    public async Task FailedTurnKeepsUndoForChangesAlreadyMade()
    {
        // 工具跑完才在最後一輪炸掉：改動已落地，玩家仍該能一鍵復原。
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Throw(new RimLLMException(LLMError.NetworkError, "boom"));
        ColonyAgentSession session = NewSession();

        Assert.That(await session.SendAsync("set doctor to 1"), Is.Null);

        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));
        Assert.That(session.Undo.CanUndo, Is.True);
        Assert.That(session.UndoLast(), Does.Contain("(failed)"));
        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(3));
    }

    [Test]
    public async Task ContextTooLongAfterAToolDoesNotRetryTheInstruction()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Throw(new RimLLMException(LLMError.ContextWindowExceeded, "too long"))
               .CallTool("set_work_priorities", new { assignments = "Ali:doctor=2" })
               .Reply("Done.");
        ColonyAgentSession session = NewSession();

        Assert.That(await session.SendAsync("set doctor priority"), Is.Null);

        Assert.That(_world.Calls.Count(c => c.StartsWith("SetWorkPriority(")), Is.EqualTo(1));
        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));
        Assert.That(_client.CallCount, Is.EqualTo(2), "已有副作用後不可重跑整句指令");
    }

    [Test]
    public async Task AutonomousInstructionIsMarkedInTranscript()
    {
        _client.Reply("Nothing to do.");
        ColonyAgentSession session = NewSession();

        await session.SendAsync("Routine inspection.", default, autonomous: true);
        await session.SendAsync("player says hi");

        List<TranscriptEntry> users = [.. session.Transcript.Snapshot().Where(e => e.Kind == TranscriptKind.User)];
        Assert.That(users.Select(u => u.Autonomous), Is.EqualTo([true, false]));
        TranscriptEntryData saved = TranscriptEntryData.FromTranscript(session.Transcript).First();
        Assert.That(saved.autonomous, Is.True);
        Assert.That(saved.ToEntry().Autonomous, Is.True);
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
        Assert.That(session.Transcript.Snapshot().Select(e => e.Kind), Is.EqualTo([TranscriptKind.User, TranscriptKind.Assistant]));
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
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
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
    public async Task ConfirmModeBlocksIrreversibleToolThenExecutesOnConfirm()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("I will send Bob to attack the bear.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        string preview = await session.SendAsync("attack the bear");

        Assert.That(preview, Does.Contain("attack the bear"));
        Assert.That(_world.Calls, Is.Empty, "被擋下那次不可碰世界");
        Assert.That(session.HasPendingConfirmation, Is.True);
        Assert.That(session.Transcript.Snapshot().Last().Kind, Is.EqualTo(TranscriptKind.Notice));

        string done = await session.ConfirmAsync();

        Assert.That(done, Is.EqualTo("Executed 1 confirmed command(s)."));
        Assert.That(_world.Calls, Does.Contain("SetDrafted(102,True)").And.Contain("OrderAttack(102,900)"));
        Assert.That(session.HasPendingConfirmation, Is.False);
        Assert.That(_client.CallCount, Is.EqualTo(2), "Confirm must not call the model a second time.");
    }

    [Test]
    public async Task GoalConfirmationProvidesExecutedResultsForNextTurn()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("Waiting for confirmation.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("pursue goal", goalRun: true, displayText: "defeat the bear");
        Assert.That(session.HasPendingGoalConfirmation, Is.True);
        Assert.That(session.TakeGoalConfirmationReport("defeat the bear"), Is.Null);

        await session.ConfirmAsync();

        Assert.That(session.PeekGoalConfirmationReport("defeat the bear"), Does.Contain("order_attack"));
        Assert.That(session.PeekGoalConfirmationReport("another goal"), Is.Null);
        string report = session.TakeGoalConfirmationReport("defeat the bear");
        Assert.That(report, Does.Contain("order_attack").And.Contain("Bob"));
        Assert.That(session.TakeGoalConfirmationReport("defeat the bear"), Is.Null);
    }

    [Test]
    public async Task PlayerConfirmationIsNotOwnedByGoal()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("Waiting for confirmation.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("attack the bear");

        Assert.That(session.HasPendingConfirmation, Is.True);
        Assert.That(session.HasPendingGoalConfirmation, Is.False);
        Assert.That(session.TakeGoalConfirmationReport("defeat the bear"), Is.Null);
    }

    [Test]
    public async Task ConfirmExecutesOnlyTheCapturedCommandAndDoesNotRepeatSafeCommands()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("list_colonists", new { })
               .CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("I need confirmation before the attack.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("who is here and attack the bear");
        var toolEntries = session.Transcript.Snapshot().Where(e => e.Kind == TranscriptKind.Tool).ToList();
        Assert.That(toolEntries.Any(e => e.Result != null && !e.Result.StartsWith(ToolText.ConfirmationPrefix)), Is.True, "查詢直接執行");
        Assert.That(_world.Calls, Does.Not.Contain("OrderAttack(102,900)"), "命令需要確認");

        await session.ConfirmAsync();

        Assert.That(_world.Calls, Does.Contain("OrderAttack(102,900)"));
        Assert.That(_client.CallCount, Is.EqualTo(3), "The confirmation must not re-run the model tool loop.");
    }

    [Test]
    public async Task ConfirmingWithRememberStopsAskingForThatToolThisSession()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Reply("Waiting for confirmation.")
               .CallTool("set_work_priorities", new { assignments = "Ali:doctor=2" })
               .Reply("Done.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("set doctor to 1");
        string done = await session.ConfirmAsync(rememberForSession: true);

        Assert.That(done, Does.Contain("not ask again"));

        await session.SendAsync("now set doctor to 2");

        Assert.That(session.HasPendingConfirmation, Is.False, "同一個工具本局不再擋");
        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(2));
    }

    [Test]
    public async Task RememberedApprovalsCoverOnlyTheApprovedToolAndAreClearedByClear()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Reply("Waiting for confirmation.")
               .CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("The attack still needs confirmation.")
               .CallTool("set_work_priorities", new { assignments = "Ali:doctor=3" })
               .Reply("Waiting again.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("set doctor to 1");
        await session.ConfirmAsync(rememberForSession: true);

        await session.SendAsync("attack the bear");
        Assert.That(session.HasPendingConfirmation, Is.True, "別的工具不受影響");
        Assert.That(_world.Calls, Does.Not.Contain("OrderAttack(102,900)"));

        session.Clear();
        await session.SendAsync("set doctor to 3");

        Assert.That(session.HasPendingConfirmation, Is.True, "對話重來後「不再問」一併歸零");
    }

    [Test]
    public async Task IrreversibleBlacklistedToolsAreNeverRememberedForTheSession()
    {
        _permissions.Set(ToolCategory.Trade, true);
        _client.CallTool("form_caravan", new { colonists = "Bob", tile = 4600 })
               .Reply("Waiting for confirmation.")
               .CallTool("form_caravan", new { colonists = "Ali", tile = 4600 })
               .Reply("Waiting for confirmation again.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("send Bob north");
        Assert.That(session.ConfirmGate.CanRememberPending, Is.False, "YOLO 黑名單上的工具不提供「不再問」");
        await session.ConfirmAsync(rememberForSession: true);

        await session.SendAsync("now send Ali south");

        Assert.That(session.HasPendingConfirmation, Is.True);
    }

    [Test]
    public async Task ConfirmationThatNeverExecutesDoesNotRememberTheTool()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Reply("Waiting for confirmation.")
               .CallTool("set_work_priorities", new { assignments = "Ali:doctor=2" })
               .Reply("Waiting again.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("set doctor to 1");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Assert.That(await session.ConfirmAsync(cts.Token, rememberForSession: true), Is.Null);
        }
        Assert.That(_world.Calls, Is.Empty, "取消時一個都不該執行");

        await session.SendAsync("now set doctor to 2");

        Assert.That(session.HasPendingConfirmation, Is.True, "沒真的跑過的工具不該被記成不再問");
    }

    [Test]
    public async Task McpExecutionIsNeverRememberedForTheSession()
    {
        _permissions = ToolPermissions.For(ToolCategory.Mcp);
        _client.CallTool("mcp_call_tool", new { server = "fetch", tool = "fetch", args = "{}" })
               .Reply("Waiting for confirmation.")
               .CallTool("mcp_call_tool", new { server = "fetch", tool = "fetch", args = "{}" })
               .Reply("Waiting for confirmation again.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("call the MCP tool");
        Assert.That(session.ConfirmGate.CanRememberPending, Is.False, "MCP 不提供「不再問」");
        await session.ConfirmAsync(rememberForSession: true);

        await session.SendAsync("call the MCP tool again");

        Assert.That(session.HasPendingConfirmation, Is.True);
    }

    [Test]
    public async Task NewInstructionDiscardsPreviouslyCapturedCommands()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("First attack needs confirmation.")
               .Reply("Old request discarded.")
               .CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("Second attack needs confirmation.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("first attack");
        await session.SendAsync("do nothing instead");
        await session.SendAsync("second attack");
        string result = await session.ConfirmAsync();

        Assert.That(result, Is.EqualTo("Executed 1 confirmed command(s)."));
        Assert.That(_world.Calls.Count(c => c == "OrderAttack(102,900)"), Is.EqualTo(1));
    }

    [Test]
    public async Task ClearDiscardsCapturedCommandsBeforeTheNextConfirmation()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("First attack needs confirmation.")
               .CallTool("order_attack", new { colonists = "Bob", targetId = 900 })
               .Reply("Second attack needs confirmation.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("first attack");
        session.Clear();
        await session.SendAsync("second attack");
        string result = await session.ConfirmAsync();

        Assert.That(result, Is.EqualTo("Executed 1 confirmed command(s)."));
        Assert.That(_world.Calls.Count(c => c == "OrderAttack(102,900)"), Is.EqualTo(1));
    }

    [TestCase(AgentMode.Autonomous, false)]
    [TestCase(AgentMode.Confirm, true)]
    [TestCase(AgentMode.Yolo, false)]
    public async Task McpExecutionRespectsModeConfirmationPolicy(AgentMode mode, bool expectsConfirmation)
    {
        _permissions = ToolPermissions.For(ToolCategory.Mcp);
        _client.CallTool("mcp_call_tool", new { server = "fetch", tool = "fetch", args = "{}" })
               .Reply(expectsConfirmation ? "Waiting for confirmation." : "Done.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => mode);

        await session.SendAsync("call the MCP tool");

        Assert.That(session.HasPendingConfirmation, Is.EqualTo(expectsConfirmation));
        string result = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool).Result;
        if (expectsConfirmation)
        {
            Assert.That(result, Does.StartWith(ToolText.ConfirmationPrefix));
        }
        else
        {
            Assert.That(result, Does.Not.StartWith(ToolText.ConfirmationPrefix));
        }
    }

    [Test]
    public async Task ConfirmModeRequiresConfirmationForEveryCommand()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Reply("Waiting for confirmation.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Confirm);

        await session.SendAsync("set doctor to 1");

        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.Not.EqualTo(1));
        Assert.That(session.HasPendingConfirmation, Is.True);

        await session.ConfirmAsync();

        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));
        Assert.That(session.HasPendingConfirmation, Is.False);
        Assert.That(session.Undo.CanUndo, Is.True);
    }

    [Test]
    public async Task YoloModeRunsPermittedToolDirectly()
    {
        _client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
               .Reply("Done.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Yolo);

        await session.SendAsync("set doctor to 1");

        Assert.That(_world.Colonists[0].WorkPriorities.First(w => w.WorkTypeDefName == "Doctor").Priority, Is.EqualTo(1));
        Assert.That(session.HasPendingConfirmation, Is.False);
        Assert.That(session.Undo.CanUndo, Is.True);
    }

    [Test]
    public async Task AutonomousModeRunsIrreversibleToolDirectly()
    {
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _permissions.Set(ToolCategory.Combat, true);
        _client.CallTool("order_attack", new { colonists = "Bob", targetId = 900 }).Reply("Done.");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Autonomous);

        await session.SendAsync("attack the bear");

        Assert.That(_world.Calls, Does.Contain("OrderAttack(102,900)"));
        Assert.That(session.HasPendingConfirmation, Is.False);
    }

    [Test]
    public void AskModeOffersOnlyQueryTools()
    {
        var session = new ColonyAgentSession(_client, _world, () => ToolPermissions.All, 8, () => AgentMode.Ask);

        List<string> names = [.. session.BuildRunOptions().Tools.OfType<AIFunction>().Select(t => t.Name)];
        // 包內工具不公告，完整的允許集合看登錄表（會話用同一套過濾）。
        List<string> allowed = [.. new AgentToolRegistry(_world, new AgentTranscript())
            .Select(ToolPermissions.All, AgentMode.Ask).Select(t => t.Name)];

        Assert.That(names, Does.Contain("list_colonists"));
        Assert.That(names, Does.Not.Contain("set_work_priorities"));
        Assert.That(allowed, Does.Contain("get_health").And.Contain("mcp_list_tools"));
        Assert.That(allowed, Does.Not.Contain("execute_trade").And.Not.Contain("mcp_call_tool"));
    }

    [Test]
    public void YoloModeExcludesForbiddenTools()
    {
        var session = new ColonyAgentSession(_client, _world, () => ToolPermissions.All, 8, () => AgentMode.Yolo);

        List<string> advertised = [.. session.BuildRunOptions().Tools.OfType<AIFunction>().Select(t => t.Name)];
        // 包內工具不公告，完整的允許集合看登錄表（會話用同一套過濾）。
        List<string> names = [.. new AgentToolRegistry(_world, new AgentTranscript())
            .Select(ToolPermissions.All, AgentMode.Yolo).Select(t => t.Name)];

        Assert.That(advertised, Does.Contain("set_work_priorities"));
        Assert.That(names, Does.Not.Contain("execute_trade"));
        Assert.That(names, Does.Not.Contain("form_caravan"));
        Assert.That(names, Does.Not.Contain("release_prisoner"));
        Assert.That(names, Does.Not.Contain("schedule_surgery"));
        Assert.That(names, Does.Not.Contain("set_prisoner_interaction"));
        Assert.That(names, Does.Contain("set_work_priorities"));
        Assert.That(names, Does.Contain("order_attack"));
        Assert.That(names, Does.Contain("mcp_call_tool"));
    }

    [Test]
    public void AskModeAppendsReadOnlyNoteToInstructions()
    {
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Ask, () => "COMPOSED PROMPT", askNote: () => "ASK NOTE");

        Assert.That(session.BuildRunOptions().Instructions, Is.EqualTo("COMPOSED PROMPT\n\nASK NOTE"));
        Assert.That(NewSession().BuildRunOptions().Instructions, Is.Null, "非詢問模式不動 Instructions");
    }

    [Test]
    public void AskModeWithoutNoteLeavesInstructionsAlone()
    {
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8, () => AgentMode.Ask, () => "COMPOSED PROMPT");

        Assert.That(session.BuildRunOptions().Instructions, Is.EqualTo("COMPOSED PROMPT"), "缺檔就不加註");
    }

    [Test]
    public void RiskTagsFollowReversibility()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());

        foreach (string name in new[] { "execute_trade", "form_caravan", "release_prisoner", "set_prisoner_interaction", "schedule_surgery", "delete_zone", "delete_bill", "place_blueprint", "designate_area", "order_attack", "accept_quest", "use_right_click_option", "send_caravan", "settle_caravan" })
            Assert.That(registry.RiskOf(name), Is.EqualTo(ToolRisk.Confirm), name);
        foreach (string name in new[] { "set_work_priorities", "set_schedule", "draft", "order_move", "cancel_designation", "set_medical_care", "tame_animal", "train_animal", "set_animal_area", "add_zone", "set_zone_filter", "add_bill", "set_research", "set_time_speed", "set_current_map", "take_screenshot", "dismiss_quest", "set_caravan_movement", "cancel_forming_caravan" })
            Assert.That(registry.RiskOf(name), Is.EqualTo(ToolRisk.Safe), name);
        foreach (string name in new[] { "get_colony_overview", "list_colonists", "get_colonist_wellbeing", "list_recent_letters", "find_things", "inspect_area", "get_health", "list_prisoners", "list_bills", "list_traders", "list_quests", "view_map_grid", "get_building_info", "list_right_click_menu", "list_caravans", "list_world_objects", "get_world_tile", "plan_caravan", "mcp_list_tools" })
            Assert.That(registry.RiskOf(name), Is.EqualTo(ToolRisk.Query), name);
        Assert.That(registry.RiskOf("mcp_call_tool"), Is.EqualTo(ToolRisk.Confirm));
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
    public void PreferredModelBecomesRequestModelId()
    {
        ColonyAgentSession session = NewSession();
        Assert.That(session.BuildRunOptions().ModelId, Is.Null, "未指定時交給框架備援鏈");

        session.PreferredModel = " OpenRouter:deepseek/deepseek-chat ";
        Assert.That(session.BuildRunOptions().ModelId, Is.EqualTo("OpenRouter:deepseek/deepseek-chat"));

        session.PreferredModel = "  ";
        Assert.That(session.BuildRunOptions().ModelId, Is.Null);
    }

    [Test]
    public void DefaultSessionLeavesRunInstructionsUnset()
    {
        ColonyAgentSession session = NewSession();

        Assert.That(session.BuildRunOptions().Instructions, Is.Null);
    }

    [Test]
    public void ReasoningLevelMapsToRunOptions()
    {
        ColonyAgentSession session = NewSession();

        RimLLMChatOptions auto = session.BuildRunOptions();
        Assert.That(auto.Reasoning, Is.Null, "Auto 不設定，交給框架預設");
        Assert.That(auto.DisableReasoning, Is.False);

        session.Reasoning = ReasoningLevel.Off;
        Assert.That(session.BuildRunOptions().DisableReasoning, Is.True);

        session.Reasoning = ReasoningLevel.High;
        RimLLMChatOptions high = session.BuildRunOptions();
        Assert.That(high.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.High));
        Assert.That(high.DisableReasoning, Is.False);
    }

    [Test]
    public void RegistryExposesAllTools()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());
        Assert.That(registry.All.Count, Is.EqualTo(76));
        var observeOnly = new ToolPermissions();
        observeOnly.Set(ToolCategory.Observe, true);
        Assert.That(registry.Select(observeOnly).Count, Is.EqualTo(21));
        Assert.That(registry.Select(new ToolPermissions()), Is.Empty);
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
            // FileMemoryProvider 的參數 schema 由上游產生、沒有逐項說明，無法在這裡補。
            bool upstreamSchema = tool.Name.StartsWith("file_memory_", StringComparison.Ordinal);
            if (hasParameters && !upstreamSchema)
            {
                Assert.That(schema, Does.Contain("\"description\""), $"{tool.Name} 的參數缺少說明");
            }
        }
    }
}
