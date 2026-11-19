using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>囚犯類別（prisoner issue 01）：查詢、互動模式切換、立即釋放。</summary>
[TestFixture]
public class PrisonerToolsTests
{
    private FakeGameWorld _world;
    private PrisonerTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Prisoners.Add(new PrisonerInfo
        {
            Id = 201, Name = "Rook", Nickname = "Rook", FullName = "Ren 'Rook' Cole",
            Resistance = 12.5f, Recruitable = false,
            InteractionMode = "ReduceResistance", InteractionModeLabel = "reduce resistance",
            HealthPercent = 0.8f
        });
        _world.Prisoners.Add(new PrisonerInfo
        {
            Id = 202, Name = "Vex", Nickname = string.Empty, FullName = "Vex Marlowe",
            Resistance = -1f, Recruitable = true,
            InteractionMode = "AttemptRecruit", InteractionModeLabel = "recruit",
            HealthPercent = 1f, Downed = true
        });
        _tools = new PrisonerTools(_world);
    }

    [Test]
    public void PrisonerPermissionOffUntilGrantedButSettingsDefaultOn()
    {
        Assert.That(new ToolPermissions().Allows(ToolCategory.Prisoner), Is.False);
        Assert.That(new RimAgentSettings().ToPermissions().Allows(ToolCategory.Prisoner), Is.True, "設定頁預設全開");
    }

    [Test]
    public void RegistryGatesThePrisonerToolsByPermission()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());

        Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Prisoner)).OfType<AIFunction>().Count(t => t.Name == "list_prisoners" || t.Name == "set_prisoner_interaction" || t.Name == "release_prisoner"), Is.EqualTo(3));
        Assert.That(registry.Select(new ToolPermissions()).OfType<AIFunction>().Any(t => t.Name == "list_prisoners" || t.Name == "set_prisoner_interaction" || t.Name == "release_prisoner"), Is.False);
    }

    [Test]
    public void ListsPrisonersWithResistanceAndMode()
    {
        string text = _tools.ListPrisoners();

        Assert.That(text, Does.Contain("2 prisoners").And.Contain("Rook").And.Contain("12.5").And.Contain("reduce resistance"));
        Assert.That(text, Does.Contain("Vex").And.Contain("none").And.Contain("recruitable").And.Contain("DOWNED"));
    }

    [Test]
    public void EmptyPrisonSaysSoExplicitly()
    {
        _world.Prisoners.Clear();

        Assert.That(_tools.ListPrisoners(), Does.Contain("no prisoners").IgnoreCase);
    }

    [Test]
    public void SetsInteractionAndReportsOldAndNew()
    {
        string text = _tools.SetPrisonerInteraction("Rook", "recruit");

        Assert.That(text, Is.EqualTo("Set Rook's interaction reduce resistance -> recruit."));
        Assert.That(_world.Prisoners[0].InteractionMode, Is.EqualTo("AttemptRecruit"));
    }

    [Test]
    public void UnknownModeListsNoFabrication()
    {
        string text = _tools.SetPrisonerInteraction("Rook", "befriend forever");

        Assert.That(text, Does.StartWith("Error:"));
        Assert.That(_world.Prisoners[0].InteractionMode, Is.EqualTo("ReduceResistance"));
    }

    [Test]
    public void UnknownPrisonerReportsError()
    {
        Assert.That(_tools.SetPrisonerInteraction("Nobody", "recruit"), Does.StartWith("Error:"));
        Assert.That(_tools.ReleasePrisoner("Nobody"), Does.StartWith("Error:"));
    }

    [Test]
    public void ReleaseRemovesThePrisoner()
    {
        string text = _tools.ReleasePrisoner("Vex");

        Assert.That(text, Is.EqualTo("Vex was released and left the colony."));
        Assert.That(_world.Prisoners.Select(p => p.Id), Is.EqualTo([201]));
    }

    [Test]
    public async Task SetInteractionIsRejectedWithoutClassifierInAuto()
    {
        var client = new ScriptedChatClient();
        var classifier = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
        client.CallTool("set_prisoner_interaction", new { prisoner = "Rook", mode = "recruit" }).Reply("Done.");
        var session = new ColonyAgentSession(
            client,
            _world,
            () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Prisoner),
            8,
            () => AgentMode.Auto,
            classifierClient: classifier,
            classifierPrompt: () => "test classifier prompt");

        await session.SendAsync("開始招募 Rook");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.ToolName, Is.EqualTo("set_prisoner_interaction"));
        Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Prisoner));
        Assert.That(tool.RiskCheck, Is.EqualTo("high"));
        Assert.That(tool.Result, Does.StartWith(ToolText.ErrorPrefix).And.Contain("blacklist"));
        Assert.That(classifier.CallCount, Is.EqualTo(0), "黑名單不燒分類器模型");
        Assert.That(session.HasPendingConfirmation, Is.False);
    }
}
