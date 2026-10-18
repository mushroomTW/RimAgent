using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>生產類別（bill issue 01）：工作台與帳單增刪。</summary>
[TestFixture]
public class ProductionToolsTests
{
    private FakeGameWorld _world;
    private ProductionTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.WorkTables.Add(new WorkTableInfo { Id = 501, Label = "Fueled smithy", X = 30, Z = 40 });
        _world.WorkTables.Add(new WorkTableInfo { Id = 502, Label = "Butcher table", X = 32, Z = 41, BillCount = 1 });
        _world.TableBills[502] =
        [
            new() { Index = 1, Label = "Butcher creature", Repeat = "2x" }
        ];
        _tools = new ProductionTools(_world);
    }

    [Test]
    public void ProductionPermissionOffUntilGrantedButSettingsDefaultOn()
    {
        Assert.That(new ToolPermissions().Allows(ToolCategory.Production), Is.False);
        Assert.That(new RimAgentSettings().ToPermissions().Allows(ToolCategory.Production), Is.True, "設定頁預設全開");
    }

    [Test]
    public void RegistryGatesTheProductionToolsByPermission()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());

        Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Production)).OfType<AIFunction>().Count(t => t.Name == "list_bills" || t.Name == "add_bill" || t.Name == "delete_bill"), Is.EqualTo(3));
        Assert.That(registry.Select(new ToolPermissions()).OfType<AIFunction>().Any(t => t.Name == "list_bills"), Is.False);
    }

    [Test]
    public void ListsEveryWorktableWithItsBillsWhenNoTableIsGiven()
    {
        string text = _tools.ListBills("");

        Assert.That(text, Does.Contain("2 work tables").And.Contain("[501] Fueled smithy at (30,40), no bills"));
        Assert.That(text, Does.Contain("[502] Butcher table").And.Contain("  [1] Butcher creature (2x)"));
    }

    [Test]
    public void ListsBillsWithRepeatCounts()
    {
        Assert.That(_tools.ListBills("502"), Does.Contain("Bills on Butcher table:").And.Contain("[1] Butcher creature (2x)"));
        Assert.That(_tools.ListBills("501"), Is.EqualTo("Fueled smithy has no bills."));
    }

    [Test]
    public void AddsBillWithCount()
    {
        string text = _tools.AddBill("501", "component", 5);

        Assert.That(text, Is.EqualTo("Added bill [1] Make component x5 on Fueled smithy."));
        Assert.That(_world.WorkTables[0].BillCount, Is.EqualTo(1));
    }

    [Test]
    public void RecipeIsResolvedAmongTheTablesOwnRecipes()
    {
        // 模組包常讓不同工作台各有一個產出同物品的配方；只看這張台能做的，不因別台的同名配方歧義。
        _world.TableRecipes[501] = new[] { new DefInfo("MakeComponent", "component") };
        _world.TableRecipes[502] = new[] { new DefInfo("ModX_MakeComponent", "component") };

        string text = _tools.AddBill("502", "component", 2);

        Assert.That(text, Does.StartWith("Added bill"));
        Assert.That(_world.Calls, Does.Contain("AddBill(502,ModX_MakeComponent,2)"));
    }

    [Test]
    public void RecipeTheTableCannotMakeIsRejectedWithItsName()
    {
        _world.TableRecipes[502] = new[] { new DefInfo("ButcherCreature", "butcher creature") };

        Assert.That(_tools.AddBill("502", "component", 1), Does.StartWith("Error:").And.Contain("Butcher table"));
    }

    [Test]
    public void UnknownTableRecipeAndCountAreRejected()
    {
        Assert.That(_tools.AddBill("999", "component", 1), Does.StartWith("Error:"));
        Assert.That(_tools.ListBills("999"), Does.StartWith("Error:"));
        Assert.That(_tools.AddBill("501", "warp drive", 1), Does.StartWith("Error:"));
        Assert.That(_tools.AddBill("501", "component", 0), Does.StartWith("Error:").And.Contain("at least 1"));
        Assert.That(_world.TableBills.ContainsKey(501), Is.False);
    }

    [Test]
    public void DeleteRenumbersRemainingBills()
    {
        _tools.AddBill("501", "component", 5);
        _tools.AddBill("501", "short bow", 2);

        string text = _tools.DeleteBill("501", 1);

        Assert.That(text, Does.Contain("Deleted bill Make component"));
        Assert.That(_world.TableBills[501].Select(b => b.Index).ToArray(), Is.EqualTo([1]));
        Assert.That(_world.TableBills[501][0].Label, Is.EqualTo("Make short bow"));
    }

    [Test]
    public void UnknownBillNumberIsRejected()
    {
        Assert.That(_tools.DeleteBill("502", 7), Does.StartWith("Error:").And.Contain("does not exist"));
        Assert.That(_world.TableBills[502].Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AddBillIsLoggedAsAToolCall()
    {
        var client = new ScriptedChatClient();
        client.CallTool("add_bill", new { table = "501", recipe = "component", count = 5 }).Reply("Done.");
        var session = new ColonyAgentSession(client, _world, () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Production), 8);

        await session.SendAsync("做 5 個零件");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.ToolName, Is.EqualTo("add_bill"));
        Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Production));
        Assert.That(tool.Result, Does.Contain("Make component").And.Contain("Fueled smithy"));
    }
}
