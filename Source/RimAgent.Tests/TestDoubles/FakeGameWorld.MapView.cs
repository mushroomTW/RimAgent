using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public ScreenshotInfo TakeScreenshot(string filename = null)
    {
        Calls.Add($"TakeScreenshot({filename ?? "null"})");
        if (ScreenshotResult != null && !ScreenshotResult.Success)
        {
            return ScreenshotResult;
        }

        string name = string.IsNullOrWhiteSpace(filename) ? "screenshot" : filename;
        return new ScreenshotInfo
        {
            Success = ScreenshotResult?.Success ?? true,
            FilePath = $"C:/RimWorld/Screenshots/{name}.png",
            Width = ScreenshotResult?.Width ?? 1920,
            Height = ScreenshotResult?.Height ?? 1080,
            Error = ScreenshotResult?.Error ?? string.Empty
        };
    }

    public ScreenshotInfo RenderMapArea(CellRectSpec rect, string filename = null)
    {
        Calls.Add($"RenderMapArea({rect}, {filename ?? "null"})");
        if (ScreenshotResult != null && !ScreenshotResult.Success) return ScreenshotResult;
        MapShotLayout layout = MapShotLayout.For(rect);
        return new ScreenshotInfo
        {
            Success = true,
            FilePath = $"C:/RimWorld/Screenshots/{(string.IsNullOrWhiteSpace(filename) ? "screenshot" : filename)}.png",
            Width = layout.ImageWidth,
            Height = layout.ImageHeight,
            Layout = layout
        };
    }

    /// <summary>GetMapSurvey 回傳的勘查資料；沒設定時為 10x10 全是一般土壤的空地圖。</summary>
    public MapSurvey MapSurvey { get; set; }

    public MapSurvey GetMapSurvey()
    {
        Calls.Add("GetMapSurvey()");
        return MapSurvey ?? new MapSurvey
        {
            Width = 10,
            Height = 10,
            Cells = [.. Enumerable.Repeat(SurveyCell.Soil, 100)],
            Resource = new short[100],
            ThickRoof = new bool[100]
        };
    }
}
