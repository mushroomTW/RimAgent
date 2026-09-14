using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class ObserveToolsTests
    {
        private FakeGameWorld _world;
        private ObserveTools _tools;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith", 12, 34));
            _world.Colonists.Add(FakeGameWorld.MakeColonist(102, "Bob", "", "Jones"));
            _world.Things.Add(FakeGameWorld.MakeThing(1, "MineableSteel", "compacted steel", ThingKind.Mineable, 5, 5));
            _world.Things.Add(FakeGameWorld.MakeThing(2, "MineableSteel", "compacted steel", ThingKind.Mineable, 40, 40));
            _world.Things.Add(FakeGameWorld.MakeThing(3, "Plant_TreeOak", "oak tree", ThingKind.Plant, 6, 5, harvestable: true));
            _tools = new ObserveTools(_world);
        }

        [Test]
        public void OverviewIsOneLineWithKeyNumbers()
        {
            string text = _tools.GetColonyOverview();
            Assert.That(text, Does.Contain("New Arrivals").And.Contain("Colonists: 2").And.Contain("Spring day 3").And.Contain("21.4°C"));
            Assert.That(text, Does.Not.Contain("\n"));
        }

        [Test]
        public void ColonistsListIdsPositionsAndPassions()
        {
            string text = _tools.ListColonists();
            Assert.That(text, Does.Contain("[101] Ali").And.Contain("(12,34)").And.Contain("shooting 8**").And.Contain("medical 5*").And.Contain("plants (disabled)"));
            Assert.That(text, Does.Contain("[102] Bob"));
        }

        [Test]
        public void ResourcesGroupedByCategoryWithNutrition()
        {
            _world.Resources = new ResourceSnapshot
            {
                TotalHumanEdibleNutrition = 42.5f,
                Items = new[]
                {
                    new ResourceInfo { DefName = "Steel", Label = "steel", Count = 300, Category = "Resources" },
                    new ResourceInfo { DefName = "MealSimple", Label = "simple meal", Count = 12, Category = "Food" }
                }
            };
            string text = _tools.GetResources();
            Assert.That(text, Does.Contain("42.5").And.Contain("Resources: steel (Steel) x300").And.Contain("Food: simple meal (MealSimple) x12"));
        }

        [Test]
        public void ThreatsEmptyAndListed()
        {
            Assert.That(_tools.ListThreats(), Is.EqualTo("No threats on the map."));
            _world.Threats.Add(new ThreatInfo { Id = 900, Label = "pirate", Kind = "HostilePawn", Faction = "Pirates", X = 3, Z = 4 });
            Assert.That(_tools.ListThreats(), Does.Contain("[900] pirate (HostilePawn, Pirates) at (3,4)"));
        }

        [Test]
        public void ResearchShowsCurrentAndAvailable()
        {
            _world.Research = new ResearchInfo
            {
                Current = new ResearchProjectInfo { DefName = "Electricity", Label = "electricity", ProgressPercent = 0.45f, Cost = 1600 },
                Available = new[] { new ResearchProjectInfo { DefName = "Batteries", Label = "batteries", Cost = 400 } }
            };
            string text = _tools.GetResearchStatus();
            Assert.That(text, Does.Contain("electricity (Electricity) 45% of 1600").And.Contain("batteries (Batteries, cost 400)"));
        }

        [Test]
        public void SetResearchSwitchesAndReportsPreviousAndNew()
        {
            _world.Research = new ResearchInfo
            {
                Current = new ResearchProjectInfo { DefName = "Electricity", Label = "electricity", Cost = 1600 },
                Available = new[] { new ResearchProjectInfo { DefName = "Batteries", Label = "batteries", Cost = 400 } }
            };

            string text = _tools.SetResearch("batteries");

            Assert.That(text, Does.Contain("electricity (Electricity) -> batteries (Batteries)"));
            Assert.That(_world.Research.Current.DefName, Is.EqualTo("Batteries"));
        }

        [Test]
        public void SetResearchUnknownNameIsAnError()
        {
            _world.Research = new ResearchInfo
            {
                Available = new[] { new ResearchProjectInfo { DefName = "Batteries", Label = "batteries", Cost = 400 } }
            };
            Assert.That(_tools.SetResearch("warp drive"), Does.StartWith("Error:"));
            Assert.That(_world.Research.Current, Is.Null);
        }

        [Test]
        public void SetResearchNoneStopsTheCurrentProject()
        {
            _world.Research = new ResearchInfo
            {
                Current = new ResearchProjectInfo { DefName = "Electricity", Label = "electricity", Cost = 1600 },
                Available = new ResearchProjectInfo[0]
            };

            string text = _tools.SetResearch("none");

            Assert.That(text, Does.Contain("electricity (Electricity) -> none"));
            Assert.That(_world.Research.Current, Is.Null);
        }

        [Test]
        public void MapsListCurrentMarkerAndSwitchingChangesCurrent()
        {
            _world.Maps.Add(new MapInfo { Id = 2, Label = "Mining outpost", Tile = 777, ColonistCount = 1 });

            string listed = _tools.GetMaps();
            Assert.That(listed, Does.Contain("[1] New Arrivals").And.Contain("(current)").And.Contain("[2] Mining outpost"));

            string switched = _tools.SetCurrentMap("2");
            Assert.That(switched, Does.Contain("Mining outpost"));
            Assert.That(_world.CurrentMapId, Is.EqualTo(2));
            Assert.That(_tools.GetMaps(), Does.Contain("[2] Mining outpost, tile 777, colonists 1 (current)"));
        }

        [Test]
        public void SetCurrentMapUnknownIdIsAnError()
        {
            Assert.That(_tools.SetCurrentMap("99"), Does.StartWith("Error:"));
            Assert.That(_world.CurrentMapId, Is.EqualTo(1));
        }

        [Test]
        public void FindThingsWholeMapWithNameFilter()
        {
            string text = _tools.FindThings("mineable", "steel", -1, -1, -1, -1, 20);
            Assert.That(text, Does.Contain("Found 2").And.Contain("[1] compacted steel (MineableSteel) at (5,5)").And.Contain("[2]"));
        }

        [Test]
        public void FindThingsRestrictedToRectangle()
        {
            string text = _tools.FindThings("any", "", 0, 0, 10, 10, 20);
            Assert.That(text, Does.Contain("[1]").And.Contain("[3]").And.Not.Contain("[2]"));
            Assert.That(text, Does.Contain("harvestable"));
        }

        [Test]
        public void FindThingsUnknownKindIsAnError()
        {
            Assert.That(_tools.FindThings("monster", "", -1, -1, -1, -1, 20), Does.StartWith("Error:").And.Contain("mineable"));
        }

        [Test]
        public void FindThingsNothingFoundSaysWhere()
        {
            Assert.That(_tools.FindThings("building", "", 0, 0, 10, 10, 20), Does.Contain("No building").And.Contain("(0,0)-(10,10)"));
        }

        [Test]
        public void FindThingsRectangleOutsideMapIsAnError()
        {
            Assert.That(_tools.FindThings("any", "", 0, 0, 500, 10, 20), Does.StartWith("Error:"));
        }
    }
}
