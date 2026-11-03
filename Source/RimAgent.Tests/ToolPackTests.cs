using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>工具包：低頻工具不常駐，load_toolpack 展開後同一輪下一步就能用，整段對話保留，清除對話後重置。</summary>
[TestFixture]
public class ToolPackTests
{
    private FakeGameWorld _world;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
    }

    private static List<string> ToolNames(ChatOptions options)
    {
        return [.. options.Tools.OfType<AIFunction>().Select(t => t.Name)];
    }

    private ColonyAgentSession NewSession(ScriptedChatClient client, ToolPermissions permissions, AgentMode mode = AgentMode.Autonomous)
    {
        return new ColonyAgentSession(client, _world, () => permissions, 8, () => mode, instructions: () => "Base instructions");
    }

    [Test]
    public async Task PackedToolsAreHiddenBehindTheLoaderAndListedInTheIndex()
    {
        var client = new ScriptedChatClient().Reply("ok");
        await NewSession(client, ToolPermissions.All).SendAsync("hi");

        List<string> names = ToolNames(client.ReceivedOptions[0]);
        Assert.That(names, Does.Contain("get_colony_overview").And.Contain("place_blueprint").And.Contain("file_memory_read"));
        Assert.That(names, Does.Contain(ToolPackTools.ToolName));
        Assert.That(names, Does.Not.Contain("draft").And.Not.Contain("execute_trade").And.Not.Contain("file_memory_write").And.Not.Contain("mcp_call_tool"));
        Assert.That(client.ReceivedOptions[0].Instructions, Does.Contain("- combat:").And.Contain("- memory_edit:"));
    }

    [Test]
    public async Task LoadedPackIsUsableInTheSameRunAndStaysForLaterInstructions()
    {
        var client = new ScriptedChatClient()
            .CallTool(ToolPackTools.ToolName, new { pack = "combat" })
            .CallTool("draft", new { colonists = "Ali", drafted = true })
            .Reply("Drafted.")
            .Reply("Still here.");
        ColonyAgentSession session = NewSession(client, ToolPermissions.All);

        await session.SendAsync("draft Ali");
        await session.SendAsync("anything else?");

        Assert.That(ToolNames(client.ReceivedOptions[0]), Does.Not.Contain("draft"));
        Assert.That(ToolNames(client.ReceivedOptions[1]), Does.Contain("draft").And.Contain("order_attack"), "同一輪下一步就看得到");
        TranscriptEntry draft = session.Transcript.Snapshot().Single(e => e.ToolName == "draft");
        Assert.That(draft.Failed, Is.False, draft.Result);
        Assert.That(ToolNames(client.ReceivedOptions[3]), Does.Contain("draft"), "整段對話保留");
    }

    [Test]
    public async Task LoadingAPackDoesNotSpendTheAutonomousToolBudget()
    {
        var client = new ScriptedChatClient()
            .CallTool(ToolPackTools.ToolName, new { pack = "combat" })
            .CallTool("draft", new { colonists = "Ali", drafted = true })
            .Reply("Drafted.");
        ColonyAgentSession session = NewSession(client, ToolPermissions.All);

        await session.SendAsync("draft Ali", autonomous: true, maxToolSteps: 3);

        TranscriptEntry load = session.Transcript.Snapshot().Single(e => e.ToolName == ToolPackTools.ToolName);
        TranscriptEntry draft = session.Transcript.Snapshot().Single(e => e.ToolName == "draft");
        Assert.That(load.BudgetConsumed, Is.False);
        Assert.That(draft.BudgetConsumed, Is.True, draft.Result);
    }

    [Test]
    public async Task ClearingTheConversationUnloadsPacks()
    {
        var client = new ScriptedChatClient()
            .CallTool(ToolPackTools.ToolName, new { pack = "trade" })
            .Reply("Loaded.")
            .Reply("Fresh.");
        ColonyAgentSession session = NewSession(client, ToolPermissions.All);

        await session.SendAsync("trade");
        session.Clear();
        await session.SendAsync("hi");

        Assert.That(ToolNames(client.ReceivedOptions[2]), Does.Not.Contain("execute_trade"));
    }

    [Test]
    public async Task PacksFollowPermissions()
    {
        var client = new ScriptedChatClient()
            .CallTool(ToolPackTools.ToolName, new { pack = "medical" })
            .Reply("ok");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Medical), AgentMode.Confirm);

        await session.SendAsync("how is Ali's health?");

        string instructions = client.ReceivedOptions[0].Instructions;
        Assert.That(instructions, Does.Contain("- medical:"), "允許醫療類別時列出醫療包");
        Assert.That(instructions, Does.Not.Contain("- combat:"), "未允許戰鬥類別時不列出戰鬥包");
        Assert.That(ToolNames(client.ReceivedOptions[1]), Does.Contain("get_health").And.Not.Contain("order_attack"));
    }

    [Test]
    public async Task UnavailablePackIsRejected()
    {
        var client = new ScriptedChatClient()
            .CallTool(ToolPackTools.ToolName, new { pack = "trade" })
            .Reply("ok");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Combat));

        await session.SendAsync("trade");

        TranscriptEntry load = session.Transcript.Snapshot().Single(e => e.ToolName == ToolPackTools.ToolName);
        Assert.That(load.Failed, Is.True);
        Assert.That(load.Result, Does.Contain("combat"), "列出可用的包");
        Assert.That(ToolNames(client.ReceivedOptions[1]), Does.Not.Contain("execute_trade"));
    }

    [Test]
    public async Task NoLoaderOrIndexWhenNothingIsPacked()
    {
        var client = new ScriptedChatClient().Reply("ok");
        await NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work)).SendAsync("hi");

        Assert.That(ToolNames(client.ReceivedOptions[0]), Does.Not.Contain(ToolPackTools.ToolName));
        Assert.That(client.ReceivedOptions[0].Instructions, Does.Not.Contain("Tool packs"));
    }

    [Test]
    public void SubagentsStillGetTheirWholeCategories()
    {
        var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());

        IEnumerable<string> names = registry.SelectForSubagent(ToolPermissions.All,
            new[] { ToolCategory.Combat }, readOnly: false).Select(t => t.Name);

        Assert.That(names, Does.Contain("draft"));
    }
}
