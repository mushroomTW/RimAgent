using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>區域類別（zone issue 01）：查詢、新增、刪除、儲藏篩選。</summary>
[TestFixture]
public class ZoneToolsTests
{
    private FakeGameWorld _world;
    private ZoneTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Zones.Add(new ZoneInfo { Id = 1, Label = "Storage", Kind = "Stockpile", CellCount = 40 });
        _world.Zones.Add(new ZoneInfo { Id = 2, Label = "Rice field", Kind = "Growing", CellCount = 25 });
        _tools = new ZoneTools(_world);
    }

    [Test]
    public void ZonePermissionOffUntilGrantedButSettingsDefaultOn()
    {
        Assert.That(new ToolPermissions().Allows(ToolCategory.Zone), Is.False);
        Assert.That(new RimAgentSettings().ToPermissions().Allows(ToolCategory.Zone), Is.True, "設定頁預設全開");
    }

    [Test]
    public void RegistryGatesTheZoneToolsByPermission()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());

        Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Zone)).OfType<AIFunction>().Count(t => t.Name == "list_zones" || t.Name == "add_zone" || t.Name == "delete_zone" || t.Name == "set_zone_filter"), Is.EqualTo(4));
        Assert.That(registry.Select(new ToolPermissions()).OfType<AIFunction>().Any(t => t.Name == "list_zones"), Is.False);
    }

    [Test]
    public void ListsZonesWithKindAndCells()
    {
        string text = _tools.ListZones();

        Assert.That(text, Does.Contain("2 zones").And.Contain("[1] Storage").And.Contain("Stockpile").And.Contain("40 cells"));
        Assert.That(text, Does.Contain("[2] Rice field").And.Contain("Growing").And.Contain("25 cells"));
    }

    [Test]
    public void AddsStockpileOverRectangle()
    {
        string text = _tools.AddZone("stockpile", 10, 10, 12, 12, string.Empty);

        Assert.That(text, Is.EqualTo("Added stockpile 3 [3] covering 9 cells."));
        Assert.That(_world.Zones.Count, Is.EqualTo(3));
    }

    [Test]
    public void UnknownKindIsRejected()
    {
        Assert.That(_tools.AddZone("parking lot", 10, 10, 12, 12, string.Empty), Does.StartWith("Error:").And.Contain("stockpile or growing"));
        Assert.That(_world.Zones.Count, Is.EqualTo(2));
    }

    [Test]
    public void PlantOnStockpileIsRejected()
    {
        Assert.That(_tools.AddZone("stockpile", 10, 10, 12, 12, "rice"), Does.StartWith("Error:").And.Contain("growing zones"));
    }

    [TestCase("rice")]
    [TestCase("plant_rice")]
    [TestCase("Plant_Rice")]
    [TestCase("水稻")]
    public void GrowingPlantResolvesByNameOrDefName(string plant)
    {
        string text = _tools.AddZone("growing", 10, 10, 12, 12, plant);

        Assert.That(text, Does.StartWith("Added"));
        Assert.That(_world.Calls.Last(), Does.EndWith(",Plant_Rice)"), "世界層收到的是正確的 defName");
    }

    [Test]
    public void UnknownPlantIsRejectedBeforeTouchingTheWorld()
    {
        string text = _tools.AddZone("growing", 10, 10, 12, 12, "moon lettuce");

        Assert.That(text, Does.StartWith("Error:").And.Contain("growable plant"));
        Assert.That(_world.Calls, Has.None.StartsWith("AddZone"));
    }

    [Test]
    public void OutOfBoundsRectangleIsRejected()
    {
        Assert.That(_tools.AddZone("stockpile", 0, 0, 200, 200, string.Empty), Does.StartWith("Error:").And.Contain("outside the map"));
        Assert.That(_world.Zones.Count, Is.EqualTo(2));
    }

    [Test]
    public void DeletesZoneByLabel()
    {
        string text = _tools.DeleteZone("Rice field");

        Assert.That(text, Does.Contain("Deleted zone [2]"));
        Assert.That(_world.Zones.Select(z => z.Id), Is.EqualTo([1]));
    }

    [Test]
    public void UnknownZoneIsRejected()
    {
        Assert.That(_tools.DeleteZone("moon base"), Does.StartWith("Error:"));
    }

    [Test]
    public void SetsFilterAllowAndDisallow()
    {
        string text = _tools.SetZoneFilter("1", "corn", "steel");

        Assert.That(text, Is.EqualTo("Zone [1] Storage now allows corn; disallows steel."));
        Assert.That(_world.ZoneDisallowed[1], Does.Contain("Steel"));
    }

    [Test]
    public void FilterOnGrowingZoneIsRefused()
    {
        Assert.That(_tools.SetZoneFilter("2", "corn", string.Empty), Does.StartWith("Error:").And.Contain("no storage filter"));
    }

    [Test]
    public void EmptyFilterIsRejected()
    {
        Assert.That(_tools.SetZoneFilter("1", string.Empty, "  "), Does.StartWith("Error:").And.Contain("at least one item"));
    }

    [Test]
    public void UnknownItemIsRejected()
    {
        Assert.That(_tools.SetZoneFilter("1", "moon rock", string.Empty), Does.StartWith("Error:"));
    }

    [Test]
    public async Task AddZoneIsLoggedAsAToolCall()
    {
        var client = new ScriptedChatClient();
        client.CallTool("add_zone", new { kind = "growing", x1 = 20, z1 = 20, x2 = 24, z2 = 24, plant = "rice" }).Reply("Done.");
        var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Zone), 8);

        await session.SendAsync("在東邊開一塊 5x5 稻田");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.ToolName, Is.EqualTo("add_zone"));
        Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Zone));
        Assert.That(tool.Result, Does.Contain("covering 25 cells"));
    }
}
