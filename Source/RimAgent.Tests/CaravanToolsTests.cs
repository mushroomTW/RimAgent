using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>交易類別：規劃商隊、照原版組隊視窗送出（集結）、中止集結。</summary>
[TestFixture]
public class CaravanToolsTests
{
    private FakeGameWorld _world;
    private CaravanTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
        _world.Animals.Add(new AnimalInfo { Id = 700, Name = "boomalope", Species = "boomalope", SpeciesDefName = "Boomalope", Wild = true });
        _world.Animals.Add(new AnimalInfo { Id = 701, Name = "Rex", Species = "husky", SpeciesDefName = "Husky" });
        _world.Animals.Add(new AnimalInfo { Id = 702, Name = "muffalo", Species = "muffalo", SpeciesDefName = "Muffalo", Downed = true });
        _world.Animals.Add(new AnimalInfo { Id = 703, Name = "muffalo", Species = "muffalo", SpeciesDefName = "Muffalo" });
        _world.Animals.Add(new AnimalInfo { Id = 704, Name = "muffalo", Species = "muffalo", SpeciesDefName = "Muffalo" });
        _world.Prisoners.Add(new PrisonerInfo { Id = 600, Name = "Zed", FullName = "Zed Kane" });
        _tools = new CaravanTools(_world);
    }

    [Test]
    public void RegistryGatesTheCaravanToolsByPermission()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());
        string[] names = ["plan_caravan", "form_caravan", "cancel_forming_caravan"];

        var allowed = registry.Select(ToolPermissions.For(ToolCategory.Trade)).OfType<AIFunction>().Select(t => t.Name).ToList();
        var denied = registry.Select(new ToolPermissions()).OfType<AIFunction>().Select(t => t.Name).ToList();

        foreach (string name in names)
        {
            Assert.That(allowed, Does.Contain(name));
            Assert.That(denied, Does.Not.Contain(name));
        }
        Assert.That(registry.RiskOf("plan_caravan"), Is.EqualTo(ToolRisk.Query));
        Assert.That(registry.RiskOf("form_caravan"), Is.EqualTo(ToolRisk.Confirm));
        Assert.That(registry.RiskOf("cancel_forming_caravan"), Is.EqualTo(ToolRisk.Safe));
        Assert.That(AgentToolRegistry.IsYoloForbidden("form_caravan"), Is.True);
        Assert.That(AgentToolRegistry.IsYoloForbidden("plan_caravan"), Is.False);
    }

    // ---- plan_caravan ----

    [Test]
    public void PlanShowsEveryNumberTheFormingWindowShows()
    {
        string text = _tools.PlanCaravan("Ali, Bob", "Rex, muffalo x2", "silver x500, pemmican x80", tile: 4600);

        Assert.That(text, Does.Contain("nothing has been started"));
        Assert.That(text, Does.Contain("Members (5): Ali, Bob, Rex (animal), muffalo (animal), muffalo (animal)"));
        Assert.That(text, Does.Contain("silver x500 (4 kg), pemmican x80 (4 kg)"));
        Assert.That(text, Does.Contain("Mass: 8 / 440 kg (1%)"));
        Assert.That(text, Does.Contain("Speed: 15 tiles per day (4000 ticks per tile)"));
        Assert.That(text, Does.Contain("Food: 20 days of food; it rots in 90 days; foraging: 1.5 berries per day"));
        Assert.That(text, Does.Contain("Visibility: 40%"));
        Assert.That(text, Does.Contain("Destination: tile 4600 (Temperate forest) | 12 tiles, ~2 days at this speed"));
        Assert.That(text, Does.Contain("Can be sent now: yes (form_caravan)"));
        Assert.That(_world.Calls, Is.Empty, "規劃是查詢，不動世界");
        Assert.That(_world.PlanCalls.Single(), Is.EqualTo("101+102+701+703+704|Silver:500+Pemmican:80|4600"), "沒倒地的同種動物先取");
    }

    [Test]
    public void PlanWithoutDestinationExplainsWhatIsMissing()
    {
        string text = _tools.PlanCaravan("Ali");

        Assert.That(text, Does.Contain("Destination: none chosen"));
        Assert.That(text, Does.Contain("The game would refuse to send it: You must choose a route first."));
        Assert.That(text, Does.Contain("Can be sent now: no"));
    }

    [Test]
    public void PlanReportsOverloadWarningsAndUnreachableDestination()
    {
        _world.PlanOverride = new CaravanPlanInfo
        {
            Members = new[] { new CaravanMemberInfo { Id = 101, Name = "Ali" } },
            MassUsage = 90f,
            MassCapacity = 40f,
            DaysUntilRot = 600f,
            DestinationTile = 4600,
            DestinationReachable = false,
            Blockers = new[] { "Too big caravan mass usage." },
            Warnings = new[] { "The caravan has no food." }
        };

        string text = _tools.PlanCaravan("Ali", tile: 4600);

        Assert.That(text, Does.Contain("Mass: 90 / 40 kg (225%) - OVERLOADED"));
        Assert.That(text, Does.Contain("nothing perishable"));
        Assert.That(text, Does.Contain("tile 4600 | not reachable"));
        Assert.That(text, Does.Contain("Warnings: The caravan has no food."));
        Assert.That(text, Does.Contain("The game would refuse to send it: Too big caravan mass usage."));
    }

    [Test]
    public void PlanWithAnInvalidRequestReportsTheGameError()
    {
        _world.Colonists[0].Drafted = true;

        Assert.That(_tools.PlanCaravan("Ali", tile: 4600), Does.StartWith("Error:").And.Contain("drafted"));
    }

    [Test]
    public void PrisonersJoinByName()
    {
        _tools.PlanCaravan("Ali", prisoners: "Zed", tile: 4600);

        Assert.That(_world.PlanCalls.Single(), Is.EqualTo("101+600||4600"));
        Assert.That(_tools.PlanCaravan("Ali", prisoners: "Nobody", tile: 4600), Does.StartWith("Error:"));
    }

    [Test]
    public void WorldObjectIdResolvesToItsTile()
    {
        _world.Survey = new WorldSurveyInfo { Objects = new[] { new WorldObjectInfo { Id = 302, Label = "bandit camp", Tile = 4650 } } };

        _tools.PlanCaravan("Ali", worldObjectId: 302);

        Assert.That(_world.PlanCalls.Single(), Is.EqualTo("101||4650"));
        Assert.That(_tools.PlanCaravan("Ali", worldObjectId: 999), Does.StartWith("Error:").And.Contain("999"));
    }

    // ---- 成員與貨物解析 ----

    [Test]
    public void AllMembersKeywordIsRefusedWithoutTouchingTheWorld()
    {
        string text = _tools.FormCaravan("all", tile: 4600);

        Assert.That(text, Does.StartWith("Error:").And.Contain("explicitly"));
        Assert.That(_world.Calls, Is.Empty);
        Assert.That(_world.PlanCalls, Is.Empty);
    }

    [Test]
    public void UnknownColonistReportsError()
    {
        string text = _tools.FormCaravan("Nobody", tile: 4600);

        Assert.That(text, Does.StartWith("Error:"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void AnimalsAcceptNamesIdsAndSpeciesCounts()
    {
        _tools.PlanCaravan("Ali", "701, 703", tile: 4600);
        _tools.PlanCaravan("Ali", "muffalo x3", tile: 4600);

        Assert.That(_world.PlanCalls[0], Is.EqualTo("101+701+703||4600"));
        Assert.That(_world.PlanCalls[1], Is.EqualTo("101+703+704+702||4600"), "倒地的排最後但仍可取");
    }

    [Test]
    public void TooManyOfASpeciesAndWildAnimalsAreRefused()
    {
        string tooMany = _tools.PlanCaravan("Ali", "husky x2", tile: 4600);
        string wild = _tools.PlanCaravan("Ali", "boomalope", tile: 4600);
        string wildByCount = _tools.PlanCaravan("Ali", "boomalope x1", tile: 4600);
        string garbage = _tools.PlanCaravan("Ali", "muffalo x0", tile: 4600);

        Assert.That(tooMany, Does.StartWith("Error:").And.Contain("only 1 colony animal"));
        Assert.That(wild, Does.StartWith("Error:").And.Contain("Wild animals"));
        Assert.That(wildByCount, Does.StartWith("Error:").And.Contain("only 0"));
        Assert.That(garbage, Does.StartWith("Error:").And.Contain("cannot understand"));
        Assert.That(_world.PlanCalls, Is.Empty);
    }

    [Test]
    public void CargoNamesResolveAgainstWhatTheColonyCanCarryAndMerge()
    {
        _tools.PlanCaravan("Ali", cargo: "Silver x100, bedroll x1, silver x50", tile: 4600);

        Assert.That(_world.PlanCalls.Single(), Is.EqualTo("101|Silver:150+Bedroll:1|4600"));
    }

    [Test]
    public void UnknownCargoAndExcessCountsAreRefused()
    {
        string unknown = _tools.PlanCaravan("Ali", cargo: "unobtanium x5", tile: 4600);
        string tooMuch = _tools.PlanCaravan("Ali", cargo: "medicine x13", tile: 4600);
        string badFormat = _tools.PlanCaravan("Ali", cargo: "silver", tile: 4600);

        Assert.That(unknown, Does.StartWith("Error:").And.Contain("storage or the home area"));
        Assert.That(tooMuch, Does.StartWith("Error:").And.Contain("only supply 12 medicine"));
        Assert.That(badFormat, Does.StartWith("Error:").And.Contain("name x count"));
        Assert.That(_world.PlanCalls, Is.Empty);
    }

    // ---- form_caravan ----

    [Test]
    public void FormsTheCaravanThroughTheWorldAndReportsTheTrip()
    {
        string text = _tools.FormCaravan("Ali, Bob", "Rex", "silver x500, pemmican x80", tile: 4600);

        Assert.That(text, Does.StartWith("Started forming a caravan with Ali, Bob, Rex for tile 4600"));
        Assert.That(text, Does.Contain("12 tiles").And.Contain("20 days"));
        Assert.That(text, Does.Contain("cancel_forming_caravan"));
        Assert.That(_world.Calls.Single(), Is.EqualTo("FormCaravan(101+102+701|Silver:500+Pemmican:80|4600)"));
        Assert.That(_world.Forming.Single().Cargo, Is.EquivalentTo(new[] { "silver x500", "pemmican x80" }));
    }

    [Test]
    public void WarningsTravelWithTheResult()
    {
        string text = _tools.FormCaravan("Ali", tile: 4600);

        Assert.That(text, Does.Contain("Warnings: The caravan has no food."));
        Assert.That(_world.Forming, Has.Count.EqualTo(1));
    }

    [Test]
    public void MissingDestinationIsRefusedWithTheGameReason()
    {
        string text = _tools.FormCaravan("Ali", cargo: "pemmican x80");

        Assert.That(text, Does.StartWith("Error:").And.Contain("would not let this caravan leave").And.Contain("You must choose a route first."));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void OverweightAndNoWalkingColonistAreRefusedBeforeAnythingMoves()
    {
        _world.PlanOverride = new CaravanPlanInfo
        {
            Members = new[] { new CaravanMemberInfo { Id = 101, Name = "Ali", Downed = true } },
            Blockers = new[] { "Too big caravan mass usage.", "A caravan needs at least one colonist who is not downed." }
        };

        string text = _tools.FormCaravan("Ali", tile: 4600);

        Assert.That(text, Does.StartWith("Error:").And.Contain("Too big caravan mass usage.").And.Contain("not downed"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void DraftedMemberMustUndraftFirst()
    {
        _world.Colonists[0].Drafted = true;

        string text = _tools.FormCaravan("Ali, Bob", tile: 4600);

        Assert.That(text, Does.StartWith("Error:").And.Contain("drafted"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void DownedColonistCanBeCarriedWhenSomeoneCanWalk()
    {
        _world.Colonists[0].Downed = true;

        string carried = _tools.FormCaravan("Ali, Bob", tile: 4600);
        Assert.That(carried, Does.StartWith("Started forming"));

        _world.Forming.Clear();
        string alone = _tools.FormCaravan("Ali", tile: 4600);
        Assert.That(alone, Does.StartWith("Error:").And.Contain("not downed"));
    }

    [Test]
    public void ConfirmationPreviewListsTheTripAndWarningsWithoutTouchingTheWorld()
    {
        var gated = new CaravanTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.FormCaravan("Ali, Bob", "Rex", "silver x500", tile: 4600);

        Assert.That(text, Does.StartWith(ToolText.ConfirmationPrefix + "would start forming a caravan with Ali, Bob, Rex (animal)"));
        Assert.That(text, Does.Contain("carrying silver x500").And.Contain("tile 4600").And.Contain("Warnings: The caravan has no food."));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public async Task FormCaravanIsRejectedWithoutClassifierInAuto()
    {
        var client = new ScriptedChatClient();
        var classifier = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
        client.CallTool("form_caravan", new { colonists = "Ali, Bob", tile = 4600 }).Reply("Done.");
        var session = new ColonyAgentSession(
            client,
            _world,
            () => ToolPermissions.For(ToolCategory.Observe, ToolCategory.Trade),
            8,
            () => AgentMode.Auto,
            classifierClient: classifier,
            classifierPrompt: () => "test classifier prompt");

        await session.SendAsync("Ali 跟 Bob 組商隊出發");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.ToolName, Is.EqualTo("form_caravan"));
        Assert.That(tool.ToolCategory, Is.EqualTo(ToolCategory.Trade));
        Assert.That(tool.RiskCheck, Is.EqualTo("high"));
        Assert.That(tool.Result, Does.StartWith(ToolText.ErrorPrefix).And.Contain("blacklist"));
        Assert.That(classifier.CallCount, Is.EqualTo(0), "黑名單不燒分類器模型");
        Assert.That(_world.Calls, Is.Empty);
        Assert.That(session.HasPendingConfirmation, Is.False);
    }

    // ---- cancel_forming_caravan ----

    private void StartForming()
    {
        Assert.That(_tools.FormCaravan("Ali, Bob", "Rex", "silver x10", tile: 4600), Does.StartWith("Started forming"));
        _world.Calls.Clear();
    }

    [Test]
    public void CancelStopsTheOnlyFormingCaravan()
    {
        StartForming();
        int id = _world.Forming.Single().Id;

        string text = _tools.CancelFormingCaravan();

        Assert.That(text, Does.StartWith("Stopped forming"));
        Assert.That(_world.Calls.Single(), Is.EqualTo($"CancelFormingCaravan({id})"));
        Assert.That(_world.Forming, Is.Empty);
    }

    [Test]
    public void CancelFindsTheCaravanByIdOrMember()
    {
        StartForming();
        int id = _world.Forming.Single().Id;
        _world.Forming.Add(new FormingCaravanInfo { Id = 99, Members = new[] { new CaravanMemberInfo { Id = 555, Name = "Cara" } } });

        Assert.That(_tools.CancelFormingCaravan(""), Does.StartWith("Error:").And.Contain("several"));
        Assert.That(_tools.CancelFormingCaravan("Cara"), Does.StartWith("Stopped"));
        Assert.That(_tools.CancelFormingCaravan(id.ToString()), Does.StartWith("Stopped"));
        Assert.That(_world.Forming, Is.Empty);
    }

    [Test]
    public void CancelByMemberIdAndUnknownNames()
    {
        StartForming();
        _world.Forming.Add(new FormingCaravanInfo { Id = 99, Members = new[] { new CaravanMemberInfo { Id = 555, Name = "Cara" } } });

        Assert.That(_tools.CancelFormingCaravan("Zoe"), Does.StartWith("Error:").And.Contain("no forming caravan matches"));
        Assert.That(_tools.CancelFormingCaravan("555"), Does.StartWith("Stopped"));
        Assert.That(_world.Forming.Select(f => f.Id), Does.Not.Contain(99));
    }

    [Test]
    public void CancelWithNothingFormingSaysSo()
    {
        string text = _tools.CancelFormingCaravan();

        Assert.That(text, Does.StartWith("Error:").And.Contain("no caravan is being formed"));
        Assert.That(_world.Calls, Is.Empty);
    }
}
