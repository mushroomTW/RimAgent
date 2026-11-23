using System.Collections.Generic;
using System.IO;
using LudeonTK;
using RimAgent.Agent;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 開發模式除錯動作「RimAgent」分類的運行紀錄統計按鈕：讀 runs 資料夾，
/// 報表寫到 RimAgent 資料夾的 RimAgent-runs-stats.txt 並用系統預設程式開啟（表格要等寬字型才對得齊）。
/// </summary>
[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
internal static class RunLogStatsActions
{
    [DebugAction("RimAgent", "Run log stats: all runs", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    private static void AllRuns() => Show(version: null);

    [DebugAction("RimAgent", "Run log stats: this mod version", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    private static void ThisVersion() => Show(LoadedModManager.GetMod<RimAgentMod>()?.Content?.ModMetaData?.ModVersion);

    private static void Show(string version)
    {
        string runs = Path.Combine(RunLogWriter.DefaultFolder, RunLogWriter.FolderName);
        string log = Path.Combine(runs, RunLogWriter.IndexName);
        if (!File.Exists(log))
        {
            Messages.Message("No RimAgent run log yet: " + log, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        // 幾萬個事件檔在主線程讀會讓遊戲沒有回應。
        LongEventHandler.QueueLongEvent(() => Write(runs, log, version), "RimAgent run log stats", doAsynchronously: true,
            exceptionHandler: ex => Log.Error("[RimAgent] run log stats failed: " + ex));
    }

    private static void Write(string runs, string log, string version)
    {
        string report = RunLogStats.Format(File.ReadLines(log), file => ReadRun(Path.Combine(runs, file)), version);
        if (!string.IsNullOrEmpty(version)) report = "mod version " + version + "\n" + report;
        string output = Path.Combine(RunLogWriter.DefaultFolder, "RimAgent-runs-stats.txt");
        File.WriteAllText(output, report);
        Log.Message("[RimAgent] run log stats\n" + report);
        LongEventHandler.ExecuteWhenFinished(() => UnityEngine.Application.OpenURL(new System.Uri(output).AbsoluteUri));
    }

    private static IEnumerable<string> ReadRun(string path) => File.Exists(path) ? File.ReadLines(path) : [];
}
