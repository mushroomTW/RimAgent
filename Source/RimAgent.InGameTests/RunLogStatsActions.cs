using System.IO;
using LudeonTK;
using RimAgent.Agent;
using RimWorld;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 開發模式除錯動作「RimAgent」分類的運行紀錄統計按鈕：讀 RimAgent-runs.jsonl，
/// 報表寫到同資料夾的 RimAgent-runs-stats.txt 並用系統預設程式開啟（表格要等寬字型才對得齊）。
/// </summary>
internal static class RunLogStatsActions
{
    [DebugAction("RimAgent", "Run log stats: all runs", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    private static void AllRuns() => Show(null);

    [DebugAction("RimAgent", "Run log stats: this mod version", allowedGameStates = AllowedGameStates.Entry | AllowedGameStates.Playing)]
    private static void ThisVersion() => Show(LoadedModManager.GetMod<RimAgentMod>()?.Content?.ModMetaData?.ModVersion);

    private static void Show(string version)
    {
        string log = Path.Combine(RunLogWriter.DefaultFolder, RunLogWriter.FileName);
        if (!File.Exists(log))
        {
            Messages.Message("No RimAgent run log yet: " + log, MessageTypeDefOf.RejectInput, false);
            return;
        }
        string report = RunLogStats.Format(File.ReadLines(log), version);
        if (!string.IsNullOrEmpty(version)) report = "mod version " + version + "\n" + report;
        string output = Path.Combine(RunLogWriter.DefaultFolder, "RimAgent-runs-stats.txt");
        File.WriteAllText(output, report);
        Log.Message("[RimAgent] run log stats\n" + report);
        UnityEngine.Application.OpenURL(new System.Uri(output).AbsoluteUri);
    }
}
