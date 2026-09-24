using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
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
            Assert.That(_world.Calls, Is.EqualTo(new[] { "SetForbidden(map,,False)" }));
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
            Assert.That(_world.Calls, Is.EqualTo(new[] { "SetForbidden((0,0)-(10,10),steel,True)" }));
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
            Assert.That(_world.Calls, Is.EqualTo(new[] { "AddDesignationOnCell(6,5,Mine)" }));
            Assert.That(_world.Things[1].Designations, Does.Contain("Mine"));
            Assert.That(_world.Things[2].Designations, Is.Empty, "granite 不符合 steel 過濾");
        }

        [Test]
        public void HarvestOnlyMarksRipePlants()
        {
            string text = _tools.DesignateArea("harvest", false, 0, 0, 10, 10, "");
            Assert.That(text, Does.Contain("Marked 1 for harvesting").And.Contain("1 not applicable"));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "AddDesignationOnThing(5,HarvestPlant)" }));
        }

        [Test]
        public void DeconstructOnlyPlayerBuildings()
        {
            string text = _tools.DesignateArea("deconstruct", false, 0, 0, 10, 10, "");
            Assert.That(text, Does.Contain("Marked 1 for deconstruction"));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "AddDesignationOnThing(7,Deconstruct)" }));
        }

        [Test]
        public void SelectionIsUsedWhenRequested()
        {
            _world.Selection.Add(_world.Things[3]);
            _world.Selection.Add(_world.Things[4]);
            string text = _tools.DesignateArea("mine", true, 0, 0, 0, 0, "");
            Assert.That(text, Is.EqualTo("Marked 1 for mining."));
            Assert.That(_world.Calls, Is.EqualTo(new[] { "AddDesignationOnCell(50,50,Mine)" }));
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
            Assert.That(_world.Blueprints.Select(b => b.X), Is.EqualTo(new[] { 10, 11, 13, 14 }));
            Assert.That(_world.Blueprints.All(b => b.Def == "Wall" && b.Stuff == "WoodLog"), Is.True);
        }

        [Test]
        public void BlueprintLineWalksFromTheStartTowardTheEnd()
        {
            _tools.PlaceBlueprint("wall", "line", 5, 9, 5, 7, 0, "");
            Assert.That(_world.Blueprints.Select(b => b.Z), Is.EqualTo(new[] { 9, 8, 7 }));
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
}
