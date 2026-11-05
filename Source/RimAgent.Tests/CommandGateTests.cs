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

namespace RimAgent.Tests;

/// <summary>
/// 閘門不變量：每個命令工具（Risk != Query）被擋下時只回傳預覽、不碰世界；放行時真的動世界。
/// 自動模式的兩段式執行（先預覽再執行）靠這個不變量才安全，所以每個命令工具都要在這裡登記一組有效參數。
/// </summary>
[TestFixture]
public class CommandGateTests
{
    private FakeGameWorld _world;

    /// <summary>每個命令工具的一組有效參數；新增命令工具而沒登記，測試會指出。</summary>
    private static readonly Dictionary<string, Dictionary<string, object>> ValidArguments = new()
    {
        ["set_work_priorities"] = Args(("assignments", "Ali:doctor=1; Bob:hauling=2")),
        ["set_schedule"] = Args(("colonists", "Ali"), ("startHour", 22), ("endHour", 6), ("assignment", "sleep")),
        ["designate_area"] = Args(("kind", "mine"), ("useSelection", false), ("x1", 0), ("z1", 0), ("x2", 30), ("z2", 30), ("filter", "")),
        ["cancel_designation"] = Args(("kind", "mine"), ("useSelection", false), ("x1", 0), ("z1", 0), ("x2", 30), ("z2", 30)),
        ["remove_blueprint"] = Args(("x1", 5), ("z1", 5), ("x2", 5), ("z2", 5)),
        ["place_blueprint"] = Args(("building", "wall"), ("shape", "single"), ("x", 5), ("z", 5), ("x2", 5), ("z2", 5), ("rotation", 0), ("stuff", "wood")),
        ["draft"] = Args(("colonists", "Ali"), ("drafted", true)),
        ["order_move"] = Args(("colonists", "Ali"), ("x", 12), ("z", 12)),
        ["order_attack"] = Args(("colonists", "Ali"), ("targetId", 900)),
        ["order_job"] = Args(("colonist", "Ali"), ("job", "capture"), ("targetId", 900)),
        ["set_forbidden"] = Args(("forbidden", false), ("x1", -1), ("z1", -1), ("x2", -1), ("z2", -1), ("filter", "")),
        ["choose_option"] = Args(("choiceId", "letter-12"), ("option", 1)),
        ["use_gizmo"] = Args(("thingId", 300), ("gizmo", "Launch ship")),
        ["use_right_click_option"] = Args(("colonist", "Ali"), ("option", "1"), ("targetId", 302), ("targetX", -1), ("targetZ", -1)),
        ["accept_quest"] = Args(("questId", 40), ("reward", 0), ("accepter", "")),
        ["dismiss_quest"] = Args(("questId", 40), ("dismissed", true)),
        ["execute_trade"] = Args(("trader", "PassingShip_1"), ("buys", "spacer component x1"), ("sells", ""), ("negotiator", "Ali")),
        ["form_caravan"] = Args(("colonists", "Bob"), ("animals", ""), ("cargo", ""), ("prisoners", ""), ("tile", 4600), ("worldObjectId", -1)),
        ["cancel_forming_caravan"] = Args(("caravan", "Bob")),
        ["send_caravan"] = Args(("caravan", "Trailblazers"), ("option", "1) Travel to tile 4600"), ("tile", 4600), ("worldObjectId", -1)),
        ["set_caravan_movement"] = Args(("caravan", "Trailblazers"), ("action", "pause")),
        ["settle_caravan"] = Args(("caravan", "Trailblazers"), ("action", "settle")),
        ["set_medical_care"] = Args(("colonist", "Ali"), ("level", "best")),
        ["schedule_surgery"] = Args(("colonist", "Ali"), ("surgery", "remove body part"), ("part", "left leg")),
        ["set_prisoner_interaction"] = Args(("prisoner", "Zed"), ("mode", "recruit")),
        ["release_prisoner"] = Args(("prisoner", "Zed")),
        ["tame_animal"] = Args(("animal", "700")),
        ["train_animal"] = Args(("animal", "Rex"), ("training", "obedience"), ("enable", true)),
        ["set_animal_area"] = Args(("animal", "Rex"), ("area", "Barn")),
        ["add_zone"] = Args(("kind", "stockpile"), ("x1", 40), ("z1", 40), ("x2", 42), ("z2", 42), ("plant", "")),
        ["delete_zone"] = Args(("zone", "50")),
        ["set_zone_filter"] = Args(("zone", "50"), ("allow", "corn"), ("disallow", "")),
        ["add_bill"] = Args(("table", "800"), ("recipe", "component"), ("count", 2)),
        ["delete_bill"] = Args(("table", "800"), ("bill", 1)),
        ["set_research"] = Args(("project", "batteries")),
        ["set_time_speed"] = Args(("speed", "Fast")),
        ["wait_for_time"] = Args(("hours", 2)),
        ["save_game"] = Args(),
        ["load_save"] = Args(("save", "Autosave-1"), ("lesson", "build walls before the raid")),
        ["set_current_map"] = Args(("map", "2")),
        ["take_screenshot"] = Args(("name", "test_shot")),
        ["mcp_call_tool"] = Args(("server", "fetch"), ("tool", "fetch"), ("args", "{}")),
        ["file_memory_write"] = Args(("fileName", "base.md"), ("content", "Near river"), ("description", "Base notes")),
        ["file_memory_delete"] = Args(("fileName", "base.md")),
        ["file_memory_replace"] = Args(("fileName", "base.md"), ("oldString", "river"), ("newString", "lake"), ("replaceAll", false)),
        ["file_memory_replace_lines"] = Args(("fileName", "base.md"), ("edits", "[]")),
        ["web_search"] = Args(("query", "RimWorld wiki")),
    };

    /// <summary>不經 IGameWorld 的命令（放行時不會留下 Calls）。</summary>
    private static readonly HashSet<string> NotRecordedInWorld =
    [
        "mcp_call_tool", "file_memory_write", "file_memory_delete", "file_memory_replace", "file_memory_replace_lines", "web_search"
    ];

    [SetUp]
    public void SetUp()
    {
        _world = NewWorld();
    }

    private static FakeGameWorld NewWorld()
    {
        var _world = new FakeGameWorld();
        _world.RemovableBlueprints.Add("wall (5,5)-(5,5)");
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
        _world.Things.Add(FakeGameWorld.MakeThing(300, "MineableSteel", "compacted steel", ThingKind.Mineable, 10, 10));
        _world.Things.Add(FakeGameWorld.MakeThing(301, "MineableSteel", "compacted steel", ThingKind.Mineable, 11, 10));
        _world.Things[1].Designations = new[] { "Mine" };
        _world.Threats.Add(new ThreatInfo { Id = 900, Label = "grizzly bear", Kind = "Manhunter", X = 20, Z = 20 });
        _world.Traders.Add(new TraderInfo
        {
            Kind = TraderInfo.OrbitalKind, Id = "PassingShip_1", Name = "Starweaver", Faction = "Outlanders", Silver = 1200,
            Goods = new[] { new TradeGoodsInfo { DefName = "ComponentSpacer", Label = "spacer component", Count = 8, MarketValue = 55 } }
        });
        _world.Resources = new ResourceSnapshot { Items = new[] { new ResourceInfo { DefName = "Silver", Label = "silver", Count = 500 } } };
        _world.Prisoners.Add(new PrisonerInfo { Id = 600, Name = "Zed", FullName = "Zed Kane", InteractionMode = "MaintainOnly", InteractionModeLabel = "maintain only" });
        _world.Animals.Add(new AnimalInfo { Id = 700, Name = "boomalope", Species = "boomalope", SpeciesDefName = "Boomalope", Wild = true });
        _world.Animals.Add(new AnimalInfo { Id = 701, Name = "Rex", Species = "husky", SpeciesDefName = "Husky", Wild = false });
        _world.Zones.Add(new ZoneInfo { Id = 50, Label = "Stockpile 1", Kind = "Stockpile", CellCount = 9 });
        _world.WorkTables.Add(new WorkTableInfo { Id = 800, Label = "machining table", X = 3, Z = 3, BillCount = 1 });
        _world.TableBills[800] = [new() { Index = 1, Label = "Make component", Repeat = "2x" }];
        _world.Research = new ResearchInfo { Available = new[] { new ResearchProjectInfo { DefName = "Batteries", Label = "batteries", Cost = 400 } } };
        _world.Maps.Add(new MapInfo { Id = 2, Label = "Outpost", Tile = 999, ColonistCount = 0 });
        // 讀檔次數記在行程共用的紀錄裡：每個世界的存檔用不同寫入時間，算成不同的存檔。
        _world.SaveFiles.Add(new SaveSlotInfo { Name = "Autosave-1", ColonyId = "colony1", Tick = 1000, WrittenUtcTicks = DateTime.UtcNow.Ticks });
        _world.Things.Add(FakeGameWorld.MakeThing(302, "Gun_Revolver", "revolver", ThingKind.Item, 12, 12));
        _world.Things[2].Forbidden = true;
        _world.Gizmos[300] = [new() { Number = 1, Label = "Launch ship" }];
        _world.PendingChoices.Add(new PendingChoiceInfo
        {
            Id = "letter-12",
            Title = "Wanderer joins",
            Options = new[] { new ChoiceOptionInfo { Number = 1, Text = "Accept" } }
        });
        _world.FloatMenuOptions.Add(new FloatMenuOptionInfo { Number = 1, Label = "Equip revolver" });
        _world.Quests.Add(new QuestInfo { Id = 40, Name = "Hospitality request", State = QuestInfo.AvailableState });
        _world.Caravans.Add(new CaravanInfo
        {
            Id = 12, Name = "Trailblazers", Tile = 4500, Moving = true, DestinationTile = 4700, CanSettle = true, CanCamp = true,
            Members = new[] { new CaravanMemberInfo { Id = 101, Name = "Ali" } }
        });
        _world.CaravanOrders.Add(new FloatMenuOptionInfo { Number = 1, Label = "Travel to tile 4600" });
        _world.Forming.Add(new FormingCaravanInfo
        {
            Id = 77, Status = "gathering items", Members = new[] { new CaravanMemberInfo { Id = 102, Name = "Bob" } }
        });
        return _world;
    }

    [Test]
    public void EveryCommandToolIsRegisteredHere()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());
        List<string> commands = [.. registry.All.OfType<AIFunction>().Select(t => t.Name).Where(n => registry.RiskOf(n) != ToolRisk.Query)];

        Assert.That(commands, Is.EquivalentTo(ValidArguments.Keys), "每個命令工具都要在 ValidArguments 登記一組有效參數");
    }

    [Test]
    public async Task BlockedCommandToolsPreviewWithoutTouchingWorld()
    {
        // 確認模式的閘門擋下所有命令，正好用來驗證每個命令工具都有預覽分支。
        var registry = new AgentToolRegistry(_world, new AgentTranscript(), null, new ConfirmationGate(() => AgentMode.Confirm));

        foreach (AIFunction tool in registry.All.OfType<AIFunction>())
        {
            if (registry.RiskOf(tool.Name) == ToolRisk.Query) continue;
            _world.Calls.Clear();

            string text = (await tool.InvokeAsync(new AIFunctionArguments(ValidArguments[tool.Name])))?.ToString();

            Assert.That(text, Does.StartWith(ToolText.ConfirmationPrefix + "would "), tool.Name);
            Assert.That(_world.Calls, Is.Empty, tool.Name + " 被擋下時不可碰世界");
        }
    }

    [Test]
    public async Task AllowedCommandToolsReachTheWorld()
    {
        // 命令會改世界（刪區域、切地圖），每個工具用一個乾淨的世界跑，彼此不影響。
        foreach (string name in ValidArguments.Keys)
        {
            if (NotRecordedInWorld.Contains(name)) continue;
            FakeGameWorld world = NewWorld();
            var registry = new AgentToolRegistry(world, new AgentTranscript(), null, new ConfirmationGate(() => AgentMode.Autonomous));
            AIFunction tool = registry.All.OfType<AIFunction>().Single(t => t.Name == name);

            string text = (await tool.InvokeAsync(new AIFunctionArguments(ValidArguments[name])))?.ToString();

            Assert.That(text, Does.Not.StartWith(ToolText.ErrorPrefix).And.Not.StartWith(ToolText.ConfirmationPrefix), name + ": " + text);
            Assert.That(world.Calls, Is.Not.Empty, name + " 放行時必須動到世界");
        }
    }

    [Test]
    public async Task ConfirmModeBlocksAllCommands()
    {
        foreach (string name in ValidArguments.Keys)
        {
            FakeGameWorld world = NewWorld();
            var registry = new AgentToolRegistry(world, new AgentTranscript(), null, new ConfirmationGate(() => AgentMode.Confirm));
            AIFunction tool = registry.All.OfType<AIFunction>().Single(t => t.Name == name);

            string text = (await tool.InvokeAsync(new AIFunctionArguments(ValidArguments[name])))?.ToString();

            Assert.That(text, Does.StartWith(ToolText.ConfirmationPrefix), name);
            Assert.That(world.Calls, Is.Empty, name);
        }
    }

    [Test]
    public async Task MissingModeFallsBackToConfirm()
    {
        var gate = new ConfirmationGate(null);
        Assert.That(gate.Mode, Is.EqualTo(AgentMode.Confirm));

        FakeGameWorld world = NewWorld();
        var registry = new AgentToolRegistry(world, new AgentTranscript(), null, gate);
        AIFunction tool = registry.All.OfType<AIFunction>().Single(t => t.Name == "set_work_priorities");

        string text = (await tool.InvokeAsync(new AIFunctionArguments(ValidArguments["set_work_priorities"])))?.ToString();

        Assert.That(text, Does.StartWith(ToolText.ConfirmationPrefix));
        Assert.That(world.Calls, Is.Empty);
    }

    [TestCase(AgentMode.Autonomous, "mcp_call_tool", false)]
    [TestCase(AgentMode.Confirm, "mcp_call_tool", true)]
    [TestCase(AgentMode.Auto, "mcp_call_tool", false)]
    [TestCase(AgentMode.Confirm, "set_work_priorities", false)]
    public void RequiresExplicitConfirmationTests(AgentMode mode, string toolName, bool expected)
    {
        var gate = new ConfirmationGate(() => mode);
        Assert.That(gate.RequiresExplicitConfirmation(toolName), Is.EqualTo(expected));
    }

    [Test]
    public async Task FailedApprovalKeepsLaterCommandsPending()
    {
        var gate = new ConfirmationGate(() => AgentMode.Confirm);
        int laterCalls = 0;
        gate.RegisterPendingApproval("first", _ => Task.FromResult<object>(null));
        gate.RegisterPendingApproval("failed", _ => throw new InvalidOperationException("failed"));
        gate.RegisterPendingApproval("later", _ => { laterCalls++; return Task.FromResult<object>(null); });

        Assert.ThrowsAsync<InvalidOperationException>(async () => await gate.ApprovePendingAsync(CancellationToken.None));
        Assert.That(gate.HasPendingApprovals, Is.True);
        Assert.That(await gate.ApprovePendingAsync(CancellationToken.None), Is.EqualTo(1));
        Assert.That(laterCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task ErrorResultDoesNotRememberApprovalOrConsumeLaterCommands()
    {
        var gate = new ConfirmationGate(() => AgentMode.Confirm);
        gate.RegisterPendingApproval("set_schedule", _ => Task.FromResult<object>("Error: target disappeared"));
        gate.RegisterPendingApproval("set_work_priorities", _ => Task.FromResult<object>("ok"));

        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await gate.ApprovePendingAsync(CancellationToken.None, rememberForSession: true));
        Assert.That(error.Message, Does.Contain("target disappeared"));
        Assert.That(gate.IsApprovedForSession("set_schedule"), Is.False);
        Assert.That(gate.HasPendingApprovals, Is.True);
    }

    [Test]
    public async Task ParallelInvocationsKeepTheirOwnConfirmationState()
    {
        var gate = new ConfirmationGate(() => AgentMode.Confirm);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = Task.Run(async () =>
        {
            gate.BeginInvocation("command", ToolRisk.Confirm);
            entered.SetResult(true);
            await release.Task;
            bool allowed = gate.AllowCommands();
            gate.EndInvocation();
            return allowed;
        });
        await entered.Task;
        gate.BeginInvocation("query", ToolRisk.Query);
        Assert.That(gate.AllowCommands(), Is.True);
        release.SetResult(true);
        Assert.That(await first, Is.False);
        gate.EndInvocation();
    }

    private static Dictionary<string, object> Args(params (string Name, object Value)[] pairs)
    {
        return pairs.ToDictionary(p => p.Name, p => p.Value);
    }
}
