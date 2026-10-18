using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class BuildToolsTests
{
    private FakeGameWorld _world;
    private BuildTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Things.Add(FakeGameWorld.MakeThing(1, "MineableSteel", "compacted steel", ThingKind.Mineable, 5, 5));
        _world.Things.Add(FakeGameWorld.MakeThing(2, "MineableSteel", "compacted steel", ThingKind.Mineable, 6, 5));
        _world.Things.Add(FakeGameWorld.MakeThing(3, "Granite", "granite", ThingKind.Mineable, 7, 5));
        _world.Things.Add(FakeGameWorld.MakeThing(4, "MineableSteel", "compacted steel", ThingKind.Mineable, 50, 50));
        _world.Things.Add(FakeGameWorld.MakeThing(5, "Plant_TreeOak", "oak tree", ThingKind.Plant, 8, 5, harvestable: true));
        _world.Things.Add(FakeGameWorld.MakeThing(6, "Plant_Grass", "grass", ThingKind.Plant, 9, 5, harvestable: false));
        _world.Things.Add(FakeGameWorld.MakeThing(7, "Wall", "wall", ThingKind.Building, 5, 6, playerOwned: true));
        _world.Things.Add(FakeGameWorld.MakeThing(8, "AncientWall", "ancient wall", ThingKind.Building, 6, 6, playerOwned: false));
        _tools = new BuildTools(_world);
    }

    [Test]
    public void AllowForbiddenItemsOnWholeMap()
    {
        _world.Things.Add(FakeGameWorld.MakeThing(20, "Gun_Revolver", "revolver", ThingKind.Item, 30, 30));
        _world.Things.Add(FakeGameWorld.MakeThing(21, "Steel", "steel", ThingKind.Item, 31, 30));
        _world.Things[_world.Things.Count - 2].Forbidden = true;
        _world.Things[_world.Things.Count - 1].Forbidden = true;

        string text = _tools.SetForbidden(false, -1, -1, -1, -1, "");

        Assert.That(text, Is.EqualTo("Allowed 2 item(s): revolver, steel."));
        Assert.That(_world.Calls, Is.EqualTo(["SetForbidden(map,,False)"]));
    }

    [TestCase(-1, 0, 10, 10)]
    [TestCase(0, 0, 10, -1)]
    [TestCase(-2, -2, -2, -2)]
    public void SetForbiddenRejectsInvalidWholeMapCoordinates(int x1, int z1, int x2, int z2)
    {
        Assert.That(_tools.SetForbidden(true, x1, z1, x2, z2, ""),
            Does.StartWith("Error:").And.Contain("-1 for all four corners"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void ForbidPassesNormalizedRectangleAndFilter()
    {
        _world.Things.Add(FakeGameWorld.MakeThing(20, "Steel", "steel", ThingKind.Item, 5, 5));

        string text = _tools.SetForbidden(true, 10, 10, 0, 0, " steel ");

        Assert.That(text, Is.EqualTo("Forbade 1 item(s): steel."));
        Assert.That(_world.Calls, Is.EqualTo(["SetForbidden((0,0)-(10,10),steel,True)"]));
    }

    [Test]
    public void SetForbiddenReportsNothingToChange()
    {
        Assert.That(_tools.SetForbidden(false, -1, -1, -1, -1, ""), Does.StartWith("Error:"));
    }

    [Test]
    public void MineFilteredByNameSkipsAlreadyMarked()
    {
        _world.Things[0].Designations = new[] { "Mine" };
        string text = _tools.DesignateArea("mine", false, 0, 0, 10, 10, "steel");
        Assert.That(text, Is.EqualTo("Marked 1 for mining, 1 already marked."));
        Assert.That(_world.Calls, Is.EqualTo(["AddDesignationOnCell(6,5,Mine)"]));
        Assert.That(_world.Things[1].Designations, Does.Contain("Mine"));
        Assert.That(_world.Things[2].Designations, Is.Empty, "granite 不符合 steel 過濾");
    }

    [Test]
    public void HarvestOnlyMarksRipePlants()
    {
        string text = _tools.DesignateArea("harvest", false, 0, 0, 10, 10, "");
        Assert.That(text, Does.Contain("Marked 1 for harvesting").And.Contain("1 not applicable"));
        Assert.That(_world.Calls, Is.EqualTo(["AddDesignationOnThing(5,HarvestPlant)"]));
    }

    [Test]
    public void DeconstructOnlyPlayerBuildings()
    {
        string text = _tools.DesignateArea("deconstruct", false, 0, 0, 10, 10, "");
        Assert.That(text, Does.Contain("Marked 1 for deconstruction"));
        Assert.That(_world.Calls, Is.EqualTo(["AddDesignationOnThing(7,Deconstruct)"]));
    }

    [Test]
    public void SelectionIsUsedWhenRequested()
    {
        _world.Selection.Add(_world.Things[3]);
        _world.Selection.Add(_world.Things[4]);
        string text = _tools.DesignateArea("mine", true, 0, 0, 0, 0, "");
        Assert.That(text, Is.EqualTo("Marked 1 for mining."));
        Assert.That(_world.Calls, Is.EqualTo(["AddDesignationOnCell(50,50,Mine)"]));
    }

    [Test]
    public void EmptySelectionIsAnError()
    {
        Assert.That(_tools.DesignateArea("mine", true, 0, 0, 0, 0, ""), Does.StartWith("Error:").And.Contain("selection"));
    }

    [Test]
    public void UnknownKindAndOutOfBoundsAreErrors()
    {
        Assert.That(_tools.DesignateArea("paint", false, 0, 0, 10, 10, ""), Does.StartWith("Error:").And.Contain("cut_plants"));
        Assert.That(_tools.DesignateArea("mine", false, 0, 0, 999, 10, ""), Does.StartWith("Error:").And.Contain("outside"));
    }

    [Test]
    public void NothingMatchingIsAnErrorThatSuggestsFindThings()
    {
        Assert.That(_tools.DesignateArea("mine", false, 20, 20, 30, 30, "gold"), Does.StartWith("Error:").And.Contain("find_things"));
    }

    [Test]
    public void CancelRemovesOnlyExistingMarks()
    {
        _world.Things[0].Designations = new[] { "Mine" };
        _world.Things[4].Designations = new[] { "HarvestPlant" };
        string text = _tools.CancelDesignation("mine", false, 0, 0, 10, 10);
        Assert.That(text, Is.EqualTo("Removed 1 mining mark(s)."));
        Assert.That(_world.Things[0].Designations, Is.Empty);
        Assert.That(_world.Things[4].Designations, Does.Contain("HarvestPlant"));
        Assert.That(_tools.CancelDesignation("mine", false, 0, 0, 10, 10), Does.Contain("No mining marks"));
    }

    [Test]
    public void BlueprintLinePlacesEachCellAndReportsBlockedOnes()
    {
        _world.BlockedCells[(12, 20)] = "space already occupied";
        string text = _tools.PlaceBlueprint("wall", "line", 10, 20, 14, 20, 0, "wood");
        Assert.That(text, Does.StartWith("Placed 4 wall (WoodLog) blueprint(s) from (10,20) to (14,20) (line).").And.Contain("(12,20): space already occupied"));
        Assert.That(_world.Blueprints.Select(b => b.X), Is.EqualTo([10, 11, 13, 14]));
        Assert.That(_world.Blueprints.All(b => b.Def == "Wall" && b.Stuff == "WoodLog"), Is.True);
    }

    [Test]
    public void BlueprintLineWalksFromTheStartTowardTheEnd()
    {
        _tools.PlaceBlueprint("wall", "line", 5, 9, 5, 7, 0, "");
        Assert.That(_world.Blueprints.Select(b => b.Z), Is.EqualTo([9, 8, 7]));
    }

    [Test]
    public void SingleBlueprintWithDefaultStuffIgnoresTheSecondCorner()
    {
        string text = _tools.PlaceBlueprint("Bed", "single", 3, 3, 90, 90, 1, "");
        Assert.That(text, Is.EqualTo("Placed 1 bed blueprint(s) at (3,3)."));
        Assert.That(_world.Blueprints.Single().Rotation, Is.EqualTo(1));
        Assert.That(_world.Blueprints.Single().Stuff, Is.Empty);
    }

    [Test]
    public void BlueprintFillCoversTheWholeRectangle()
    {
        string text = _tools.PlaceBlueprint("wall", "fill", 4, 3, 2, 2, 0, "");
        Assert.That(text, Does.StartWith("Placed 6 wall blueprint(s) from (4,3) to (2,2) (fill)."));
        Assert.That(_world.Blueprints.Select(b => (b.X, b.Z)), Is.EquivalentTo(new[] { (2, 2), (3, 2), (4, 2), (2, 3), (3, 3), (4, 3) }));
    }

    [Test]
    public void BlueprintOutlinePlacesOnlyTheBorder()
    {
        string text = _tools.PlaceBlueprint("wall", "outline", 10, 10, 13, 12, 0, "");
        Assert.That(text, Does.StartWith("Placed 10 wall blueprint(s) from (10,10) to (13,12) (outline)."));
        Assert.That(_world.Blueprints.Any(b => b.X == 11 && b.Z == 11), Is.False, "內部不放");
        Assert.That(_world.Blueprints.Any(b => b.X == 12 && b.Z == 11), Is.False);
    }

    [Test]
    public void BlueprintErrorsForUnknownBuildingShapeRotationBentLineAndSize()
    {
        Assert.That(_tools.PlaceBlueprint("teleporter", "single", 3, 3, 3, 3, 0, ""), Does.StartWith("Error:").And.Contain("buildable"));
        Assert.That(_tools.PlaceBlueprint("wall", "circle", 3, 3, 5, 5, 0, ""), Does.StartWith("Error:").And.Contain("single, line, fill or outline"));
        Assert.That(_tools.PlaceBlueprint("wall", "single", 3, 3, 3, 3, 7, ""), Does.StartWith("Error:").And.Contain("rotation"));
        Assert.That(_tools.PlaceBlueprint("wall", "line", 3, 3, 5, 5, 0, ""), Does.StartWith("Error:").And.Contain("straight"));
        Assert.That(_tools.PlaceBlueprint("wall", "fill", 0, 0, 20, 19, 0, ""), Does.StartWith("Error:").And.Contain("420 cells"));
        Assert.That(_world.Blueprints, Is.Empty);
    }

    [Test]
    public void BlueprintOutsideMapCellsAreReported()
    {
        Assert.That(_tools.PlaceBlueprint("wall", "line", 99, 99, 101, 99, 0, ""), Does.StartWith("Placed 1").And.Contain("outside the map"));
        Assert.That(_tools.PlaceBlueprint("wall", "single", 200, 200, 200, 200, 0, ""), Does.StartWith("Error:").And.Contain("outside"));
    }

    [Test]
    public void AmbiguousStuffIsAnError()
    {
        _world.Stuffs = new[] { new DefInfo("WoodLog", "wood"), new DefInfo("WoodPlanks", "wooden planks") };
        Assert.That(_tools.PlaceBlueprint("wall", "single", 3, 3, 3, 3, 0, "woo"), Does.StartWith("Error:").And.Contain("WoodLog").And.Contain("WoodPlanks"));
    }

    private void AddShipParts()
    {
        _world.Buildables = _world.Buildables.Concat(new[] { new DefInfo("Ship_Reactor", "ship reactor"), new DefInfo("Ship_SensorCluster", "ship sensor cluster") }).ToList();
        _world.BuildingInfos["Ship_Reactor"] = new BuildingInfo
        {
            DefName = "Ship_Reactor",
            Label = "ship reactor",
            Width = 6,
            Height = 7,
            Costs = new[] { new BuildingCost { Label = "steel", Count = 350 }, new BuildingCost { Label = "plasteel", Count = 280 } },
            TerrainAffordance = "Heavy",
            Research = new[] { new BuildingResearch { Label = "ship reactor", Done = false } },
            ConstructionSkill = 8,
            PlaceWorkers = new[] { "PlaceWorker_NotUnderRoof" },
            ShipPart = true,
            PowerWatts = -1000f
        };
        _world.BuildingInfos["Ship_SensorCluster"] = new BuildingInfo { DefName = "Ship_SensorCluster", Label = "ship sensor cluster", Width = 2, Height = 2 };
    }

    [Test]
    public void LargeBlueprintReportsSizeRotationAndCoveredRectangle()
    {
        AddShipParts();

        string text = _tools.PlaceBlueprint("ship reactor", "single", 41, 45, 41, 45, 0, "");

        Assert.That(text, Is.EqualTo("Placed 1 ship reactor blueprint(s) at (41,45). Size 6x7, rotation 0, covers (39,42)-(44,48)."));
    }

    [Test]
    public void RotatedLargeBlueprintSwapsSizeAndShiftsEvenSides()
    {
        AddShipParts();

        string text = _tools.PlaceBlueprint("ship reactor", "single", 41, 45, 41, 45, 1, "");

        Assert.That(text, Does.Contain("Size 7x6, rotation 1, covers (38,42)-(44,47)."));
    }

    [TestCase(0, 10, 20, 11, 21)]
    [TestCase(1, 10, 19, 11, 20)]
    [TestCase(2, 9, 19, 10, 20)]
    [TestCase(3, 9, 20, 10, 21)]
    public void TwoByTwoFootprintFollowsVanillaCenterRule(int rotation, int minX, int minZ, int maxX, int maxZ)
    {
        var info = new BuildingInfo { Width = 2, Height = 2 };

        CellRectSpec rect = BuildTools.OccupiedRect(10, 20, rotation, info);

        Assert.That((rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ), Is.EqualTo((minX, minZ, maxX, maxZ)));
    }

    [Test]
    public void OneByOneAndOddSizesStayCentered()
    {
        Assert.That(BuildTools.OccupiedRect(5, 5, 3, new BuildingInfo { Width = 1, Height = 1 }).ToString(), Is.EqualTo("(5,5)-(5,5)"));
        Assert.That(BuildTools.OccupiedRect(5, 5, 1, new BuildingInfo { Width = 3, Height = 1 }).ToString(), Is.EqualTo("(5,4)-(5,6)"));
    }

    [Test]
    public void OneByOneThingsKeepTheShortMessage()
    {
        _world.BuildingInfos["Wall"] = new BuildingInfo { DefName = "Wall", Label = "wall" };

        Assert.That(_tools.PlaceBlueprint("wall", "single", 3, 3, 3, 3, 0, ""), Is.EqualTo("Placed 1 wall blueprint(s) at (3,3)."));
    }

    [Test]
    public void SeveralLargeBlueprintsListEachCenterAndRectangle()
    {
        AddShipParts();

        string text = _tools.PlaceBlueprint("Ship_SensorCluster", "line", 10, 10, 20, 10, 0, "");

        Assert.That(text, Does.Contain("Placed 11 ship sensor cluster blueprint(s)").And.Contain("Each is 2x2, rotation 0: (10,10) covers (10,10)-(11,11); (11,10) covers (11,10)-(12,11);"));
        Assert.That(text, Does.Contain("+6 more."));
    }

    [Test]
    public void RefusedLargeBlueprintShowsWhatItWouldCoverAndTheGameReason()
    {
        AddShipParts();
        _world.BlockedCells[(41, 44)] = "Space already occupied (blocked by: ship reactor (blueprint) #77 (39,42)-(44,48))";

        string text = _tools.PlaceBlueprint("Ship_SensorCluster", "single", 41, 44, 41, 44, 0, "");

        Assert.That(text, Does.StartWith("Error: could not place any ship sensor cluster blueprint."));
        Assert.That(text, Does.Contain("(41,44) (would cover (41,44)-(42,45)): Space already occupied (blocked by: ship reactor (blueprint) #77 (39,42)-(44,48))"));
    }

    [Test]
    public void RefusedSmallBlueprintKeepsTheShortFailureText()
    {
        _world.BlockedCells[(12, 20)] = "space already occupied";

        Assert.That(_tools.PlaceBlueprint("wall", "single", 12, 20, 12, 20, 0, ""), Does.Contain("(12,20): space already occupied").And.Not.Contain("would cover"));
    }

    [Test]
    public void BuildingInfoListsSizeCostResearchAndPlacementRules()
    {
        AddShipParts();

        string text = _tools.GetBuildingInfo("ship reactor");

        Assert.That(text, Does.StartWith("ship reactor (Ship_Reactor)"));
        Assert.That(text, Does.Contain("Size: 6x7 (x by z, facing north). Rotation 1 or 3 (east/west) makes it 7x6."));
        Assert.That(text, Does.Contain("place_blueprint x,z is the center cell"));
        Assert.That(text, Does.Contain("Cost: steel x350, plasteel x280"));
        Assert.That(text, Does.Contain("Ground: needs Heavy support under every covered cell."));
        Assert.That(text, Does.Contain("Research: ship reactor (NOT done)"));
        Assert.That(text, Does.Contain("Builder skill: construction 8."));
        Assert.That(text, Does.Contain("Placement: PlaceWorker_NotUnderRoof -> must not be under a roof"));
        Assert.That(text, Does.Contain("Ship part:"));
        Assert.That(text, Does.Contain("Power: produces 1000 W."));
        Assert.That(text, Does.Not.Contain("Interaction cell"));
    }

    [Test]
    public void BuildingInfoCoversMaterialsInteractionCellAndUnknownPlaceWorkers()
    {
        _world.BuildingInfos["Bed"] = new BuildingInfo
        {
            DefName = "Bed",
            Label = "bed",
            Width = 1,
            Height = 2,
            Costs = new[] { new BuildingCost { Label = "cloth", Count = 5 } },
            StuffCount = 40,
            StuffCategories = new[] { "metallic", "woody" },
            AffordanceFromStuff = true,
            TerrainAffordance = "Light",
            PlaceWorkers = new[] { "PlaceWorker_Heater", "Placeworker_AttachedToWall" },
            PowerWatts = 120f,
            InteractionOffsets = new[] { (0, -1) }
        };

        string text = _tools.GetBuildingInfo("Bed");

        Assert.That(text, Does.Contain("Cost: cloth x5, 40 of a material (metallic, woody)"));
        Assert.That(text, Does.Contain("Ground: needs Light support under every covered cell (may depend on the material)."));
        Assert.That(text, Does.Contain("Research: none."));
        Assert.That(text, Does.Contain("Placement: PlaceWorker_Heater; Placeworker_AttachedToWall -> must be attached to a wall"));
        Assert.That(text, Does.Contain("Power: uses 120 W."));
        Assert.That(text, Does.Contain("Interaction cell: offset (0,-1) from the center when facing north"));
        Assert.That(text, Does.Not.Contain("Ship part"));
    }

    [Test]
    public void BuildingInfoOfASquareOrUnitThingSaysRotationKeepsTheFootprint()
    {
        _world.BuildingInfos["Wall"] = new BuildingInfo { DefName = "Wall", Label = "wall" };

        string text = _tools.GetBuildingInfo("wall");

        Assert.That(text, Does.Contain("Size: 1x1 (x by z, facing north). Rotation does not change the footprint."));
        Assert.That(text, Does.Contain("Cost: none"));
        Assert.That(text, Does.Not.Contain("center cell"));
    }

    [Test]
    public void BuildingInfoErrorsForUnknownNamesAndMissingData()
    {
        Assert.That(_tools.GetBuildingInfo("teleporter"), Does.StartWith("Error:").And.Contain("buildable"));
        Assert.That(_tools.GetBuildingInfo("wall"), Does.StartWith("Error:").And.Contain("Wall"));
    }

    [Test]
    public void BuildingInfoIsAnAlwaysLoadedQueryTool()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());

        Assert.That(registry.RiskOf("get_building_info"), Is.EqualTo(ToolRisk.Query));
        Assert.That(registry.Descriptors.Single(d => d.Name == "get_building_info").Category, Is.EqualTo(ToolCategory.Observe));
        Assert.That(ToolPacks.PackOf("get_building_info", ToolCategory.Observe), Is.Null);
    }

    [Test]
    public void BlockedDesignationReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new BuildTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.DesignateArea("mine", false, 0, 0, 10, 10, "steel");

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("mining"));
        Assert.That(_world.Calls, Is.Empty);
        Assert.That(_world.Things.All(t => t.Designations.Count == 0), Is.True);
    }

    [Test]
    public void BlockedBlueprintReturnsPreviewWithoutTouchingWorld()
    {
        var gated = new BuildTools(_world, new ConfirmationGate(() => AgentMode.Confirm));

        string text = gated.PlaceBlueprint("wall", "outline", 3, 3, 6, 6, 0, "");

        Assert.That(text, Does.StartWith("Needs confirmation:").And.Contain("wall"));
        Assert.That(_world.Blueprints, Is.Empty);
    }
}
