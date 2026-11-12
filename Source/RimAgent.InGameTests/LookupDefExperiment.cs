using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimAgent.Agent;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 真模型實測：問幾個需要遊戲知識的問題（不提示工具名稱），看模型是否主動用 lookup_def 查，而不是憑記憶回答。
/// 只有隔離環境的 Config 有 <see cref="MarkerFileName"/> 才啟動；模型與金鑰照 seed 進來的玩家設定。
/// 每題的工具呼叫與回覆以訊息輸出，最後一行 "[RimAgent.InGameTests] LookupDefExperiment DONE" 彙總。
/// </summary>
public sealed class LookupDefExperimentComponent : GameComponent
{
    public const string MarkerFileName = "RimAgentLookupDefExperiment.xml";
    private const string LogPrefix = TestHelpers.LogPrefix + "LookupDefExperiment ";
    private static readonly bool Enabled = File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName));

    private static readonly string[] Questions =
    [
        "突擊步槍要怎麼做？需要什麼研究、材料和工作台？只要回答，不要下任何指令。",
        "瘟疫有多危險？嚴重度到多少會致命？只要回答，不要下任何指令。",
        "零部件除了自己做，還能從哪裡取得？只要回答，不要下任何指令。"
    ];

    private static bool _started;
    private float _readyAt = -1f;

    public LookupDefExperimentComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (!Enabled || _started || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        // 等 RimAgent 自己的初始化跑完再送指令。
        if (_readyAt < 0f) _readyAt = UnityEngine.Time.realtimeSinceStartup + 5f;
        if (UnityEngine.Time.realtimeSinceStartup < _readyAt) return;
        _started = true;
        RunAsync();
    }

    private static async void RunAsync()
    {
        try
        {
            RimAgentSettings settings = RimAgentMod.Settings;
            RimAgentGameComponent agent = RimAgentGameComponent.Instance;
            if (settings == null || agent == null)
            {
                Log.Warning(LogPrefix + $"FAIL not started: settings={settings != null}, component={agent != null}");
                return;
            }
            // 只改隔離環境記憶體裡的設定：關掉自主任務免得插隊，確認模式讓行動類工具停在待確認。
            settings.mode = AgentMode.Confirm;
            settings.autonomousCare = false;
            settings.recordRuns = true;

            ColonyAgentSession session = agent.Session;
            var summary = new List<string>();
            for (int i = 0; i < Questions.Length; i++)
            {
                int before = session.Transcript.Snapshot().Count;
                string reply;
                try
                {
                    reply = await session.SendAsync(Questions[i]);
                }
                catch (Exception ex)
                {
                    reply = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message;
                }
                List<TranscriptEntry> added = [.. session.Transcript.Snapshot().Skip(before)];
                List<TranscriptEntry> tools = [.. added.Where(e => e.Kind == TranscriptKind.Tool)];
                string errors = string.Join(" | ", added.Where(e => e.Kind == TranscriptKind.Error).Select(e => e.Text));
                Log.Message(LogPrefix + $"Q{i + 1}: {Questions[i]}\nTools ({tools.Count}):\n" +
                            string.Join("\n", tools.Select(t => $"- {t.ToolName}({t.Arguments}){(t.Failed ? " FAILED" : string.Empty)} => {Short(t.Result, 300)}")) +
                            (errors.Length > 0 ? "\nErrors: " + errors : string.Empty) +
                            "\nReply:\n" + (reply ?? "(null)"));
                int lookups = tools.Count(t => t.ToolName == "lookup_def");
                summary.Add($"Q{i + 1} tools={tools.Count} lookup_def={lookups} reply={(reply == null ? "null" : "ok")}");
            }
            Log.Warning(LogPrefix + "DONE: " + string.Join("; ", summary));
        }
        catch (Exception ex)
        {
            Log.Warning(LogPrefix + "FAIL exception: " + ex);
        }
    }

    private static string Short(string text, int max)
    {
        string flat = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " / ");
        return flat.Length > max ? flat.Substring(0, max) + "…" : flat;
    }
}
