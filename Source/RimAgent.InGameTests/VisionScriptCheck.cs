using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimAgent.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 隔離環境 Config 有標記檔 RimAgentVisionScriptCheck.xml 時，進圖後跑一次，驗證只能在遊戲內確認的部分：
/// add_zone 跳過不合格格子並分開不相連的區域、截圖讀回並縮成 JPEG、剪貼簿 DIB 轉圖、大圖縮圖、
/// run_script 在主線程逐步呼叫工具、超額矩形裁切。結果是 "[RimAgent.InGameTests] VisionScriptCheck PASS/FAIL" 警告。
/// 不需要模型與金鑰。
/// </summary>
public sealed class VisionScriptCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentVisionScriptCheck.xml";
    private const string Prefix = TestHelpers.LogPrefix + "VisionScriptCheck ";
    private static bool _started;

    public VisionScriptCheckComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (_started || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        _started = true;
        if (!File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName))) return;
        RunAsync();
    }

    private static async void RunAsync()
    {
        var failures = new List<string>();
        try
        {
            var world = new RimWorldGameWorld(Current.Game);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            CheckZoneSkipsAndSplits(world, failures);
            await CheckScreenshotForModel(world, failures);
            CheckDibAndDownscale(failures);
            await CheckRunScript(world, failures);
            CheckOversizedInspectArea(world, failures);
            Log.Warning(Prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));
        }
        catch (Exception ex)
        {
            Log.Warning(Prefix + "FAIL exception: " + ex);
        }
    }

    /// <summary>先畫一條 1x6 的儲存區，再畫涵蓋它的 7x6 矩形：中間那條要跳過，左右兩塊各自成區。</summary>
    private static void CheckZoneSkipsAndSplits(RimWorldGameWorld world, List<string> failures)
    {
        Map map = Find.CurrentMap;
        IntVec3 origin = FindOpenArea(map, 7, 6);
        if (!origin.IsValid)
        {
            failures.Add("zone: no open 7x6 area near the colonists");
            return;
        }
        var tools = new ZoneTools(world);
        int x = origin.x;
        int z = origin.z;
        string strip = tools.AddZone("stockpile", x + 3, z, x + 3, z + 5, string.Empty);
        if (strip.StartsWith("Error:", StringComparison.Ordinal))
        {
            failures.Add("zone strip: " + strip);
            return;
        }
        int zonesBefore = map.zoneManager.AllZones.Count;
        string text = tools.AddZone("stockpile", x, z, x + 6, z + 5, string.Empty);
        int created = map.zoneManager.AllZones.Count - zonesBefore;
        Log.Message(Prefix + "zone result: " + text);
        if (text.StartsWith("Error:", StringComparison.Ordinal)) failures.Add("zone: " + text);
        if (!text.Contains("Skipped 6 cells (already in zone")) failures.Add("zone: expected 6 skipped cells, got: " + text);
        if (created != 2) failures.Add($"zone: expected 2 separate zones, created {created}");
        foreach (Zone zone in map.zoneManager.AllZones.Where(zn => !IsContiguous(zn))) failures.Add($"zone: {zone.label} is not contiguous");
    }

    private static IntVec3 FindOpenArea(Map map, int width, int height)
    {
        IntVec3 start = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
        var designator = new Designator_ZoneAddStockpile_Resources();
        foreach (IntVec3 corner in GenRadial.RadialCellsAround(start, 30f, true))
        {
            var rect = new CellRect(corner.x, corner.z, width, height);
            if (!rect.InBounds(map)) continue;
            if (rect.Cells.All(c => map.zoneManager.ZoneAt(c) == null && designator.CanDesignateCell(c).Accepted)) return corner;
        }
        return IntVec3.Invalid;
    }

    private static bool IsContiguous(Zone zone)
    {
        var cells = new HashSet<IntVec3>(zone.Cells);
        if (cells.Count == 0) return true;
        var seen = new HashSet<IntVec3> { cells.First() };
        var queue = new Queue<IntVec3>(seen);
        while (queue.Count > 0)
        {
            IntVec3 cell = queue.Dequeue();
            foreach (IntVec3 dir in GenAdj.CardinalDirections)
            {
                IntVec3 next = cell + dir;
                if (cells.Contains(next) && seen.Add(next)) queue.Enqueue(next);
            }
        }
        return seen.Count == cells.Count;
    }

    /// <summary>截圖在該幀結束才寫檔：以和正式流程相同的主線程排程重試讀取，結果要是長邊不超過上限的 JPEG。</summary>
    private static async Task CheckScreenshotForModel(RimWorldGameWorld world, List<string> failures)
    {
        ScreenshotInfo shot = world.TakeScreenshot("RimAgent_VisionCheck");
        if (!shot.Success)
        {
            failures.Add("screenshot: " + shot.Error);
            return;
        }
        byte[] jpeg = null;
        for (int attempt = 0; attempt < 40 && jpeg == null; attempt++)
        {
            await Task.Delay(100);
            byte[] png = world.ReadScreenshotFile(shot.FilePath);
            if (png != null) jpeg = ModelImageEncoder.FromEncoded(png);
        }
        if (jpeg == null)
        {
            failures.Add("screenshot: could not read " + shot.FilePath);
            return;
        }
        (int width, int height) = Decode(jpeg);
        Log.Message(Prefix + $"screenshot {Screen.width}x{Screen.height} -> jpeg {width}x{height}, {jpeg.Length} bytes");
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) failures.Add("screenshot: result is not a JPEG");
        if (Math.Max(width, height) > ModelImageEncoder.MaxEdge) failures.Add($"screenshot: {width}x{height} exceeds {ModelImageEncoder.MaxEdge}");

        string outside = Path.Combine(TestHelpers.RimAgentContent.RootDir, "About", "Preview.png");
        if (world.ReadScreenshotFile(outside) != null) failures.Add("screenshot: read a PNG outside the screenshot folder");
    }

    /// <summary>剪貼簿 DIB（BGR、由下往上）轉 JPEG 要保住尺寸與顏色；超過上限的大圖要等比縮小。</summary>
    private static void CheckDibAndDownscale(List<string> failures)
    {
        const int width = 300;
        const int height = 200;
        int stride = ((width * 24 + 31) / 32) * 4;
        var dib = new byte[40 + stride * height];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int i = 40 + row * stride + col * 3;
                dib[i] = 0;      // B
                dib[i + 1] = 0;  // G
                dib[i + 2] = 230; // R
            }
        }
        byte[] fromDib = ClipboardImage.FromDib(dib);
        if (fromDib == null)
        {
            failures.Add("dib: decode failed");
        }
        else
        {
            Color32 center = DecodePixel(fromDib, width / 2, height / 2, out int w, out int h);
            if (w != width || h != height) failures.Add($"dib: size {w}x{h}, expected {width}x{height}");
            if (center.r < 180 || center.g > 60 || center.b > 60) failures.Add($"dib: color {center}, expected red");
        }

        var big = new Texture2D(2000, 1000, TextureFormat.RGBA32, false);
        try
        {
            (int w, int h) = Decode(ModelImageEncoder.FromTexture(big));
            if (w != 1280 || h != 640) failures.Add($"downscale: 2000x1000 -> {w}x{h}, expected 1280x640");
        }
        finally
        {
            UnityEngine.Object.Destroy(big);
        }
    }

    /// <summary>run_script 在遊戲內逐步呼叫真正的工具；查詢類工具不改變狀態。</summary>
    private static async Task CheckRunScript(RimWorldGameWorld world, List<string> failures)
    {
        var registry = new AgentToolRegistry(world, new AgentTranscript(), null, new ConfirmationGate(() => AgentMode.Autonomous));
        var script = new ScriptTools(() => registry.Select(ToolPermissions.All), registry.Outputs);
        string text = await script.RunScript(
            "[{\"tool\":\"get_colony_overview\"},{\"tool\":\"find_things\",\"args\":{\"kind\":\"item\"}," +
            "\"for_each\":[{\"limit\":3},{\"limit\":5}]}]");
        Log.Message(Prefix + "run_script tail: " + text.Substring(Math.Max(0, text.Length - 120)));
        if (!text.EndsWith("Ran 3 of 3 calls: 3 ok, 0 failed.", StringComparison.Ordinal)) failures.Add("run_script: " + text);
    }

    private static void CheckOversizedInspectArea(RimWorldGameWorld world, List<string> failures)
    {
        string text = new ObserveTools(world).InspectArea(0, 0, 50, 50);
        if (!text.StartsWith("Note:", StringComparison.Ordinal) || !text.Contains("Area (0,0)-(49,49)"))
        {
            failures.Add("inspect_area crop: " + text.Substring(0, Math.Min(200, text.Length)));
        }
    }

    private static (int Width, int Height) Decode(byte[] image)
    {
        var texture = new Texture2D(2, 2);
        try
        {
            texture.LoadImage(image);
            return (texture.width, texture.height);
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static Color32 DecodePixel(byte[] image, int x, int y, out int width, out int height)
    {
        var texture = new Texture2D(2, 2);
        try
        {
            texture.LoadImage(image);
            width = texture.width;
            height = texture.height;
            return texture.GetPixel(x, y);
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }
}
