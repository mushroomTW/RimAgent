using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;
using RimAgent.Tools;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 隔離環境 Config 有標記檔 RimAgentMapViewCheck.xml 時，進圖後跑一次，驗證只能在遊戲內確認的部分：
/// 離屏俯視截圖（同一幀要全圖與殖民者附近 50x50 兩張）都要寫出版面尺寸的 PNG、不動玩家鏡頭、不留下原版重力船旗標；
/// 全圖勘查逐格對照原版資料；get_map_overview 的文字寫進 log 供人工檢視。
/// 結果是 "[RimAgent.InGameTests] MapViewCheck PASS/FAIL" 警告；截圖路徑也寫進 log，供人工看圖。
/// </summary>
public sealed class MapViewCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentMapViewCheck.xml";
    private const string Prefix = TestHelpers.LogPrefix + "MapViewCheck ";
    private static bool _started;

    public MapViewCheckComponent(Verse.Game game)
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
            Map map = Find.CurrentMap;

            // 同一幀要兩張（先全圖再放大），兩張都要成功：截圖可並行。
            Vector3 cameraBefore = Find.Camera.transform.position;
            float sizeBefore = Find.CameraDriver.RootSize;
            IntVec3 anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
            CellRect near = CellRect.CenteredOn(anchor, 25).ClipInsideMap(map);
            var wholeRect = new CellRectSpec(0, 0, map.Size.x - 1, map.Size.z - 1);
            var nearRect = new CellRectSpec(near.minX, near.minZ, near.maxX, near.maxZ);
            ScreenshotInfo whole = world.RenderMapArea(wholeRect, "RimAgent_MapViewWhole");
            ScreenshotInfo zoomed = world.RenderMapArea(nearRect, "RimAgent_MapViewNear");
            await CheckRender(world, whole, wholeRect, "RimAgent_MapViewWhole", failures);
            await CheckRender(world, zoomed, nearRect, "RimAgent_MapViewNear", failures);
            if (GravshipCapturer.IsGravshipRenderInProgress) failures.Add("the vanilla gravship render flag was left on");
            if (Find.Camera.transform.position != cameraBefore || !Mathf.Approximately(Find.CameraDriver.RootSize, sizeBefore))
                failures.Add("the player's camera moved");
            CheckSurvey(world, map, failures);

            string overview = new MapOverviewTools(world).GetMapOverview();
            Log.Message(Prefix + "get_map_overview (" + overview.Length + " chars):\n" + overview);
            if (overview.Length > 8000) failures.Add($"overview: {overview.Length} chars exceeds the 8000-char tool result cap");

            Log.Warning(Prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));
        }
        catch (Exception ex)
        {
            Log.Warning(Prefix + "FAIL exception: " + ex);
        }
    }

    private static async Task CheckRender(RimWorldGameWorld world, ScreenshotInfo shot, CellRectSpec rect, string name, List<string> failures)
    {
        if (!shot.Success)
        {
            failures.Add($"{name}: {shot.Error}");
            return;
        }
        byte[] png = null;
        for (int attempt = 0; attempt < 40 && png == null; attempt++)
        {
            await Task.Delay(100);
            png = world.ReadScreenshotFile(shot.FilePath);
        }
        if (png == null)
        {
            failures.Add($"{name}: no PNG at {shot.FilePath}");
            return;
        }
        var texture = new Texture2D(2, 2);
        texture.LoadImage(png);
        MapShotLayout layout = MapShotLayout.For(rect);
        Log.Message(Prefix + $"{name}: {rect} -> {texture.width}x{texture.height}, {layout.PixelsPerCell} px/cell, {png.Length} bytes, {shot.FilePath}");
        if (texture.width != layout.ImageWidth || texture.height != layout.ImageHeight)
            failures.Add($"{name}: image {texture.width}x{texture.height}, layout says {layout.ImageWidth}x{layout.ImageHeight}");
        if (texture.width != shot.Width || texture.height != shot.Height)
            failures.Add($"{name}: image {texture.width}x{texture.height}, tool reported {shot.Width}x{shot.Height}");

        // 地圖區域不能是單色（渲染失敗時整塊是清除色）。
        Color32[] pixels = texture.GetPixels32();
        int distinct = pixels.Where((_, i) => i % 97 == 0).Select(c => (c.r << 16) | (c.g << 8) | c.b).Distinct().Count();
        if (distinct < 20) failures.Add($"{name}: only {distinct} distinct colours, the map was probably not drawn");
        UnityEngine.Object.Destroy(texture);
    }

    /// <summary>勘查逐格對照原版：迷霧格不透露、天然岩石與礦脈、水、厚岩頂；殖民者數一致。</summary>
    private static void CheckSurvey(RimWorldGameWorld world, Map map, List<string> failures)
    {
        MapSurvey survey = world.GetMapSurvey();
        if (survey.Width != map.Size.x || survey.Height != map.Size.z) failures.Add($"survey: size {survey.Width}x{survey.Height}");
        var samples = new Dictionary<string, int>();
        foreach (IntVec3 c in map.AllCells)
        {
            int i = map.cellIndices.CellToIndex(c);
            SurveyCell shown = survey.Cells[i];
            string kind;
            bool ok;
            Building edifice = c.GetEdifice(map);
            if (c.Fogged(map))
            {
                kind = "fog";
                ok = shown == SurveyCell.Unknown && survey.Resource[i] == 0 && !survey.ThickRoof[i];
            }
            else if (edifice != null && edifice.def.building.isNaturalRock)
            {
                kind = edifice.def.building.isResourceRock ? "ore" : "rock";
                ok = shown == SurveyCell.Rock
                    && (edifice.def.building.isResourceRock
                        ? survey.Resource[i] > 0 && survey.ResourceLabels[survey.Resource[i] - 1] == edifice.def.label
                        : survey.Resource[i] == 0);
            }
            else if (edifice != null)
            {
                kind = "structure";
                ok = shown == SurveyCell.Structure;
            }
            else if (c.GetTerrain(map).IsWater)
            {
                kind = "water";
                ok = shown == SurveyCell.ShallowWater || shown == SurveyCell.DeepWater;
            }
            else
            {
                kind = "ground";
                ok = shown != SurveyCell.Unknown && shown != SurveyCell.Rock && shown != SurveyCell.Structure;
            }
            samples[kind] = samples.TryGetValue(kind, out int n) ? n + 1 : 1;
            if (!ok && failures.Count < 20) failures.Add($"survey: {kind} at {c} shown as {shown}");
            bool roof = !c.Fogged(map) && map.roofGrid.RoofAt(c)?.isThickRoof == true;
            if (survey.ThickRoof[i] != roof && failures.Count < 20) failures.Add($"survey: thick roof at {c} is {survey.ThickRoof[i]}");
        }
        if (survey.Colonists.Count != map.mapPawns.FreeColonistsSpawned.Count) failures.Add($"survey: {survey.Colonists.Count} colonists");
        Log.Message(Prefix + "survey samples " + string.Join(", ", samples.Select(kv => kv.Key + " x" + kv.Value)));
    }
}
