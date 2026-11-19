using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class MapOverviewToolsTests
{
    /// <summary>
    /// 30x30 的測試地圖：東北角 10x10 是岩體（其中兩塊不相連的鋼礦），西側一條 3 格寬的河，
    /// 中央 5x5 肥沃土壤，南緣一排迷霧，其餘一般土壤。
    /// </summary>
    private static MapSurvey MakeSurvey()
    {
        const int w = 30, h = 30;
        var cells = Enumerable.Repeat(SurveyCell.Soil, w * h).ToArray();
        var resource = new short[w * h];
        var roof = new bool[w * h];
        void Set(int x, int z, SurveyCell c) => cells[z * w + x] = c;

        for (int x = 20; x < 30; x++)
            for (int z = 20; z < 30; z++)
            {
                Set(x, z, SurveyCell.Rock);
                roof[z * w + x] = true;
            }
        resource[22 * w + 22] = 1;
        resource[22 * w + 23] = 1;
        resource[27 * w + 27] = 1;

        for (int x = 0; x < 3; x++)
            for (int z = 0; z < h; z++)
                Set(x, z, z < 15 ? SurveyCell.ShallowWater : SurveyCell.DeepWater);

        for (int x = 12; x < 17; x++)
            for (int z = 12; z < 17; z++)
                Set(x, z, SurveyCell.RichSoil);

        for (int x = 0; x < w; x++) Set(x, 0, SurveyCell.Unknown);

        return new MapSurvey
        {
            Width = w,
            Height = h,
            Biome = "Temperate forest",
            Cells = cells,
            Resource = resource,
            ResourceLabels = new[] { "compacted steel" },
            ThickRoof = roof,
            Rooms = new List<RoomInfo>
            {
                new() { Id = 7, Role = "bedroom", CellCount = 16, TemperatureC = 21.4f, Extents = new CellRectSpec(5, 5, 8, 8) },
                new() { Id = 3, Role = "", CellCount = 9, TemperatureC = 15f, Extents = new CellRectSpec(9, 5, 11, 7) },
                new() { Id = 9, Role = "", CellCount = 1, TemperatureC = 15f, Extents = new CellRectSpec(20, 5, 20, 5) }
            },
            Zones = new List<ZoneInfo> { new() { Id = 1, Label = "Growing zone 1", Kind = "Growing", CellCount = 25, Extents = new CellRectSpec(12, 12, 16, 16) } },
            HomeArea = new CellRectSpec(4, 4, 12, 10),
            HomeAreaCells = 63,
            Colonists = new List<(int X, int Z)> { (6, 6), (8, 8) },
            Hostiles = new List<(int X, int Z)>()
        };
    }

    [Test]
    public void SummarisesCompositionBaseRoomsZonesAndClusters()
    {
        var world = new FakeGameWorld { MapSurvey = MakeSurvey() };
        string text = new MapOverviewTools(world).GetMapOverview();

        Assert.That(text, Does.StartWith("Map 30x30, Temperate forest. Explored 97% of the map"));
        Assert.That(text, Does.Contain("natural rock 11%"));
        Assert.That(text, Does.Contain("Thick mountain roof covers 11% of it."));
        Assert.That(text, Does.Contain("Home area: (4,4)-(12,10), 63 cells, south-west."));
        Assert.That(text, Does.Contain("Colonists: 2 within (6,6)-(8,8), centred near (7,7) south-west."));
        Assert.That(text, Does.Contain("Visible hostiles: none."));
        Assert.That(text, Does.Contain("- bedroom #7: (5,5)-(8,8), 16 cells, 21°C"));
        Assert.That(text, Does.Contain("- room #3: (9,5)-(11,7), 9 cells, 15°C"), "a room without a role is called room");
        Assert.That(text, Does.Contain("Indoor rooms (2; 1 enclosed spaces under 4 cells left out):"));
        Assert.That(text, Does.Not.Contain("#9"), "one-cell ruin spaces are only counted");
        Assert.That(text, Does.Contain("- Growing \"Growing zone 1\" #1: (12,12)-(16,16), 25 cells"));
        Assert.That(text, Does.Contain("Ore veins (2):"));
        Assert.That(text, Does.Contain("- compacted steel: 2 cells, (22,22)-(23,22), north-east"));
        Assert.That(text, Does.Contain("- compacted steel: 1 cell, (27,27)-(27,27), north-east"));
        Assert.That(text, Does.Contain("Water bodies (20+ cells) (1):"), "shallow and deep water join into one body");
        Assert.That(text, Does.Contain("- water: 87 cells, (0,1)-(2,29), west"));
        Assert.That(text, Does.Contain("- rich soil: 25 cells, (12,12)-(16,16), centre"));
        Assert.That(text, Does.Contain("Large rock masses (200+ explored cells) (0): none explored."));
    }

    [Test]
    public void FoggedCellsAreNotRevealed()
    {
        MapSurvey survey = MakeSurvey();
        // 迷霧中的礦脈：世界層不會填 Resource，Cells 是 Unknown。
        for (int i = 0; i < survey.Cells.Length; i++) survey.Cells[i] = SurveyCell.Unknown;
        survey.Resource = new short[survey.Cells.Length];
        survey.ThickRoof = new bool[survey.Cells.Length];

        string text = MapOverviewTools.Format(survey);

        Assert.That(text, Does.Contain("Explored 0% of the map"));
        Assert.That(text, Does.Not.Contain("Explored ground:"));
        Assert.That(text, Does.Contain("Ore veins (0): none explored."));
    }

    [Test]
    public void DirectionsSplitTheMapIntoThirds()
    {
        var survey = new MapSurvey { Width = 90, Height = 90 };
        Assert.That(MapOverviewTools.Direction(survey, 10, 80), Is.EqualTo("north-west"));
        Assert.That(MapOverviewTools.Direction(survey, 45, 80), Is.EqualTo("north"));
        Assert.That(MapOverviewTools.Direction(survey, 45, 45), Is.EqualTo("centre"));
        Assert.That(MapOverviewTools.Direction(survey, 80, 45), Is.EqualTo("east"));
        Assert.That(MapOverviewTools.Direction(survey, 80, 5), Is.EqualTo("south-east"));
    }

    [Test]
    public void ToolIsAResidentQueryInTheObserveCategory()
    {
        var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());
        Assert.That(registry.RiskOf("get_map_overview"), Is.EqualTo(ToolRisk.Query));
        Assert.That(ToolPacks.PackOf("get_map_overview", ToolCategory.Observe), Is.Null, "觀察類工具常駐，不放進工具包");
    }
}
