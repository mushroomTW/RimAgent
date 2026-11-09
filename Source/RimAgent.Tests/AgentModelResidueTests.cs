using NUnit.Framework;

namespace RimAgent.Tests;

/// <summary>框架換掉模型後，RimAgent 指定的舊模型要自動清回預設，不殘留。</summary>
[TestFixture]
public class AgentModelResidueTests
{
    [Test]
    public void ClearsModelMissingFromFramework()
    {
        var settings = new RimAgentSettings { agentModel = "OpenRouter:nvidia/nemotron-3-super-120b-a12b" };

        bool cleared = settings.ClearAgentModelIfMissing(["OpenRouter:nvidia/nemotron-3.5-super-vl:free", "OpenRouter:google/gemma-4-26b-a4b-it:free"]);

        Assert.That(cleared, Is.True);
        Assert.That(settings.agentModel, Is.Empty);
    }

    [Test]
    public void KeepsModelStillConfigured()
    {
        var settings = new RimAgentSettings { agentModel = " openrouter:google/gemma-4-26b-a4b-it:free " };

        bool cleared = settings.ClearAgentModelIfMissing(["OpenRouter:google/gemma-4-26b-a4b-it:free"]);

        Assert.That(cleared, Is.False);
        Assert.That(settings.agentModel, Is.EqualTo(" openrouter:google/gemma-4-26b-a4b-it:free "));
    }

    [Test]
    public void KeepsModelWhenFrameworkListUnavailable()
    {
        var settings = new RimAgentSettings { agentModel = "OpenRouter:x" };

        Assert.That(settings.ClearAgentModelIfMissing([]), Is.False);
        Assert.That(settings.ClearAgentModelIfMissing(null), Is.False);
        Assert.That(settings.agentModel, Is.EqualTo("OpenRouter:x"));
    }
}
