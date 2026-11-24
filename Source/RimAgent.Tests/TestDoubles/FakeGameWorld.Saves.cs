using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public ScreenshotInfo ScreenshotResult { get; set; } = new ScreenshotInfo
    {
        Success = true,
        FilePath = "C:/RimWorld/Screenshots/screenshot.png",
        Width = 1920,
        Height = 1080
    };

    public SaveContext SaveContext { get; set; } = new SaveContext { ColonyId = "colony1", Tick = 300000 };

    /// <summary>存檔資料夾：SaveGame 直接加進來（真實遊戲下一幀才寫檔，測試不必等）。</summary>
    public List<SaveSlotInfo> SaveFiles { get; } = [];

    public string LoadedSave { get; private set; }

    public SaveContext GetSaveContext() => SaveContext;

    public Task<IReadOnlyList<SaveSlotInfo>> GetSaveFilesAsync() => Task.FromResult<IReadOnlyList<SaveSlotInfo>>([.. SaveFiles]);

    public CommandResult SaveGame(string saveName)
    {
        Calls.Add($"SaveGame({saveName})");
        SaveFiles.Add(new SaveSlotInfo { Name = saveName, Checkpoint = true, ColonyId = SaveContext.ColonyId, Tick = SaveContext.Tick, WrittenUtcTicks = SaveFiles.Count + 1 });
        return CommandResult.Ok();
    }

    public CommandResult DeleteCheckpoint(string saveName)
    {
        Calls.Add($"DeleteCheckpoint({saveName})");
        SaveFiles.RemoveAll(s => s.Checkpoint && s.Name == saveName);
        return CommandResult.Ok();
    }

    public CommandResult LoadGame(string saveName)
    {
        Calls.Add($"LoadGame({saveName})");
        LoadedSave = saveName;
        return CommandResult.Ok();
    }

    /// <summary>ReadScreenshotFile 依路徑回傳的檔案內容；沒設定的路徑回傳空陣列（模擬尚未寫完）。</summary>
    public Dictionary<string, byte[]> ScreenshotImages { get; } = new Dictionary<string, byte[]>();

    public byte[] ReadScreenshotFile(string filePath)
    {
        Calls.Add($"ReadScreenshotFile({filePath})");
        return ScreenshotImages.TryGetValue(filePath ?? string.Empty, out byte[] image) ? image : [];
    }
}
