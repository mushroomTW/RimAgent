using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>世界地圖（endgame issue 03 A）：商隊與世界物件查詢、派商隊前往並選抵達動作、暫停與停下、就地定居。</summary>
[TestFixture]
public class WorldToolsTests
{
    private FakeGameWorld _world;
    private WorldTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Caravans.Add(MakeCaravan(12, "Trailblazers", 4500));
        _world.CaravanOrders.Add(new FloatMenuOptionInfo { Number = 1, Label = "Travel to tile 4600" });
        _world.CaravanOrders.Add(new FloatMenuOptionInfo { Number = 2, Label = "Trade with Foo Camp" });
        _world.CaravanOrders.Add(new FloatMenuOptionInfo { Number = 3, Label = "Attack Foo Camp" });
        _world.CaravanOrders.Add(new FloatMenuOptionInfo { Number = 4, Label = "Enter Foo Camp (cooldown 3 days)", Disabled = true });
        _tools = new WorldTools(_world);
    }

    private static CaravanInfo MakeCaravan(int id, string name, int tile)
    {
        return new CaravanInfo
        {
            Id = id,
            Name = name,
            Tile = tile,
            Terrain = "Temperate forest, Flat",
            Members = new[]
            {
                new CaravanMemberInfo { Id = 101, Name = "Ali", Kind = CaravanMemberInfo.ColonistKind, HealthPercent = 1f },
                new CaravanMemberInfo { Id = 700, Name = "Rex", Kind = CaravanMemberInfo.AnimalKind, HealthPercent = 0.5f, Downed = true }
            },
            FoodDays = 12.34f,
            MassUsage = 80f,
            MassCapacity = 200f,
            TicksPerMove = 3300,
            CanSettle = true,
            CanCamp = true
        };
    }

    private static WorldObjectInfo MakeObject(int id, string kind, string label, int tile, float distance, string faction = "", bool hostile = false)
    {
        return new WorldObjectInfo { Id = id, Kind = kind, Label = label, DefName = "Def" + id, Tile = tile, DistanceTiles = distance, Faction = faction, Hostile = hostile };
    }

    private void SeedWorldObjects()
    {
        _world.Survey = new WorldSurveyInfo
        {
            OriginTile = 4500,
            OriginLabel = "Trailblazers",
            OriginIsCaravan = true,
            Objects = new[]
            {
                MakeObject(301, WorldObjectInfo.FactionSettlementKind, "Far Fort", 9000, 40f, "Outlander Union", hostile: true),
                MakeObject(302, WorldObjectInfo.SiteKind, "bandit camp", 4600, 5f),
                MakeObject(303, WorldObjectInfo.PlayerSettlementKind, "New Arrivals", 4400, 2f, "New Arrivals"),
                MakeObject(304, WorldObjectInfo.OtherKind, "abandoned settlement", 4700, 9f)
            }
        };
        _world.Survey.Objects[1].Quests = new[] { new WorldObjectQuestLink { QuestId = 40, QuestName = "Bandit trouble", Active = false } };
        _world.Survey.Objects[1].Detail = "bandit camp";
        _world.Survey.Objects[1].ExpiresInTicks = 90000;
    }

    // ---- list_caravans ----

    [Test]
    public void ListCaravansShowsMembersFoodMassAndSettleOptions()
    {
        string text = _tools.ListCaravans();

        Assert.That(text, Does.StartWith("1 caravan:"));
        Assert.That(text, Does.Contain("[12] Trailblazers | tile 4500 (Temperate forest, Flat)"));
        Assert.That(text, Does.Contain("status: stopped, no destination"));
        Assert.That(text, Does.Contain("Ali (colonist, health 100%)"));
        Assert.That(text, Does.Contain("Rex (animal, health 50%, downed)"));
        Assert.That(text, Does.Contain("food: 12.3 days | mass 80/200 kg | 3300 ticks per tile"));
        Assert.That(text, Does.Contain("found a colony here: yes | set up camp here: yes"));
    }

    [Test]
    public void ListCaravansWithoutAnyExplainsHowToFormOne()
    {
        _world.Caravans.Clear();

        Assert.That(_tools.ListCaravans(), Does.Contain("no caravans").And.Contain("form_caravan"));
    }

    [Test]
    public void ListCaravansShowsCaravansStillFormingOnTheMap()
    {
        _world.Forming.Add(new FormingCaravanInfo
        {
            Id = 70, Status = "Gathering items", ExitX = 0, ExitZ = 30, Cargo = new[] { "silver x500" },
            Members = new[] { new CaravanMemberInfo { Id = 102, Name = "Bob" } }
        });

        string text = _tools.ListCaravans();

        Assert.That(text, Does.Contain("1 caravan:").And.Contain("[12] Trailblazers"));
        Assert.That(text, Does.Contain("1 forming caravan still on the map").And.Contain("cancel_forming_caravan"));
        Assert.That(text, Does.Contain("[70] Gathering items | leaves the map at (0,30)"));
        Assert.That(text, Does.Contain("members: Bob (colonist, health 100%)"));
        Assert.That(text, Does.Contain("cargo: silver x500"));
    }

    [Test]
    public void ListCaravansWithOnlyAFormingOneStillShowsIt()
    {
        _world.Caravans.Clear();
        _world.Forming.Add(new FormingCaravanInfo { Id = 70, Status = "Waiting", Members = new[] { new CaravanMemberInfo { Id = 102, Name = "Bob" } } });

        string text = _tools.ListCaravans();

        Assert.That(text, Does.StartWith("No caravans on the world map yet."));
        Assert.That(text, Does.Contain("[70] Waiting").And.Contain("cargo: none"));
    }

    [Test]
    public void TravellingCaravanShowsDestinationArrivalActionAndEta()
    {
        CaravanInfo c = _world.Caravans[0];
        c.Moving = true;
        c.DestinationTile = 4600;
        c.DestinationLabel = "Foo Camp [88], tile 4600 (Boreal forest)";
        c.ArrivalAction = "Trade with Foo Camp";
        c.EtaTicks = 150000;

        string text = _tools.ListCaravans();

        Assert.That(text, Does.Contain("status: travelling to Foo Camp [88], tile 4600 (Boreal forest), then: Trade with Foo Camp, arrival in ~2.5 days"));

        c.Paused = true;
        Assert.That(_tools.ListCaravans(), Does.Contain("paused on the way to Foo Camp"));
    }

    [Test]
    public void StuckStatesAreSpelledOut()
    {
        CaravanInfo c = _world.Caravans[0];
        c.Resting = true;
        c.Immobilized = true;
        c.AllOwnersDowned = true;
        c.AllOwnersInMentalBreak = true;
        c.MassUsage = 250f;
        c.CanSettle = false;
        c.SettleBlockedReason = "There is already a settlement here";
        c.CanCamp = false;
        c.CampBlockedReason = "A camp cannot be made here";
        c.ObjectsHere = new[] { "Foo Camp [88]" };

        string text = _tools.ListCaravans();

        Assert.That(text, Does.Contain("resting for the night; immobilized: overloaded; all members are downed; all members are in a mental break"));
        Assert.That(text, Does.Contain("mass 250/200 kg (overloaded)"));
        Assert.That(text, Does.Contain("found a colony here: no (There is already a settlement here)"));
        Assert.That(text, Does.Contain("set up camp here: no (A camp cannot be made here)"));
        Assert.That(text, Does.Contain("here: Foo Camp [88]"));
    }

    // ---- list_world_objects ----

    [Test]
    public void ObjectsAreListedNearestFirstWithTravelTimeAndQuestLink()
    {
        SeedWorldObjects();
        _world.TravelEstimates[9000] = new TravelEstimateInfo { Tile = 9000, Reachable = true, Ticks = 400000, DistanceTiles = 40f };
        _world.TravelEstimates[4700] = new TravelEstimateInfo { Tile = 4700, Reachable = false, DistanceTiles = 9f };

        string text = _tools.ListWorldObjects();

        Assert.That(text, Does.StartWith("4 world objects from Trailblazers (tile 4500):"));
        int fort = text.IndexOf("[301]"), site = text.IndexOf("[302]"), home = text.IndexOf("[303]"), ruin = text.IndexOf("[304]");
        Assert.That(home, Is.LessThan(site));
        Assert.That(site, Is.LessThan(ruin));
        Assert.That(ruin, Is.LessThan(fort));
        Assert.That(text, Does.Contain("[301] Far Fort (faction settlement, Outlander Union, hostile), tile 9000 | 40 tiles, ~6.7 days"));
        Assert.That(text, Does.Contain("[302] bandit camp (site), tile 4600, bandit camp, gone in ~1.5 days | 10 tiles, ~1 day | quest [40] Bandit trouble (not accepted yet)"));
        Assert.That(text, Does.Contain("[304] abandoned settlement (other), tile 4700 | no land route"));
    }

    [Test]
    public void ObjectsFromTheCurrentMapSayTravelTimesAssumeDefaultSpeed()
    {
        SeedWorldObjects();
        _world.Survey.OriginIsCaravan = false;
        _world.Survey.OriginLabel = "New Arrivals";
        _world.Survey.OriginTile = 4400;
        _world.Caravans.Add(MakeCaravan(13, "Second", 4510));

        string text = _tools.ListWorldObjects();

        Assert.That(text, Does.Contain("from New Arrivals (tile 4400); travel times assume a default-speed caravan"));
    }

    [TestCase("player", "[303]")]
    [TestCase("faction", "[301]")]
    [TestCase("site", "[302]")]
    [TestCase("quest", "[302]")]
    [TestCase("other", "[304]")]
    public void KindFilterKeepsOnlyThatKind(string kind, string expectedId)
    {
        SeedWorldObjects();

        string text = _tools.ListWorldObjects(kind: kind);

        Assert.That(text, Does.Contain(expectedId));
        Assert.That(text.Split(["- ["], System.StringSplitOptions.None).Length - 1, Is.EqualTo(1), text);
    }

    [Test]
    public void SearchMatchesLabelFactionAndQuestName()
    {
        SeedWorldObjects();

        Assert.That(_tools.ListWorldObjects(search: "outlander"), Does.Contain("[301]").And.Not.Contain("[302]"));
        Assert.That(_tools.ListWorldObjects(search: "trouble"), Does.Contain("[302]").And.Not.Contain("[301]"));
        Assert.That(_tools.ListWorldObjects(search: "zzz"), Does.StartWith("No matching world objects (4 known in total)"));
    }

    [Test]
    public void LimitCutsTheListAndSaysHowManyThereAre()
    {
        SeedWorldObjects();

        string text = _tools.ListWorldObjects(limit: 2);

        Assert.That(text, Does.StartWith("Nearest 2 of 4 world objects"));
        Assert.That(text, Does.Contain("[303]").And.Contain("[302]").And.Not.Contain("[304]"));
    }

    [Test]
    public void UnknownKindAndUnknownCaravanAreRefused()
    {
        Assert.That(_tools.ListWorldObjects(kind: "cities"), Does.StartWith("Error:").And.Contain("quest"));
        Assert.That(_tools.ListWorldObjects(caravan: "Nobody"), Does.StartWith("Error:").And.Contain("Trailblazers"));
    }

    // ---- get_world_tile ----

    [Test]
    public void TileShowsTerrainSettleReasonRouteObjectsAndOrders()
    {
        SeedWorldObjects();
        _world.WorldTiles[4600] = new WorldTileInfo
        {
            Tile = 4600,
            Biome = "Boreal forest",
            Hilliness = "Large hills",
            TemperatureC = 3.2f,
            MinTemperatureC = -30f,
            MaxTemperatureC = 24f,
            RainfallMm = 600f,
            ElevationM = 1200f,
            Coastal = true,
            Feature = "Misty Range",
            CanSettle = false,
            SettleBlockedReason = "A faction base is adjacent",
            Objects = new[] { _world.Survey.Objects[1] }
        };
        _world.TravelEstimates[4600] = new TravelEstimateInfo { Tile = 4600, Reachable = true, Ticks = 90000, DistanceTiles = 5f };

        string text = _tools.GetWorldTile(tile: 4600);

        Assert.That(text, Does.StartWith("Tile 4600: Boreal forest, Large hills | feature: Misty Range"));
        Assert.That(text, Does.Contain("Temperature 3.2C on average (-30C to 24C), rainfall 600 mm, elevation 1200 m, coastal"));
        Assert.That(text, Does.Contain("A colony can be founded here: no (A faction base is adjacent)"));
        Assert.That(text, Does.Contain("From Trailblazers (tile 4500): 5 tiles, ~1.5 days"));
        Assert.That(text, Does.Contain("World objects here:").And.Contain("[302] bandit camp"));
        Assert.That(text, Does.Contain("Orders for Trailblazers on this tile (send_caravan):"));
        Assert.That(text, Does.Contain("1) Travel to tile 4600").And.Contain("2) Trade with Foo Camp").And.Contain("4) Enter Foo Camp (cooldown 3 days) [disabled]"));
    }

    [Test]
    public void TileCanBeGivenAsAWorldObjectAndMismatchIsRefused()
    {
        SeedWorldObjects();

        Assert.That(_tools.GetWorldTile(worldObjectId: 302), Does.StartWith("Tile 4600:"));
        Assert.That(_tools.GetWorldTile(tile: 4600, worldObjectId: 302), Does.StartWith("Tile 4600:"));
        Assert.That(_tools.GetWorldTile(tile: 1, worldObjectId: 302), Does.StartWith("Error:").And.Contain("tile 4600"));
        Assert.That(_tools.GetWorldTile(worldObjectId: 999), Does.StartWith("Error:").And.Contain("list_world_objects"));
        Assert.That(_tools.GetWorldTile(worldObjectId: 12), Does.StartWith("Tile 4500:"), "自己的商隊也能當目標");
    }

    [Test]
    public void TileWithoutADefaultDestinationUsesTheCaravansOwnTile()
    {
        Assert.That(_tools.GetWorldTile(), Does.StartWith("Tile 4500:"));
    }

    [Test]
    public void TileWithoutAnyCaravanListsNoOrders()
    {
        _world.Caravans.Clear();

        string text = _tools.GetWorldTile(tile: 4600);

        Assert.That(text, Does.StartWith("Tile 4600:"));
        Assert.That(text, Does.Not.Contain("Orders for"));
    }

    [Test]
    public void MissingTileWorldErrorIsReported()
    {
        Assert.That(_tools.GetWorldTile(tile: 123456), Does.StartWith("Error:").And.Contain("does not exist"));
    }

    [Test]
    public void OrderErrorFromTheGameIsShownInsteadOfAMenu()
    {
        _world.CaravanOrdersError = "tile 4600 does not exist on this layer of the world";

        Assert.That(_tools.GetWorldTile(tile: 4600), Does.Contain("Orders: tile 4600 does not exist"));
    }

    // ---- send_caravan ----

    [Test]
    public void SendUsesTheNumberedOrderAndReportsTheEta()
    {
        string text = _tools.SendCaravan("Trailblazers", "2) Trade with Foo Camp", tile: 4600);

        Assert.That(_world.Calls.Single(), Is.EqualTo("SendCaravan(12,4600,2,Trade with Foo Camp)"));
        Assert.That(text, Does.StartWith("Ordered Trailblazers: \"Trade with Foo Camp\" on tile 4600."));
        Assert.That(text, Does.Contain("on its way to tile 4600").And.Contain("arrival in ~2.5 days"));
    }

    [Test]
    public void SendAcceptsABareNumberALabelAndAWorldObjectDestination()
    {
        SeedWorldObjects();

        _tools.SendCaravan("", "3", worldObjectId: 302);
        _tools.SendCaravan("12", "trade with foo", tile: 4600);

        Assert.That(_world.Calls[0], Is.EqualTo("SendCaravan(12,4600,3,Attack Foo Camp)"));
        Assert.That(_world.Calls[1], Is.EqualTo("SendCaravan(12,4600,2,Trade with Foo Camp)"));
    }

    [Test]
    public void StaleNumberWithADifferentLabelIsCaughtBeforeTheWorldIsTouched()
    {
        // 模型記得的是「2) Attack Foo Camp」，但選單現在第 2 項是交易。
        string text = _tools.SendCaravan("Trailblazers", "2) Attack Foo Camp", tile: 4600);

        Assert.That(text, Does.StartWith("Error:").And.Contain("order 2 is now \"Trade with Foo Camp\"").And.Contain("menu changed"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void UnknownNumberAndDisabledAndAmbiguousOrdersAreRefused()
    {
        Assert.That(_tools.SendCaravan("", "9", tile: 4600), Does.StartWith("Error:").And.Contain("there is no order 9").And.Contain("1) Travel to tile 4600"));
        Assert.That(_tools.SendCaravan("", "4", tile: 4600), Does.StartWith("Error:").And.Contain("disabled"));
        Assert.That(_tools.SendCaravan("", "Foo Camp", tile: 4600), Does.StartWith("Error:").And.Contain("matches several"));
        Assert.That(_tools.SendCaravan("", "fly", tile: 4600), Does.StartWith("Error:").And.Contain("no order matches"));
        Assert.That(_tools.SendCaravan("", "", tile: 4600), Does.StartWith("Error:"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void SendNeedsADestinationAndAResolvableCaravan()
    {
        Assert.That(_tools.SendCaravan("", "1"), Does.StartWith("Error:").And.Contain("destination"));
        Assert.That(_tools.SendCaravan("Nobody", "1", tile: 4600), Does.StartWith("Error:").And.Contain("no caravan is named"));

        _world.Caravans.Add(MakeCaravan(13, "Traders", 4510));
        Assert.That(_tools.SendCaravan("", "1", tile: 4600), Does.StartWith("Error:").And.Contain("name the caravan").And.Contain("[13] Traders"));
        Assert.That(_tools.SendCaravan("Trailblazers", "1", tile: 4600), Does.Not.StartWith("Error:"));

        _world.Caravans.Clear();
        Assert.That(_tools.SendCaravan("", "1", tile: 4600), Does.StartWith("Error:").And.Contain("no caravans"));
    }

    [Test]
    public void SendFailureAndDialogFromTheGameAreReported()
    {
        _world.CaravanOrdersError = "tile 4600 does not exist on this layer of the world";
        Assert.That(_tools.SendCaravan("", "1", tile: 4600), Does.StartWith("Error:").And.Contain("does not exist"));
        _world.CaravanOrdersError = string.Empty;

        _world.FollowUpDialog = new PendingChoiceInfo
        {
            Id = "dialog-7",
            Title = "Attack friendly faction?",
            Text = "Attacking will hurt relations.",
            Options = new[] { new ChoiceOptionInfo { Number = 1, Text = "Confirm" }, new ChoiceOptionInfo { Number = 2, Text = "Cancel" } }
        };
        string text = _tools.SendCaravan("", "3", tile: 4600);

        Assert.That(text, Does.Contain("waiting for an answer").And.Contain("A dialog is open").And.Contain("Attacking will hurt relations."));
    }

    // ---- set_caravan_movement ----

    [Test]
    public void PauseResumeAndStopReachTheWorld()
    {
        _world.Caravans[0].Moving = true;
        _world.Caravans[0].DestinationTile = 4600;

        Assert.That(_tools.SetCaravanMovement("", "pause"), Does.Contain("pause"));
        Assert.That(_world.Caravans[0].Paused, Is.True);
        _tools.SetCaravanMovement("Trailblazers", "RESUME");
        Assert.That(_world.Caravans[0].Paused, Is.False);
        _tools.SetCaravanMovement("12", " stop ");
        Assert.That(_world.Caravans[0].Moving, Is.False);
        Assert.That(_world.Calls, Is.EqualTo(["SetCaravanMovement(12,pause)", "SetCaravanMovement(12,resume)", "SetCaravanMovement(12,stop)"]));
    }

    [Test]
    public void MovementRefusalsComeFromTheGameOrTheArguments()
    {
        Assert.That(_tools.SetCaravanMovement("", "pause"), Does.StartWith("Error:").And.Contain("no destination"));
        Assert.That(_tools.SetCaravanMovement("", "camp"), Does.StartWith("Error:").And.Contain("pause, resume or stop"));
        Assert.That(_tools.SetCaravanMovement("Nobody", "stop"), Does.StartWith("Error:"));
    }

    // ---- settle_caravan ----

    [Test]
    public void SettleAndCampReachTheWorld()
    {
        Assert.That(_tools.SettleCaravan("", "settle"), Does.Contain("starting to settle"));
        Assert.That(_tools.SettleCaravan("Trailblazers", "Camp"), Does.Contain("starting to camp"));
        Assert.That(_world.Calls, Is.EqualTo(["SettleCaravan(12,settle)", "SettleCaravan(12,camp)"]));
    }

    [Test]
    public void SettleIsRefusedWithTheGamesReasonBeforeAnythingHappens()
    {
        _world.Caravans[0].CanSettle = false;
        _world.Caravans[0].SettleBlockedReason = "There is already a settlement here";
        _world.Caravans[0].CanCamp = false;

        Assert.That(_tools.SettleCaravan("", "settle"), Does.StartWith("Error:").And.Contain("found a colony").And.Contain("There is already a settlement here"));
        Assert.That(_tools.SettleCaravan("", "camp"), Does.StartWith("Error:").And.Contain("set up camp"));
        Assert.That(_tools.SettleCaravan("", "build"), Does.StartWith("Error:").And.Contain("settle or camp"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void SettleConfirmationDialogIsHandedToTheModel()
    {
        _world.FollowUpDialog = new PendingChoiceInfo
        {
            Id = "dialog-9",
            Title = "Settle here?",
            Text = "This is close to a faction base.",
            Options = new[] { new ChoiceOptionInfo { Number = 1, Text = "Confirm" } }
        };

        string text = _tools.SettleCaravan("", "settle");

        Assert.That(text, Does.Contain("A dialog is open").And.Contain("This is close to a faction base."));
    }

    // ---- 閘門與登錄 ----

    [Test]
    public void BlockedCommandsPreviewAndTouchNothing()
    {
        var gate = new ConfirmationGate(() => AgentMode.Confirm);
        var tools = new WorldTools(_world, gate);
        _world.Caravans[0].Moving = true;

        string send = tools.SendCaravan("", "2) Trade with Foo Camp", tile: 4600);
        string move = tools.SetCaravanMovement("", "stop");
        string settle = tools.SettleCaravan("", "settle");

        Assert.That(send, Does.StartWith("Needs confirmation: would send Trailblazers [12] to tile 4600: \"Trade with Foo Camp\""));
        Assert.That(move, Does.StartWith("Needs confirmation: would stop Trailblazers"));
        Assert.That(settle, Does.StartWith("Needs confirmation: would found a new colony with Trailblazers [12] (Ali, Rex) on tile 4500"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void RegistryPutsTheWorldToolsInTheTradeCategoryAndTradePack()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());
        var expected = new Dictionary<string, ToolRisk>
        {
            ["list_caravans"] = ToolRisk.Query,
            ["list_world_objects"] = ToolRisk.Query,
            ["get_world_tile"] = ToolRisk.Query,
            ["send_caravan"] = ToolRisk.Confirm,
            ["set_caravan_movement"] = ToolRisk.Safe,
            ["settle_caravan"] = ToolRisk.Confirm
        };
        foreach (KeyValuePair<string, ToolRisk> pair in expected)
        {
            Assert.That(registry.RiskOf(pair.Key), Is.EqualTo(pair.Value), pair.Key);
            AgentToolDescriptor descriptor = registry.Descriptors.Single(d => d.Name == pair.Key);
            Assert.That(descriptor.Category, Is.EqualTo(ToolCategory.Trade), pair.Key);
            Assert.That(ToolPacks.PackOf(pair.Key, descriptor.Category), Is.EqualTo("trade"), pair.Key);
        }
    }

    [Test]
    public void YoloModeKeepsSendingAndSettlingCaravansOutOfReach()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());
        List<string> yolo = [.. registry.Select(ToolPermissions.All, AgentMode.Yolo).OfType<AIFunction>().Select(f => f.Name)];
        List<string> autonomous = [.. registry.Select(ToolPermissions.All, AgentMode.Autonomous).OfType<AIFunction>().Select(f => f.Name)];

        Assert.That(yolo, Does.Not.Contain("send_caravan").And.Not.Contain("settle_caravan").And.Contain("set_caravan_movement").And.Contain("list_caravans"));
        Assert.That(autonomous, Does.Contain("send_caravan").And.Contain("settle_caravan"));
    }

    // ---- 解析與格式 ----

    [Test]
    public void CaravanResolutionPrefersExactNamesAndListsCandidatesWhenAmbiguous()
    {
        var caravans = new List<CaravanInfo> { MakeCaravan(1, "Trade run", 1), MakeCaravan(2, "Trade run north", 2), MakeCaravan(3, "Scouts", 3) };

        Assert.That(WorldTools.ResolveCaravan("trade run", caravans, true).Value.Id, Is.EqualTo(1));
        Assert.That(WorldTools.ResolveCaravan("scout", caravans, true).Value.Id, Is.EqualTo(3));
        Assert.That(WorldTools.ResolveCaravan("2", caravans, true).Value.Id, Is.EqualTo(2));
        Assert.That(WorldTools.ResolveCaravan("trade", caravans, true).Error, Does.Contain("several").And.Contain("[1] Trade run").And.Contain("[2]"));
        Assert.That(WorldTools.ResolveCaravan("77", caravans, true).Error, Does.Contain("no caravan has id 77"));
        Assert.That(WorldTools.ResolveCaravan("", caravans, false).Value, Is.Null);
        Assert.That(WorldTools.ResolveCaravan("", caravans, true).Success, Is.False);
        Assert.That(WorldTools.ResolveCaravan("", new[] { caravans[2] }, true).Value.Id, Is.EqualTo(3));
    }

    [TestCase(0, "no time")]
    [TestCase(2500, "~1 h")]
    [TestCase(5000, "~2 h")]
    [TestCase(59000, "~24 h")]
    [TestCase(60000, "~1 day")]
    [TestCase(90000, "~1.5 days")]
    public void DurationIsShownInHoursUnderADayAndDaysAbove(int ticks, string expected)
    {
        Assert.That(WorldTools.Duration(ticks), Is.EqualTo(expected));
    }

    [Test]
    public async Task ToolsRunThroughTheRegistryAndAreLogged()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript(), null, new ConfirmationGate(() => AgentMode.Autonomous));
        AIFunction list = registry.All.OfType<AIFunction>().Single(t => t.Name == "list_caravans");

        string text = (await list.InvokeAsync([]))?.ToString();

        Assert.That(text, Does.Contain("[12] Trailblazers"));
    }
}
