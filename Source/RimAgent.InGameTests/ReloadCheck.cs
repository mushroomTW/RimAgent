using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 讀檔重來的遊戲內檢查（DevTools 測試換不了場景，讀檔只能這樣驗）。只有隔離環境 Config 有標記檔才啟動：
/// 存檢查點、做一份 autosave、認出兩者屬於這一局，殺掉一位殖民者看死亡任務起跑，
/// 再用 load_save 讀回檢查點，確認新遊戲裡殖民者活著、收到讀檔通知與教訓、舊會話碰不到新遊戲。
/// 不需要模型：工具直接呼叫，自主任務沒有供應商會失敗，但指令文字仍記在對話紀錄裡。
/// 結果以 "[RimAgent.InGameTests] ReloadCheck PASS/FAIL" 警告輸出，list_test_diagnostics 看得到。
/// </summary>
public sealed class ReloadCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentReloadCheck.xml";
    private const string Prefix = TestHelpers.LogPrefix + "ReloadCheck ";
    private const float StepSeconds = 3f;

    private enum Phase { Start, Saved, Listed, Killed, Reloading, Done }

    // 讀檔會換掉整場遊戲（連同這個元件），進度放在靜態欄位跨讀檔保留。
    private static Phase _phase = Phase.Start;
    private static string _checkpoint;
    private static int _victimId;
    private static string _victimName;
    private static RimWorldGameWorld _oldWorld;
    private static readonly List<string> Failures = [];

    private static bool _stepping;
    private float _nextStepAt;

    public ReloadCheckComponent(Verse.Game game)
    {
    }

    private static bool Enabled => File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName));

    public override void GameComponentUpdate()
    {
        if (_phase == Phase.Done || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        if (Time.realtimeSinceStartup < _nextStepAt || !Enabled) return;
        if (_stepping) return;
        _nextStepAt = Time.realtimeSinceStartup + StepSeconds;
        _stepping = true;
        _ = StepGuardedAsync();
    }

    // 工具是 async，讀存檔清單會切到背景執行緒再回主線程：在主線程同步等它會卡死，一律 await。
    private static async Task StepGuardedAsync()
    {
        try
        {
            await StepAsync();
        }
        catch (Exception ex)
        {
            Fail("exception in " + _phase + ": " + ex);
            Finish();
        }
        finally
        {
            _stepping = false;
        }
    }

    private static async Task StepAsync()
    {
        RimAgentGameComponent agent = RimAgentGameComponent.Instance;
        switch (_phase)
        {
            case Phase.Start:
            {
                RimAgentSettings settings = RimAgentMod.Settings;
                settings.SetAllow(ToolCategory.Save, true);
                settings.autonomousCare = true;
                ClearDanger();
                var world = new RimWorldGameWorld(Current.Game);
                SaveContext context = world.GetSaveContext();
                Check(context.ColonyId.Length == 8, "colony id: " + context.ColonyId);
                _checkpoint = SaveTools.CheckpointName(context.ColonyId, context.Tick);
                string saved = await new SaveTools(world).SaveGame();
                Check(saved.StartsWith("Saved checkpoint", StringComparison.Ordinal), "save_game: " + saved);
                // 同原版：autosave 排成長事件。
                LongEventHandler.QueueLongEvent(() => Find.Autosaver.DoAutosave(), "Autosaving", false, null);
                _phase = Phase.Saved;
                break;
            }
            case Phase.Saved:
            {
                var world = new RimWorldGameWorld(Current.Game);
                IReadOnlyList<SaveSlotInfo> files = await world.GetSaveFilesAsync();
                string id = world.GetSaveContext().ColonyId;
                SaveSlotInfo checkpoint = files.FirstOrDefault(f => f.Name == _checkpoint);
                SaveSlotInfo autosave = files.FirstOrDefault(f => !f.Checkpoint && f.ColonyId == id);
                if (checkpoint == null || autosave == null) return; // 長事件還沒寫完
                Check(checkpoint.ColonyId == id, "checkpoint colony id");
                Check(autosave.Tick > 0, "autosave tick read: " + autosave.Tick);
                string list = await new SaveTools(world).ListSaves();
                Check(list.Contains(_checkpoint) && list.Contains(autosave.Name), "list_saves: " + list);
                _phase = Phase.Listed;
                break;
            }
            case Phase.Listed:
            {
                // 殖民者要先被點名（每 500 tick 一次）才認得出死亡。
                if (Find.TickManager.TicksGame < 1000)
                {
                    if (Find.TickManager.Paused) Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                    return;
                }
                Pawn victim = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First();
                _victimId = victim.thingIDNumber;
                _victimName = victim.LabelShort;
                victim.Kill(null);
                _phase = Phase.Killed;
                break;
            }
            case Phase.Killed:
            {
                // 死亡在下一次訊號檢查（500 tick）才點到；自主任務沒有供應商會失敗，但指令留在紀錄裡。
                bool started = agent.Session.Transcript.Snapshot()
                    .Any(e => e.Kind == TranscriptKind.User && e.Autonomous && e.Text.Contains("Died: " + _victimName));
                if (!started && Find.TickManager.Paused) Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                if (!started) return;
                agent.StopAutonomousRun();
                _oldWorld = new RimWorldGameWorld(Current.Game);
                string loaded = await new SaveTools(_oldWorld).LoadSave(_checkpoint, "test lesson: keep colonists inside");
                Check(loaded.StartsWith("Reloading", StringComparison.Ordinal), "load_save: " + loaded);
                _phase = Phase.Reloading;
                break;
            }
            case Phase.Reloading:
            {
                Pawn victim = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.thingIDNumber == _victimId);
                Check(victim != null && !victim.Dead, "victim alive after reload");
                bool notice = agent.Session.Transcript.Snapshot()
                    .Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("Reloaded " + _checkpoint) && e.Text.Contains("test lesson"));
                bool reloadRun = agent.Session.Transcript.Snapshot()
                    .Any(e => e.Kind == TranscriptKind.User && e.Autonomous && e.Text.Contains("test lesson"));
                if (!notice || !reloadRun)
                {
                    // 讀檔後的通知與任務在 Update 起跑，給它幾輪。
                    if (Time.realtimeSinceStartup < 600f) return;
                }
                Check(notice, "reload notice in transcript");
                Check(reloadRun, "reloaded task carries the lesson");
                Check(_oldWorld.GetNotReadyReason()?.Contains("reloaded") == true, "old session blocked: " + _oldWorld.GetNotReadyReason());
                string list = await new SaveTools(new RimWorldGameWorld(Current.Game)).ListSaves();
                Check(list.Contains("reloads used 1/3"), "attempt counted: " + list);
                Finish();
                break;
            }
        }
    }

    private static void ClearDanger()
    {
        Map map = Find.CurrentMap;
        foreach (Thing t in map.attackTargetsCache.TargetsHostileToColony.Select(t => t.Thing).Where(t => t != null && !t.Destroyed).ToList())
            t.Destroy(DestroyMode.Vanish);
        foreach (Fire fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire).OfType<Fire>().ToList()) fire.Destroy();
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) Fail(what);
        else Log.Message(Prefix + "ok: " + what);
    }

    private static void Fail(string what)
    {
        Failures.Add(what);
        Log.Warning(Prefix + "check failed: " + what);
    }

    private static void Finish()
    {
        _phase = Phase.Done;
        Log.Warning(Prefix + (Failures.Count == 0 ? "PASS" : "FAIL (" + Failures.Count + "): " + string.Join(" | ", Failures)));
    }
}
