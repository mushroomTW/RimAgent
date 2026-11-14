using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimAgent.UI;

namespace RimAgent.Tests;

[TestFixture]
public class MapShotTests
{
    [Test]
    public void WholeMapFitsTheModelImageEdgeAndGetsTenCellGrid()
    {
        MapShotLayout layout = MapShotLayout.For(new CellRectSpec(0, 0, 249, 249));

        Assert.That(layout.ImageWidth, Is.LessThanOrEqualTo(MapShotLayout.MaxEdge));
        Assert.That(layout.ImageHeight, Is.LessThanOrEqualTo(MapShotLayout.MaxEdge));
        Assert.That(layout.PixelsPerCell, Is.EqualTo(4));
        Assert.That(layout.GridStep, Is.EqualTo(10));
        Assert.That(layout.MapWidthPx, Is.EqualTo(250 * layout.PixelsPerCell));
    }

    [Test]
    public void SmallAreasZoomInButCapPixelsPerCell()
    {
        MapShotLayout fifty = MapShotLayout.For(new CellRectSpec(100, 100, 149, 149));
        Assert.That(fifty.PixelsPerCell, Is.GreaterThanOrEqualTo(20));
        Assert.That(fifty.GridStep, Is.EqualTo(5));
        Assert.That(fifty.ImageWidth, Is.LessThanOrEqualTo(MapShotLayout.MaxEdge));

        MapShotLayout tiny = MapShotLayout.For(new CellRectSpec(10, 10, 12, 12));
        Assert.That(tiny.PixelsPerCell, Is.EqualTo(MapShotLayout.MaxPixelsPerCell));
    }

    [Test]
    public void WideStripUsesTheLongSideForScale()
    {
        MapShotLayout layout = MapShotLayout.For(new CellRectSpec(0, 0, 299, 19));
        Assert.That(layout.ImageWidth, Is.LessThanOrEqualTo(MapShotLayout.MaxEdge));
        Assert.That(layout.MapHeightPx, Is.EqualTo(20 * layout.PixelsPerCell));
    }

    [Test]
    public void ComposePlacesMapInsideMarginsAndDrawsGridLinesAndLabels()
    {
        // 20x20 格：從 x=5、z=5 起，格線在 x=10、z=10、x=20…
        var rect = new CellRectSpec(5, 5, 24, 24);
        MapShotLayout layout = MapShotLayout.For(rect);
        int ppc = layout.PixelsPerCell;
        byte[] map = Enumerable.Repeat((byte)0, layout.MapWidthPx * layout.MapHeightPx * 4).ToArray();
        for (int i = 0; i < map.Length; i += 4) map[i + 2] = 200; // 純藍地圖，alpha 0

        byte[] image = MapShotOverlay.Compose(map, layout);

        Assert.That(image.Length, Is.EqualTo(layout.ImageWidth * layout.ImageHeight * 4));
        // 地圖角落（不在格線上）：保持藍色並補成不透明。
        (byte r, byte g, byte b, byte a) corner = Pixel(image, layout, layout.LeftMargin + 1, layout.BottomMargin + 1);
        Assert.That(corner, Is.EqualTo(((byte)0, (byte)0, (byte)200, (byte)255)));
        // x = 10 的格子西緣是黃線（與藍色混合後紅、綠明顯升高）。
        int lineX = layout.LeftMargin + (10 - rect.MinX) * ppc;
        (byte r, byte g, byte b, byte a) line = Pixel(image, layout, lineX, layout.BottomMargin + 2);
        Assert.That(line.r, Is.GreaterThan(100));
        Assert.That(line.g, Is.GreaterThan(100));
        // 下方留白有白色數字像素（x 標籤），左下角留白區維持底色。
        bool labelPixel = Enumerable.Range(0, layout.BottomMargin).Any(y =>
            Enumerable.Range(lineX, 20).Any(x => Pixel(image, layout, x, y) == ((byte)255, (byte)255, (byte)255, (byte)255)));
        Assert.That(labelPixel, Is.True, "x label under the x=10 line");
        Assert.That(Pixel(image, layout, 0, 0), Is.EqualTo(((byte)28, (byte)28, (byte)28, (byte)255)));
    }

    private static (byte, byte, byte, byte) Pixel(byte[] image, MapShotLayout layout, int x, int y)
    {
        int i = (y * layout.ImageWidth + x) * 4;
        return (image[i], image[i + 1], image[i + 2], image[i + 3]);
    }

    [Test]
    public void ScreenshotWithRectangleRendersTheClampedMapAreaAndExplainsTheGrid()
    {
        var world = new FakeGameWorld();
        var tools = new ObserveTools(world);

        string text = tools.TakeScreenshot("base", 0, 0, 999, 999);

        Assert.That(world.Calls, Does.Contain("RenderMapArea((0,0)-(99,99), base)"));
        Assert.That(world.Calls.Any(c => c.StartsWith("TakeScreenshot")), Is.False);
        Assert.That(text, Does.StartWith("Screenshot saved: C:/RimWorld/Screenshots/base.png ("));
        Assert.That(text, Does.Contain("Map view of (0,0)-(99,99), north up"));
        Assert.That(TranscriptScreenshot.TryParseSavedPath(text), Is.EqualTo("C:/RimWorld/Screenshots/base.png"),
            "the image attacher still finds the path when the grid note follows");
    }

    [Test]
    public void ScreenshotWithoutRectangleStillCapturesThePlayersScreen()
    {
        var world = new FakeGameWorld();
        string text = new ObserveTools(world).TakeScreenshot("view");

        Assert.That(world.Calls, Does.Contain("TakeScreenshot(view)"));
        Assert.That(text, Is.EqualTo("Screenshot saved: C:/RimWorld/Screenshots/view.png (1920x1080)"));
    }

    [Test]
    public void ScreenshotRectangleOutsideTheMapIsAnError()
    {
        var world = new FakeGameWorld();
        string text = new ObserveTools(world).TakeScreenshot("x", 200, 200, 300, 300);

        Assert.That(text, Does.StartWith("Error:"));
        Assert.That(world.Calls.Any(c => c.StartsWith("RenderMapArea")), Is.False);
    }

    [Test]
    public void PartialCornersAreAnErrorInsteadOfATinyRender()
    {
        var world = new FakeGameWorld();
        string text = new ObserveTools(world).TakeScreenshot("x", x1: 0, z1: 0);

        Assert.That(text, Does.StartWith("Error:").And.Contain("all four corners"));
        Assert.That(world.Calls.Any(c => c.StartsWith("RenderMapArea") || c.StartsWith("TakeScreenshot")), Is.False);
    }

    [Test]
    public void InspectAreaCountsGroundSupportAndSingleCellNamesIt()
    {
        var world = new FakeGameWorld();
        world.LightOnlyCells.Add((1, 1));
        var tools = new ObserveTools(world);

        Assert.That(tools.InspectArea(0, 0, 2, 2), Does.Contain("ground holds heavy: 8, medium at most: 0, light at most: 1, nothing: 0"));
        Assert.That(tools.InspectArea(1, 1, 1, 1), Does.Contain("ground holds light buildings only"));
        Assert.That(tools.InspectArea(0, 0, 0, 0), Does.Contain("ground holds heavy buildings"));
    }

    [Test]
    public void BlockedScreenshotDoesNotRender()
    {
        var world = new FakeGameWorld();
        var gate = new ConfirmationGate(() => AgentMode.Confirm);
        string text = new ObserveTools(world, gate).TakeScreenshot("x", 0, 0, 10, 10);

        Assert.That(text, Does.StartWith(ToolText.ConfirmationPrefix + "would take screenshot"));
        Assert.That(world.Calls.Any(c => c.StartsWith("RenderMapArea")), Is.False);
    }
}
