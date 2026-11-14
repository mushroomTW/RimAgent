using System;
using System.Linq;
using NUnit.Framework;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

[TestFixture]
public class MapGridToolsTests
{
    private FakeGameWorld _world;
    private MapGridTools _tools;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _tools = new MapGridTools(_world);
    }

    private static string[] Lines(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>某一列（以 z 標籤開頭）去掉標籤後的字元。</summary>
    private static string Row(string text, int z)
    {
        string prefix = "z" + z + " ";
        string line = Lines(text).First(l => l.TrimStart([' ']).StartsWith(prefix, StringComparison.Ordinal));
        return line.Substring(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length);
    }

    private void Set(int x, int z, Action<MapGridCell> edit)
    {
        if (!_world.GridCells.TryGetValue((x, z), out MapGridCell cell)) _world.GridCells[(x, z)] = cell = new MapGridCell();
        edit(cell);
    }

    [Test]
    public void RendersRoomWithAxesAndLegend()
    {
        // 房間：牆圍 x10-15、z20-23，門在 (12,20)；室內有殖民者、床、儲藏區、地板、物品。
        for (int x = 10; x <= 15; x++)
        {
            Set(x, 23, c => c.Building = GridBuilding.Wall);
            Set(x, 20, c => c.Building = GridBuilding.Wall);
        }
        for (int z = 21; z <= 22; z++)
        {
            Set(10, z, c => c.Building = GridBuilding.Wall);
            Set(15, z, c => c.Building = GridBuilding.Wall);
        }
        Set(12, 20, c => c.Building = GridBuilding.Door);
        Set(11, 22, c => c.Pawn = GridPawn.Colonist);
        Set(14, 22, c => c.Building = GridBuilding.Other);
        Set(11, 21, c => c.Zone = GridZone.Stockpile);
        Set(12, 21, c => c.Zone = GridZone.Stockpile);
        Set(13, 21, c => c.Terrain = GridTerrain.Floor);
        Set(14, 21, c => c.HasItem = true);
        Set(9, 24, c => c.Plant = GridPlant.Tree);
        Set(16, 24, c => c.Building = GridBuilding.Blueprint);
        Set(17, 24, c => c.Building = GridBuilding.Frame);
        Set(16, 22, c => c.Zone = GridZone.Growing);
        Set(17, 22, c => c.Plant = GridPlant.Other);
        Set(9, 25, c => c.Pawn = GridPawn.Other);
        Set(8, 19, c => c.Terrain = GridTerrain.ShallowWater);
        Set(16, 19, c => c.Terrain = GridTerrain.DeepWater);
        Set(17, 19, c => c.Terrain = GridTerrain.DeepWater);
        Set(8, 18, c => c.Rock = true);
        Set(9, 18, c => c.Rock = true);
        Set(13, 18, c => c.Pawn = GridPawn.Hostile);

        string text = _tools.ViewMapGrid(8, 18, 17, 25);

        Assert.That(Lines(text), Is.EqualTo(
        [
            "Map grid (8,18)-(17,25): 10x8 cells. Rows run north (top, highest z) to south, columns west to east.",
            "      10   15",
            "      |    |",
            "z25 .o........",
            "z24 .T......bf",
            "z23 ..######..",
            "z22 ..#@..B#g*",
            "z21 ..#ss_i#..",
            "z20 ..##+###..",
            "z19 ~.......WW",
            "z18 ^^...!....",
            "      10   15",
            "      |    |",
            "Legend: ! hostile pawn, @ colonist, o other pawn (animal, guest, prisoner, neutral), # wall, + door, " +
            "B other building, f frame (under construction), b blueprint (planned), i item, ^ natural rock, " +
            "W deep water (impassable), ~ shallow water, T tree, * other plant (crop, bush, flower), " +
            "s stockpile zone, g growing zone, _ constructed floor, . bare ground"
        ]));
    }

    [Test]
    public void LegendListsOnlyCharactersThatAppear()
    {
        Set(5, 5, c => c.Building = GridBuilding.Wall);

        string text = _tools.ViewMapGrid(4, 4, 6, 6);

        string legend = Lines(text).Last();
        Assert.That(legend, Is.EqualTo("Legend: # wall, . bare ground"));
    }

    [Test]
    public void OverlappingLayersFollowPriorityOrder()
    {
        // 同一格疊滿所有圖層，從最優先的往下逐層拿掉，字元要依序換成下一層。
        var cell = new MapGridCell
        {
            Fogged = true,
            Pawn = GridPawn.Hostile,
            Building = GridBuilding.Wall,
            HasItem = true,
            Rock = true,
            Terrain = GridTerrain.DeepWater,
            Plant = GridPlant.Tree,
            Zone = GridZone.Stockpile,
            Roof = GridRoof.Constructed
        };
        _world.GridCells[(3, 3)] = cell;
        string Draw() => Row(_tools.ViewMapGrid(3, 3, 3, 3), 3);

        Assert.That(Draw(), Is.EqualTo("?"), "迷霧蓋過一切");
        cell.Fogged = false;
        Assert.That(Draw(), Is.EqualTo("!"));
        cell.Pawn = GridPawn.Colonist;
        Assert.That(Draw(), Is.EqualTo("@"));
        cell.Pawn = GridPawn.Other;
        Assert.That(Draw(), Is.EqualTo("o"));
        cell.Pawn = GridPawn.None;
        Assert.That(Draw(), Is.EqualTo("#"));
        cell.Building = GridBuilding.Door;
        Assert.That(Draw(), Is.EqualTo("+"));
        cell.Building = GridBuilding.Turret;
        Assert.That(Draw(), Is.EqualTo("t"));
        cell.Building = GridBuilding.Cover;
        Assert.That(Draw(), Is.EqualTo("c"));
        cell.Building = GridBuilding.Other;
        Assert.That(Draw(), Is.EqualTo("B"));
        cell.Building = GridBuilding.Frame;
        Assert.That(Draw(), Is.EqualTo("f"));
        cell.Building = GridBuilding.Blueprint;
        Assert.That(Draw(), Is.EqualTo("b"));
        cell.Building = GridBuilding.None;
        Assert.That(Draw(), Is.EqualTo("i"));
        cell.HasItem = false;
        Assert.That(Draw(), Is.EqualTo("^"));
        cell.Rock = false;
        Assert.That(Draw(), Is.EqualTo("W"));
        cell.Terrain = GridTerrain.ShallowWater;
        Assert.That(Draw(), Is.EqualTo("~"));
        cell.Terrain = GridTerrain.Floor;
        Assert.That(Draw(), Is.EqualTo("T"));
        cell.Plant = GridPlant.Other;
        Assert.That(Draw(), Is.EqualTo("*"));
        cell.Plant = GridPlant.None;
        Assert.That(Draw(), Is.EqualTo("s"), "區域壓過地板");
        cell.Zone = GridZone.Growing;
        Assert.That(Draw(), Is.EqualTo("g"));
        cell.Zone = GridZone.None;
        Assert.That(Draw(), Is.EqualTo("_"));
        cell.Terrain = GridTerrain.Ground;
        Assert.That(Draw(), Is.EqualTo("."), "屋頂不影響地圖層");
    }

    [Test]
    public void RowsRunNorthToSouthAndLabelsMatchCoordinates()
    {
        Set(21, 34, c => c.Pawn = GridPawn.Colonist); // 左上角（x 最小、z 最大）
        Set(30, 30, c => c.Pawn = GridPawn.Hostile); // 右下角

        string[] lines = Lines(_tools.ViewMapGrid(30, 30, 21, 34));

        // 標籤：x 每 5 格一次（25、30），z 標籤在列首且由大到小。
        Assert.That(lines[1], Is.EqualTo("        25   30"));
        Assert.That(lines[2], Is.EqualTo("        |    |"));
        string[] rows = [.. lines.Where(l => l.StartsWith("z", StringComparison.Ordinal))];
        Assert.That(rows.Select(r => r.Substring(0, 3)), Is.EqualTo(["z34", "z33", "z32", "z31", "z30"]));
        Assert.That(rows[0], Is.EqualTo("z34 @........."), "(21,34) 在左上");
        Assert.That(rows[4], Is.EqualTo("z30 .........!"), "(30,30) 在右下");
    }

    [Test]
    public void ThreeDigitAndZeroLabelsAlignWithTheirColumn()
    {
        _world.Bounds = new MapBounds { Width = 250, Height = 250 };

        string[] lines = Lines(_tools.ViewMapGrid(98, 8, 106, 10));

        // x=100 的 '1' 對準索引 2 的欄，x=105 的 '1' 對準索引 7 的欄；z 標籤右對齊。
        Assert.That(lines[1], Is.EqualTo("      100  105"));
        Assert.That(lines[2], Is.EqualTo("      |    |"));
        Assert.That(lines[3], Is.EqualTo("z10 ........."));
        Assert.That(lines[4], Is.EqualTo(" z9 ........."));

        string[] zero = Lines(_tools.ViewMapGrid(0, 0, 6, 0));
        Assert.That(zero[1], Is.EqualTo("   0    5"));
    }

    [Test]
    public void CropsRectanglesOverTheCellBudget()
    {
        string tooBig = _tools.ViewMapGrid(0, 0, 50, 49);
        Assert.That(tooBig, Does.StartWith("Note:").And.Contain("2550 cells").And.Contain("2500"));
        Assert.That(tooBig, Does.Contain("at most 50 cells per side").And.Contain("corners are inclusive").And.Contain("showing (0,0)-(49,49)"));
        Assert.That(tooBig, Does.Contain("Map grid (0,0)-(49,49): 50x50 cells"));

        string full = _tools.ViewMapGrid(0, 0, 49, 49);
        Assert.That(full, Does.StartWith("Map grid (0,0)-(49,49): 50x50 cells"));
        Assert.That(Lines(full).Count(l => l.TrimStart([' ']).StartsWith("z", StringComparison.Ordinal)), Is.EqualTo(50));
        Assert.That(full.Length, Is.LessThan(ToolOutputLimit.MaxResultChars), "滿額也不能被工具結果上限截斷");
    }

    [Test]
    public void InclusiveCornersFiftyOneBySquareIsCroppedWithNote()
    {
        // 模型實際犯過多次的錯：(0,0)-(50,50) 看似 50x50，其實角落含頭尾是 51x51 = 2601 格。
        // 報錯會白白浪費一輪，改為裁成 50x50 並說明。
        string text = _tools.ViewMapGrid(0, 0, 50, 50);

        Assert.That(text, Does.StartWith("Note:").And.Contain("2601 cells").And.Contain("corners are inclusive").And.Contain("showing (0,0)-(49,49)"));
        Assert.That(text, Does.Contain("Map grid (0,0)-(49,49): 50x50 cells"));
        Assert.That(_tools.ViewMapGrid(0, 0, 50, 50, "build"), Does.Contain("Build grid (0,0)-(49,49)"));
    }

    [Test]
    public void ClampsRectanglesThatReachOutsideTheMapAndSaysSo()
    {
        Set(0, 0, c => c.Building = GridBuilding.Wall);

        string text = _tools.ViewMapGrid(-5, -5, 3, 2);

        Assert.That(_world.GridRequests.Single().ToString(), Is.EqualTo("(0,0)-(3,2)"));
        Assert.That(text, Does.Contain("Map grid (0,0)-(3,2): 4x3 cells"));
        Assert.That(text, Does.Contain("Requested (-5,-5)-(3,2) reaches outside the map (valid x is 0-99, valid z is 0-99); clamped to (0,0)-(3,2)."));
        Assert.That(Row(text, 0), Is.EqualTo("#..."));

        string east = _tools.ViewMapGrid(98, 98, 120, 120);
        Assert.That(east, Does.Contain("Map grid (98,98)-(99,99): 2x2 cells").And.Contain("clamped to (98,98)-(99,99)"));

        string inside = _tools.ViewMapGrid(1, 1, 2, 2);
        Assert.That(inside, Does.Not.Contain("clamped"));
    }

    [Test]
    public void ClampingAppliesBeforeTheSizeCap()
    {
        // 要求的矩形遠大於上限：先裁到地圖內（-100..149 → 0..99，10000 格），再裁成 50x50。
        Assert.That(_tools.ViewMapGrid(-100, -100, 149, 149), Does.StartWith("Note:").And.Contain("10000 cells").And.Contain("showing (0,0)-(49,49)"));
        // 反過來：要求的矩形帶了很大的越界範圍，裁完在上限內就畫。
        Assert.That(_tools.ViewMapGrid(-1000, 0, 9, 4), Does.StartWith("Map grid (0,0)-(9,4): 10x5 cells"));
    }

    [Test]
    public void RectangleCompletelyOutsideTheMapIsAnError()
    {
        string text = _tools.ViewMapGrid(200, 200, 210, 210);

        Assert.That(text, Does.StartWith("Error:").And.Contain("outside the map").And.Contain("0-99"));
        Assert.That(_world.GridRequests, Is.Empty);
    }

    [Test]
    public void CornersInAnyOrderGiveTheSameGrid()
    {
        Set(5, 7, c => c.Pawn = GridPawn.Colonist);

        Assert.That(_tools.ViewMapGrid(9, 9, 4, 6), Is.EqualTo(_tools.ViewMapGrid(4, 6, 9, 9)));
    }

    [Test]
    public void RoofLayerShowsRoofsWithWallsAndDoorsForOrientation()
    {
        Set(1, 1, c => c.Building = GridBuilding.Wall);
        Set(2, 1, c => { c.Building = GridBuilding.Door; c.Roof = GridRoof.Constructed; });
        Set(3, 1, c => c.Roof = GridRoof.Constructed);
        Set(4, 1, c => c.Roof = GridRoof.Natural);
        Set(5, 1, c => { c.Roof = GridRoof.Mountain; c.Rock = true; });
        Set(6, 1, c => c.Pawn = GridPawn.Colonist);
        Set(7, 1, c => c.Fogged = true);

        string text = _tools.ViewMapGrid(1, 1, 8, 1, "Roof");

        Assert.That(text, Does.StartWith("Roof grid (1,1)-(8,1)"));
        Assert.That(Row(text, 1), Is.EqualTo("#+rnM.?."));
        Assert.That(Lines(text).Last(), Is.EqualTo(
            "Legend: ? unexplored (fogged), # wall, + door, M thick rock roof (overhead mountain, cannot be removed), " +
            "n thin natural rock roof, r constructed roof, . no roof"));
    }

    [Test]
    public void BuildLayerShowsBlockedCellsAndTheStrongestTerrainSupport()
    {
        Set(1, 1, c => c.BuildSupport = GridAffordance.Heavy);
        Set(2, 1, c => c.BuildSupport = GridAffordance.Medium);
        Set(3, 1, c => c.BuildSupport = GridAffordance.Light);
        Set(4, 1, c => c.BuildSupport = GridAffordance.None);
        Set(5, 1, c => { c.BuildSupport = GridAffordance.Heavy; c.Blocked = true; c.Building = GridBuilding.Wall; });
        Set(6, 1, c => { c.BuildSupport = GridAffordance.Heavy; c.Blocked = true; c.Rock = true; });
        Set(7, 1, c => { c.BuildSupport = GridAffordance.Heavy; c.Fogged = true; c.Blocked = true; });
        // 地圖層的東西（殖民者、物品）不影響可建性。
        Set(8, 1, c => { c.BuildSupport = GridAffordance.Heavy; c.Pawn = GridPawn.Colonist; c.HasItem = true; });

        string text = _tools.ViewMapGrid(1, 1, 8, 1, "BUILD");

        Assert.That(text, Does.StartWith("Build grid (1,1)-(8,1): 8x1 cells"));
        Assert.That(Row(text, 1), Is.EqualTo("Hml~##?H"));
        Assert.That(Lines(text).Last(), Is.EqualTo(
            "Legend: ? unexplored (fogged), # taken: building, blueprint, frame or natural rock (cannot build over it), " +
            "H heavy-capable ground (ship parts, reactor, most large buildings fit), m medium-capable ground only, " +
            "l light-capable ground only, ~ no building support (deep water, etc.)"));
    }

    [Test]
    public void BuildLayerMarksEveryCellOfAMultiCellBlueprintAndLeavesOthersOpen()
    {
        // 6x7 的藍圖覆蓋的每一格都標 #；緊鄰的空地仍是 H。
        for (int x = 2; x <= 4; x++)
        {
            for (int z = 1; z <= 2; z++)
            {
                Set(x, z, c => { c.BuildSupport = GridAffordance.Heavy; c.Building = GridBuilding.Blueprint; c.Blocked = true; });
            }
        }
        Set(1, 1, c => c.BuildSupport = GridAffordance.Heavy);
        Set(5, 1, c => c.BuildSupport = GridAffordance.Heavy);

        string text = _tools.ViewMapGrid(1, 1, 5, 2, "build");

        Assert.That(Row(text, 2), Is.EqualTo("~###~"), "未登記的格子是預設的 None 承載");
        Assert.That(Row(text, 1), Is.EqualTo("H###H"));
        Assert.That(Lines(text).Last(), Does.Contain("# taken: building, blueprint, frame or natural rock"));
    }

    [Test]
    public void BuildLayerLegendListsOnlyGlyphsThatAppear()
    {
        Set(1, 1, c => c.BuildSupport = GridAffordance.Heavy);

        string legend = Lines(_tools.ViewMapGrid(1, 1, 2, 1, "build")).Last();

        Assert.That(legend, Does.Contain("H heavy-capable").And.Contain("~ no building support"));
        Assert.That(legend, Does.Not.Contain("m medium").And.Not.Contain("# taken").And.Not.Contain("? unexplored"));
    }

    [Test]
    public void UnknownLayerIsAnErrorAndMapIsTheDefault()
    {
        Assert.That(_tools.ViewMapGrid(0, 0, 1, 1, "rooms"), Does.StartWith("Error:").And.Contain("\"map\"").And.Contain("\"roof\"").And.Contain("\"build\""));
        Assert.That(_tools.ViewMapGrid(0, 0, 1, 1, ""), Is.EqualTo(_tools.ViewMapGrid(0, 0, 1, 1, "MAP")));
        Assert.That(_tools.ViewMapGrid(0, 0, 1, 1, null), Does.StartWith("Map grid"));
    }

    [Test]
    public void ToolIsAQueryInTheObserveCategoryAndStaysResident()
    {
        var registry = new AgentToolRegistry(_world, new RimAgent.Agent.AgentTranscript());

        Assert.That(registry.RiskOf("view_map_grid"), Is.EqualTo(ToolRisk.Query));
        Assert.That(ToolPacks.PackOf("view_map_grid", ToolCategory.Observe), Is.Null, "觀察類工具常駐，不放進工具包");
    }
}
