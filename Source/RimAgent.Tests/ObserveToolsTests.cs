using System.Collections.Generic;
using NUnit.Framework;
using RimAgent.Agent;
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
        public void OverviewHasKeyNumbersPowerAndAlerts()
        {
            _world.Power = new PowerInfo { NetworkCount = 2, NetWatts = -150f, StoredEnergy = 400f };
            _world.Alerts.Add(new AlertInfo { Label = "Need rescue", Priority = "Critical" });

            string text = _tools.GetColonyOverview();

            Assert.That(text, Does.Contain("New Arrivals").And.Contain("Colonists: 2").And.Contain("Spring day 3").And.Contain("21.4°C"));
            Assert.That(text, Does.Contain("Power: 2 network(s), net watts -150, stored battery energy 400"));
            Assert.That(text, Does.Contain("Alerts: [Critical] Need rescue"));
        }

        [Test]
        public void ColonistsListIdsPositionsAndPassions()
        {
            string text = _tools.ListColonists();
            Assert.That(text, Does.Contain("[101] Ali").And.Contain("(12,34)").And.Contain("shooting 8**").And.Contain("medical 5*").And.Contain("plants (disabled)"));
            Assert.That(text, Does.Contain("[102] Bob"));
        }

        [Test]
        public void UnreadablePartsAreFlaggedInsteadOfLookingEmpty()
        {
            _world.Colonists[1].UnreadableParts = new[] { "skills", "needs" };

            Assert.That(_tools.ListColonists(), Does.Contain("could not read skills, needs"));
            Assert.That(_tools.GetColonistWellbeing("Bob"), Does.Contain("could not read skills, needs"));
            Assert.That(_tools.GetColonistWellbeing("Ali"), Does.Not.Contain("could not read"));
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

        [Test]
        public void TakeScreenshotSucceedsWithDefaultTimestampAndCustomName()
        {
            string defaultText = _tools.TakeScreenshot();
            Assert.That(defaultText, Does.StartWith("Screenshot saved:").And.Match(@"RimAgent_\d{8}_\d{6}\.png").And.Contain("1920x1080"));
            Assert.That(_world.Calls.Exists(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"TakeScreenshot\(RimAgent_\d{8}_\d{6}\)")), Is.True);

            string customText = _tools.TakeScreenshot("colony_base");
            Assert.That(customText, Does.StartWith("Screenshot saved:").And.Contain("colony_base.png").And.Contain("1920x1080"));
            Assert.That(_world.Calls, Does.Contain("TakeScreenshot(colony_base)"));
        }

        [Test]
        public void ResolveScreenshotNameSanitizesAndFallsBackToTimestamp()
        {
            Assert.That(ObserveTools.ResolveScreenshotName(null), Does.Match(@"^RimAgent_\d{8}_\d{6}$"));
            Assert.That(ObserveTools.ResolveScreenshotName("   "), Does.Match(@"^RimAgent_\d{8}_\d{6}$"));
            Assert.That(ObserveTools.ResolveScreenshotName("my_base.png"), Is.EqualTo("my_base"));
            Assert.That(ObserveTools.ResolveScreenshotName("folder/sub/base"), Is.EqualTo("base"));
            Assert.That(ObserveTools.ResolveScreenshotName(@"C:\path\to\shot.png"), Is.EqualTo("shot"));
            Assert.That(ObserveTools.ResolveScreenshotName("test|shot?*"), Is.EqualTo("test_shot__"));
            Assert.That(ObserveTools.ResolveScreenshotName("???"), Does.Match(@"^RimAgent_\d{8}_\d{6}$"));
        }

        [Test]
        public void TakeScreenshotReturnsErrorOnFailure()
        {
            _world.ScreenshotResult = new ScreenshotInfo
            {
                Success = false,
                Error = "disk full"
            };

            string text = _tools.TakeScreenshot("test");
            Assert.That(text, Does.StartWith("Error:").And.Contain("disk full"));
        }

        [Test]
        public void AskModeExcludesSetCurrentMapAndTakeScreenshot()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());
            var tools = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(System.Linq.Enumerable.OfType<Microsoft.Extensions.AI.AIFunction>(registry.Select(ToolPermissions.All, AgentMode.Ask)), f => f.Name));
            Assert.That(tools, Does.Not.Contain("set_current_map"));
            Assert.That(tools, Does.Not.Contain("take_screenshot"));
            Assert.That(tools, Does.Contain("get_colony_overview"));
        }

        [Test]
        public void BlockedGateReturnsPreviewForMapAndScreenshot()
        {
            _world.Maps.Add(new MapInfo { Id = 2, Label = "Outpost" });
            var gate = new ConfirmationGate(() => AgentMode.Ask);
            var tools = new ObserveTools(_world, gate);

            string mapResult = tools.SetCurrentMap("2");
            Assert.That(mapResult, Does.StartWith(ToolText.ConfirmationPrefix + "would switch current map to"));
            Assert.That(_world.CurrentMapId, Is.EqualTo(1));

            string shotResult = tools.TakeScreenshot("test");
            Assert.That(shotResult, Does.StartWith(ToolText.ConfirmationPrefix + "would take screenshot"));
            Assert.That(_world.Calls, Does.Not.Contain("TakeScreenshot(test)"));
        }

        [Test]
        public void CellInfoShowsTerrainRoofZoneRoomAndObjects()
        {
            _world.Cells[(5, 5)] = new CellInfo
            {
                X = 5, Z = 5, TerrainDefName = "Gravel", Terrain = "gravel", Fertility = 0.7f, Roof = "overhead mountain", Walkable = false,
                Zone = new ZoneInfo { Id = 50, Label = "Stockpile 1", Kind = "Stockpile", CellCount = 9 },
                Room = new RoomInfo { Id = 12, Role = "bedroom", CellCount = 20, Outdoors = false, TemperatureC = 18.5f, Extents = new CellRectSpec(4, 4, 8, 7) },
                Things = new[] { _world.Things[0] }
            };

            string text = _tools.InspectArea(5, 5, 5, 5);

            Assert.That(text, Does.Contain("Cell (5,5): gravel (Gravel), fertility 70%, roof: overhead mountain, NOT walkable"));
            Assert.That(text, Does.Contain("Zone: [50] Stockpile 1 (Stockpile, 9 cells)"));
            Assert.That(text, Does.Contain("Room: #12 bedroom, 20 cells, extents (4,4)-(8,7), 18.5°C, indoors"));
            Assert.That(text, Does.Contain("Objects on cell: [1] compacted steel (MineableSteel) at (5,5)"));
        }

        [Test]
        public void CellInfoDefaultsAndOutOfBounds()
        {
            string text = _tools.InspectArea(20, 20, 20, 20);
            Assert.That(text, Does.Contain("soil (Soil), fertility 100%, roof: none, walkable"));
            Assert.That(text, Does.Contain("Zone: none").And.Contain("outdoors").And.Contain("Objects on cell: none"));

            Assert.That(_tools.InspectArea(100, 5, 100, 5), Does.StartWith("Error:"));
        }

        [Test]
        public void InspectAreaSummarisesTerrainObstaclesZonesAndRooms()
        {
            _world.UnwalkableCells.Add((7, 7));
            _world.Cells[(7, 7)] = new CellInfo { X = 7, Z = 7, TerrainDefName = "Marsh", Terrain = "marsh", Fertility = 0.5f, Roof = "constructed", Walkable = false, Room = _world.Outdoors };
            _world.Cells[(8, 8)] = new CellInfo
            {
                X = 8, Z = 8, TerrainDefName = "Soil", Terrain = "soil", Fertility = 1f, Walkable = true,
                Zone = new ZoneInfo { Id = 50, Label = "Stockpile 1", Kind = "Stockpile", CellCount = 9 },
                Room = new RoomInfo { Id = 12, Role = "bedroom", CellCount = 20, Outdoors = false, TemperatureC = 18f, Extents = new CellRectSpec(8, 8, 12, 12) }
            };

            string text = _tools.InspectArea(9, 9, 5, 5);

            Assert.That(text, Does.Contain("Area (5,5)-(9,9): 25 cells | terrain: soil 24, marsh 1 | fertile (>=70%): 24 | roofed: 1 | not walkable: 1"));
            // 兩顆礦石與一棵樹在範圍內；(40,40) 那顆不在。
            Assert.That(text, Does.Contain("Obstacles: 2 — [1] compacted steel (MineableSteel) at (5,5); [3] oak tree (Plant_TreeOak) at (6,5) harvestable"));
            Assert.That(text, Does.Contain("Zones: [50] Stockpile 1 (Stockpile, 1 cells inside)"));
            Assert.That(text, Does.Contain("Rooms: #0 none, 10000 cells").And.Contain("outdoors (24 cells inside)").And.Contain("#12 bedroom").And.Contain("indoors (1 cells inside)"));
        }

        [Test]
        public void InspectAreaRejectsOversizedAndOutOfMapRectangles()
        {
            Assert.That(_tools.InspectArea(0, 0, 50, 50), Does.StartWith("Error:").And.Contain("2601 cells"));
            Assert.That(_tools.InspectArea(0, 0, 49, 49), Does.StartWith("Area (0,0)-(49,49): 2500 cells"));
            Assert.That(_tools.InspectArea(0, 0, 100, 5), Does.StartWith("Error:"));
        }
    }
}

