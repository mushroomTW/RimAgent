using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class ContextMeterTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    [Test]
    public void GrowthBetweenRequestsIsSplitAcrossAddedContentByEstimate()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        meter.Observe([user], 1000, "p:m");
        Assert.That(meter.Measured(user.Contents[0]), Is.Null, "沒有固定部分的精確值時，第一次請求拆不出訊息大小");

        var call = new FunctionCallContent("c1", "get_colonists", new Dictionary<string, object> { ["filter"] = "all" });
        var result = new FunctionResultContent("c1", new string('x', 400));
        var assistant = new ChatMessage(ChatRole.Assistant, [call]);
        var tool = new ChatMessage(ChatRole.Tool, [result]);
        meter.Observe([user, assistant, tool], 1300, "p:m");

        Assert.That(meter.Measured(call) + meter.Measured(result), Is.EqualTo(300));
        Assert.That(meter.Measured(result), Is.GreaterThan(meter.Measured(call)));
    }

    [Test]
    public void ProjectionAddsNewContentAndSubtractsRemovedContent()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        var result = new FunctionResultContent("c1", new string('x', 400));
        var tool = new ChatMessage(ChatRole.Tool, [result]);
        meter.Observe([user], 1000, "p:m");
        meter.Observe([user, tool], 1250, "p:m");

        ChatMessage next = User(new string('y', 40));
        Assert.That(meter.Project([user, tool, next]), Is.EqualTo(1250 + ContextMeter.Estimate(next.Contents[0])));
        Assert.That(meter.Project([user]), Is.EqualTo(1000), "拿掉的內容扣它推回的真實大小");
    }

    [Test]
    public void MissingUsageOrModelSwitchDropsTheAnchor()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        meter.Observe([user], 1000, "p:a");
        meter.Observe([user], null, "p:a");
        Assert.That(meter.Project([user]), Is.Null, "這次沒回報用量就不能沿用上一次的數字");

        meter.Observe([user], 1000, "p:a");
        var tool = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "result text")]);
        meter.Observe([user, tool], 5000, "p:b");
        Assert.That(meter.Measured(tool.Contents[0]), Is.Null, "不同模型的 tokenizer 不同，差值沒有意義");
        Assert.That(meter.LastReal, Is.EqualTo(5000));
    }

    [Test]
    public void ImagesAddedWithoutCounterpartSkipAttribution()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        meter.Observe([user], 1000, "p:m");
        var shot = new ChatMessage(ChatRole.User, [new TextContent("screenshot"), new DataContent(new byte[] { 1, 2, 3 }, "image/jpeg")]);
        meter.Observe([user, shot], 2000, "p:m");
        Assert.That(meter.Measured(shot.Contents[0]), Is.Null, "差值含圖片的真實大小，不能全算給文字");
    }

    [Test]
    public void ReconcileKeepsExactPartsAndScalesOnlyEstimates()
    {
        IReadOnlyList<ColonyAgentSession.ContextPart> parts =
        [
            new("SystemPrompt", 500, exact: true),
            new("UserMessages", 100),
            new("ToolResults", 300)
        ];
        IReadOnlyList<ColonyAgentSession.ContextPart> shown = ColonyAgentSession.ReconcileParts(parts, 1300);
        Assert.That(shown[0].Tokens, Is.EqualTo(500));
        Assert.That(shown[1].Tokens + shown[2].Tokens, Is.EqualTo(800));
        Assert.That(shown.Sum(p => p.Tokens), Is.EqualTo(1300));
    }

    [Test]
    public void ReconcileLeavesDeferredToolPacksOutOfTheTotal()
    {
        IReadOnlyList<ColonyAgentSession.ContextPart> parts =
        [
            new("SystemPrompt", 500),
            new("DeferredPacks", 2000, count: 3, deferred: true)
        ];
        IReadOnlyList<ColonyAgentSession.ContextPart> shown = ColonyAgentSession.ReconcileParts(parts, 1000);
        Assert.That(shown[0].Tokens, Is.EqualTo(1000));
        Assert.That(shown[1].Tokens, Is.EqualTo(2000));
    }

    [Test]
    public void BreakdownReportsUnloadedToolPacksSeparately()
    {
        ColonyAgentSession session = new(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 8) { PreferredModel = "Test:model" };
        ColonyAgentSession.ContextPart deferred = session.GetContextBreakdown().Single(p => p.Key == "DeferredPacks");
        Assert.That(deferred.Deferred, Is.True);
        Assert.That(deferred.Count, Is.GreaterThan(0));
        Assert.That(deferred.Tokens, Is.GreaterThan(0));
    }

    [Test]
    public void PanelBudgetMatchesTheTriggerBudget()
    {
        ColonyAgentSession session = new(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.For(ToolCategory.Observe), 8,
            contextWindowLookup: _ => 200000) { PreferredModel = "Test:model" };
        session.SyncAutoCompact(enabled: true, 95);
        Assert.That(session.GetContextStatus().CompactBudget, Is.EqualTo(170000),
            "門檻要扣掉預留空間，面板不能只算 視窗×百分比");
    }

    [TestCase(200000, 95, 170000, Description = "15% 大於 16k")]
    [TestCase(100000, 95, 84000, Description = "15% 不足 16k，留 16k")]
    [TestCase(20000, 95, 17000, Description = "視窗不到 32k，只留 15%")]
    [TestCase(200000, 60, 120000, Description = "玩家門檻較低時照門檻")]
    public void ReserveFollowsOpencode(int window, int threshold, long expected)
    {
        Assert.That(ColonyAgentSession.CompactionBudget(window, threshold), Is.EqualTo(expected));
    }

    [TestCase(200000, 64000, 30000, Description = "受預留量限制：視窗 15%")]
    [TestCase(128000, 8192, 8192, Description = "受模型輸出上限限制")]
    [TestCase(1000000, null, 8192, Description = "輸出上限不明時用預設值")]
    [TestCase(null, 32000, 32000, Description = "視窗不明時照模型輸出上限")]
    [TestCase(8000, 4096, 1200, Description = "小視窗只留 15%")]
    public void OutputBudgetFollowsModelLimits(int? window, int? outputLimit, int expected)
    {
        Assert.That(ColonyAgentSession.OutputBudget(window, outputLimit), Is.EqualTo(expected));
    }

    [Test]
    public void RunOptionsUseTheModelsOutputBudget()
    {
        ColonyAgentSession session = new(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.For(ToolCategory.Observe), 8,
            contextWindowLookup: _ => 128000, outputLimitLookup: m => m == "P:small" ? 8192 : 64000) { PreferredModel = "P:big" };

        Assert.That(session.CreateRunOptions(null, "P:small").MaxOutputTokens, Is.EqualTo(8192));
        Assert.That(session.CreateRunOptions(null, null).MaxOutputTokens, Is.EqualTo(19200), "沒指定模型時用主模型：128k 的 15%");
    }

    [Test]
    public void ReplyIsSizedByReportedOutputTokens()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        meter.Observe([user], 1000, "p:m");
        var reply = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_colonists", new Dictionary<string, object> { ["filter"] = "all" })]);
        meter.ObserveOutput([reply], 37);

        Assert.That(meter.Project([user, reply]), Is.EqualTo(1037), "回覆的大小用回報的輸出 token，不用字元估算");
    }

    [Test]
    public void ReasoningTokensAreNotCountedAsReplySize()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        meter.Observe([user], 1000, "p:m");
        var reply = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("thinking..."), new TextContent("answer")]);
        meter.ObserveOutput([reply], 500, 460);

        Assert.That(meter.Measured(reply.Contents[1]), Is.EqualTo(40));
        Assert.That(meter.Measured(reply.Contents[0]), Is.Null);
    }

    [Test]
    public void FoldedContentIsNotPendingAndNewContentIs()
    {
        var meter = new ContextMeter();
        ChatMessage user = User("hello");
        var folded = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 400))]);
        ChatMessage kept = User("again");
        meter.Observe([user, kept], 1000, "p:m");
        ChatMessage next = User(new string('y', 40));

        (HashSet<AIContent> f, long? pending) = meter.Inspect([user, folded, kept, next]);
        Assert.That(f, Does.Contain(folded.Contents[0]));
        Assert.That(pending, Is.EqualTo(ContextMeter.Estimate(next.Contents[0])));
    }

    [Test]
    public void OutputBudgetTakesSmallestAcrossFallbackChain()
    {
        int budget = ColonyAgentSession.OutputBudgetFor(["P:big", "P:small"], _ => 1000000, m => m == "P:small" ? 8192 : 64000);
        Assert.That(budget, Is.EqualTo(8192), "備援換到上限較小的模型時不能被拒絕");
    }

    [Test]
    public void ReasoningFloorDoesNotExceedOutputBudget()
    {
        ColonyAgentSession session = new(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.For(ToolCategory.Observe), 8,
            contextWindowLookup: _ => 8000, outputLimitLookup: _ => 4096) { PreferredModel = "P:m" };
        Assert.That(session.CreateRunOptions(ReasoningLevel.High, "P:m").MaxOutputTokens, Is.EqualTo(1200));
    }

    [Test]
    public async Task HistoryPartsBecomeExactFromProviderUsage()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        var client = new ScriptedChatClient();
        client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).WithUsage(1000)
              .ReplyWithUsage("done", 1200);
        ColonyAgentSession session = new(client, world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work), 8);

        await session.SendAsync("set doctor");

        IReadOnlyList<ColonyAgentSession.ContextPart> parts = session.GetContextBreakdown();
        ColonyAgentSession.ContextPart calls = parts.Single(p => p.Key == "ToolCalls");
        ColonyAgentSession.ContextPart results = parts.Single(p => p.Key == "ToolResults");
        Assert.That(calls.Exact && results.Exact, Is.True, "送出的訊息是複本，內容物件要與歷史共用才對得上");
        Assert.That(calls.Tokens + results.Tokens, Is.EqualTo(200));
    }

    [Test]
    public async Task StoredReplyCarriesReportedOutputTokens()
    {
        var client = new ScriptedChatClient();
        client.Reply("all colonists are fine").WithUsage(1000, 25);
        ColonyAgentSession session = new(client, new FakeGameWorld(), () => ToolPermissions.For(ToolCategory.Observe), 8);

        await session.SendAsync("status?");

        ColonyAgentSession.ContextPart assistant = session.GetContextBreakdown().Single(p => p.Key == "AssistantMessages");
        Assert.That(assistant.Exact, Is.True, "回覆存進歷史的內容要與回應是同一個物件");
        Assert.That(assistant.Tokens, Is.EqualTo(25));
    }

    [Test]
    public async Task AutoCompactTriggersOnProviderReportedUsage([Values(true, false)] bool reported)
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        var client = new ScriptedChatClient();
        for (int i = 0; i < 4; i++)
        {
            client.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" });
            if (reported) client.WithUsage(70000);
            client.Reply("done");
            if (reported) client.WithUsage(70000);
        }
        client.Reply("hello back");
        ColonyAgentSession session = new(client, world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work), 8,
            contextWindowLookup: _ => 100000) { PreferredModel = "Test:model" };
        for (int i = 0; i < 4; i++) await session.SendAsync("set doctor");
        await session.SendAsync("hello");

        string forwarded = string.Join('\n', client.ReceivedMessages[^1].Select(m => m.Text ?? string.Empty));
        if (reported) Assert.That(forwarded, Does.Contain("[Tool Calls]"), "供應商回報已超過門檻，應觸發折疊");
        else Assert.That(forwarded, Does.Not.Contain("[Tool Calls]"), "字元估算遠低於門檻，不應折疊");
    }
}
