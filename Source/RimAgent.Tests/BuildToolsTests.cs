using System.Linq;
using NUnit.Framework;
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
            string text = _tools.PlaceBlueprint("wall", 10, 20, 0, "wood", 5, "east");
            Assert.That(text, Does.StartWith("Placed 4 wall (WoodLog) blueprint(s) starting at (10,20).").And.Contain("(12,20): space already occupied"));
            Assert.That(_world.Blueprints.Select(b => b.X), Is.EqualTo(new[] { 10, 11, 13, 14 }));
            Assert.That(_world.Blueprints.All(b => b.Def == "Wall" && b.Stuff == "WoodLog"), Is.True);
        }

        [Test]
        public void SingleBlueprintWithDefaultStuffIgnoresDirection()
        {
            string text = _tools.PlaceBlueprint("Bed", 3, 3, 1, "", 1, "");
            Assert.That(text, Is.EqualTo("Placed 1 bed blueprint(s) starting at (3,3)."));
            Assert.That(_world.Blueprints.Single().Rotation, Is.EqualTo(1));
            Assert.That(_world.Blueprints.Single().Stuff, Is.Empty);
        }

        [Test]
        public void BlueprintErrorsForUnknownBuildingDirectionRotationAndCount()
        {
            Assert.That(_tools.PlaceBlueprint("teleporter", 3, 3, 0, "", 1, ""), Does.StartWith("Error:").And.Contain("buildable"));
            Assert.That(_tools.PlaceBlueprint("wall", 3, 3, 0, "", 3, "up"), Does.StartWith("Error:").And.Contain("north"));
            Assert.That(_tools.PlaceBlueprint("wall", 3, 3, 7, "", 1, ""), Does.StartWith("Error:").And.Contain("rotation"));
            Assert.That(_tools.PlaceBlueprint("wall", 3, 3, 0, "", 0, ""), Does.StartWith("Error:").And.Contain("count"));
            Assert.That(_world.Blueprints, Is.Empty);
        }

        [Test]
        public void BlueprintEntirelyOutsideMapIsAnError()
        {
            Assert.That(_tools.PlaceBlueprint("wall", 99, 99, 0, "", 3, "east"), Does.StartWith("Placed 1"));
            Assert.That(_tools.PlaceBlueprint("wall", 200, 200, 0, "", 1, ""), Does.StartWith("Error:").And.Contain("outside"));
        }

        [Test]
        public void AmbiguousStuffIsAnError()
        {
            _world.Stuffs = new[] { new DefInfo("WoodLog", "wood"), new DefInfo("WoodPlanks", "wooden planks") };
            Assert.That(_tools.PlaceBlueprint("wall", 3, 3, 0, "woo", 1, ""), Does.StartWith("Error:").And.Contain("WoodLog").And.Contain("WoodPlanks"));
        }
    }
}
