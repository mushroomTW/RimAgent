using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>上下文分析面板的估算：類別齊全、工具與對話都有算進去。</summary>
[TestFixture]
public class ContextBreakdownTests
{
    [Test]
    public void EstimateCountsCjkPerCharAndLatinPerFourChars()
    {
        Assert.That(ColonyAgentSession.EstimateTokens(null), Is.EqualTo(0));
        Assert.That(ColonyAgentSession.EstimateTokens("殖民地"), Is.EqualTo(3));
        Assert.That(ColonyAgentSession.EstimateTokens("abcdefgh"), Is.EqualTo(2));
    }

    [Test]
    public async Task BreakdownCoversToolsAndConversation()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        var client = new ScriptedChatClient();
        ToolPermissions permissions = ToolPermissions.For(ToolCategory.Observe);
        var session = new ColonyAgentSession(client, world, () => permissions, 8);
        client.Reply("殖民地一切正常。");
        await session.SendAsync("查看殖民地狀況");

        var parts = session.GetContextBreakdown().ToDictionary(p => p.Key);

        Assert.That(parts.Keys, Is.EquivalentTo(new[]
        {
            "SystemPrompt", "Tools", "ToolPacks", "Subagents", "Memory",
            "UserMessages", "AssistantMessages", "ToolCalls", "ToolResults"
        }));
        Assert.That(parts["Tools"].Tokens, Is.GreaterThan(0));
        Assert.That(parts["Tools"].Count, Is.GreaterThan(0));
        Assert.That(parts["UserMessages"].Tokens, Is.GreaterThan(0));
        Assert.That(parts["UserMessages"].Count, Is.EqualTo(1));
        Assert.That(parts["AssistantMessages"].Tokens, Is.GreaterThan(0));
    }
}
