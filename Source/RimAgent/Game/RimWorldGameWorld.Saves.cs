using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public ScreenshotInfo TakeScreenshot(string filename = null)
    {
        try
        {
            string fullPath = ReserveScreenshotPath(filename);
            ScreenshotTaker.TakeNonSteamShot(System.IO.Path.GetFileNameWithoutExtension(fullPath));

            return new ScreenshotInfo
            {
                Success = true,
                FilePath = fullPath,
                Width = UnityEngine.Screen.width,
                Height = UnityEngine.Screen.height
            };
        }
        catch (Exception ex)
        {
            Log.Error("RimAgent: TakeScreenshot failed: " + ex);
            return new ScreenshotInfo
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public byte[] ReadScreenshotFile(string filePath)
    {
        // 只讀遊戲自己的截圖資料夾，避免工具結果被拿來讀任意檔案。資料夾在截圖時於主線程記下，這裡可在背景執行緒呼叫；還沒截過圖就沒有可讀的檔。
        string fullPath = UI.TranscriptScreenshot.ResolveInFolder(filePath, _screenshotFolder);
        if (fullPath == null) return [];
        try
        {
            if (!System.IO.File.Exists(fullPath)) return [];
            byte[] png = System.IO.File.ReadAllBytes(fullPath);
            // 截圖在該幀渲染完才寫出；沒有 IEND 區塊代表還沒寫完，不必拿去解碼。
            return IsCompletePng(png) ? png : [];
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            // 截圖可能仍在寫入；呼叫端會重試。
            return [];
        }
    }

    /// <summary>PNG 以 IEND 區塊（長度 0、類型 "IEND"、4 位元組 CRC）結尾。</summary>
    private static bool IsCompletePng(byte[] png)
    {
        int n = png.Length;
        return n >= 12 && png[n - 8] == 'I' && png[n - 7] == 'E' && png[n - 6] == 'N' && png[n - 5] == 'D';
    }

    public SaveContext GetSaveContext()
    {
        return new SaveContext
        {
            ColonyId = RimAgentGameComponent.Instance?.ColonyId ?? string.Empty,
            Tick = Find.TickManager.TicksGame,
            Permadeath = Find.GameInfo.permadeathMode,
            SavingBlockedReason = GameDataSaveLoader.SavingIsTemporarilyDisabled ? "a window or the world tile picker prevents saving" : null
        };
    }

    public async System.Threading.Tasks.Task<IReadOnlyList<SaveSlotInfo>> GetSaveFilesAsync()
    {
        // 列檔案在主線程（GenFilePaths 會建資料夾）；開 autosave 讀代號交給背景執行緒。
        var files = GenFilePaths.AllSavedGameFiles
            .Select(f => new { Name = System.IO.Path.GetFileNameWithoutExtension(f.Name), f.FullName, Written = f.LastWriteTimeUtc.Ticks })
            .Where(f => f.Name.StartsWith(Tools.SaveTools.CheckpointPrefix, StringComparison.OrdinalIgnoreCase)
                || f.Name.StartsWith(GameDataSaveLoader.AutosavePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return await System.Threading.Tasks.Task.Run(() => (IReadOnlyList<SaveSlotInfo>)files.Select(f =>
        {
            var info = new SaveSlotInfo { Name = f.Name, WrittenUtcTicks = f.Written };
            if (TryParseCheckpointName(f.Name, out string colonyId, out int tick))
            {
                info.Checkpoint = true;
                info.ColonyId = colonyId;
                info.Tick = tick;
            }
            else if (f.Name.StartsWith(GameDataSaveLoader.AutosavePrefix, StringComparison.OrdinalIgnoreCase))
            {
                SaveFileScanner.Scan scan = SaveFileScanner.Read(f.FullName, f.Written);
                info.ColonyId = scan.ColonyId;
                info.Tick = scan.Tick;
            }
            return info;
        }).ToList());
    }

    /// <summary>檢查點名稱是「前綴＋代號＋-＋tick」；代號本身不含連字號。</summary>
    private static bool TryParseCheckpointName(string name, out string colonyId, out int tick)
    {
        colonyId = string.Empty;
        tick = 0;
        if (!name.StartsWith(Tools.SaveTools.CheckpointPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = name.Substring(Tools.SaveTools.CheckpointPrefix.Length);
        int dash = rest.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(rest[(dash + 1)..], out tick)) return false;
        colonyId = rest[..dash];
        return true;
    }

    public CommandResult SaveGame(string saveName)
    {
        // 同原版存檔視窗：排成長事件，在下一幀、離開目前的更新流程後才寫檔。
        LongEventHandler.QueueLongEvent(() => GameDataSaveLoader.SaveGame(saveName), "SavingLongEvent", false, null);
        return CommandResult.Ok();
    }

    public CommandResult DeleteCheckpoint(string saveName)
    {
        if (!TryParseCheckpointName(saveName, out _, out _)) return CommandResult.Fail("not a RimAgent checkpoint: " + saveName);
        try
        {
            System.IO.File.Delete(GenFilePaths.FilePathForSavedGame(saveName));
            return CommandResult.Ok();
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            Log.Warning("RimAgent could not delete checkpoint " + saveName + ": " + ex.Message);
            return CommandResult.Fail(ex.Message);
        }
    }

    public CommandResult LoadGame(string saveName)
    {
        if (!SaveGameFilesUtility.SavedGameNamedExists(saveName)) return CommandResult.Fail("save not found: " + saveName);
        RimAgentGameComponent.Instance?.PrepareForReload();
        // 讀檔會當場 Dispose 目前的遊戲：排成長事件，等這一幀的遊戲更新跑完才換掉。
        LongEventHandler.QueueLongEvent(() => GameDataSaveLoader.LoadGame(saveName), "LoadingLongEvent", false, null);
        return CommandResult.Ok();
    }
}
