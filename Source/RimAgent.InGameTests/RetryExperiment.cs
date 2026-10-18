using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using RimAgent.Agent;
using RimAgent.Game;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 自主任務重試時機的資料收集（不是測試，不斷言）：在 quicktest 地圖開自主模式讓真模型跑，
/// 每 <see cref="SampleTicks"/> 記一行施工進度、排程器訊號與重試表，事後與 RimAgent-runs.jsonl 的
/// 觸發／嘗試次數／tick 對照，回答「重試時前一次的施工是否還沒完成」。
/// 只有隔離環境的 Config 資料夾裡有 <see cref="MarkerFileName"/> 才啟動（run_test_cycle 的 seed_config 帶入），
/// 平常跑測試時完全不動。需要同時 seed 玩家的 RimAgent 設定（模型照玩家的走，模式與紀錄開關由這裡打開）與框架的供應商設定和金鑰檔。
/// </summary>
public sealed class RetryExperimentComponent : GameComponent
{
    public const string MarkerFileName = "RimAgentRetryExperiment.xml";
    private const string LogPrefix = TestHelpers.LogPrefix + "experiment: ";
    private const int SampleTicks = 250;
    /// <summary>收集兩個遊戲日：足夠讓每個基地狀況走完三次嘗試，也看得到放棄後的施工結果。</summary>
    private const int DurationTicks = 120000;
    /// <summary>遊戲在沒有強制暫停的視窗下停住超過這麼久就恢復，避免無人看管時整輪卡死。</summary>
    private const float PausedResumeSeconds = 20f;

    private static readonly bool Enabled = File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName));

    private bool _started;
    private bool _speedApplied;
    private bool _done;
    private int _startTick;
    private float _startRealtime;
    private float _pausedSince = -1f;
    private string _timelinePath;
    private RimWorldGameWorld _world;

    public RetryExperimentComponent(Verse.Game game)
    {
    }

    public override void GameComponentTick()
    {
        if (!Enabled || _done) return;
        if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        int tick = Find.TickManager.TicksGame;
        if (!_started)
        {
            Start(tick);
            return;
        }
        if (!_speedApplied) ApplySpeed(tick);
        if (tick % SampleTicks == 0) Sample(tick, null);
        if (tick - _startTick >= DurationTicks) Finish(tick);
    }

    public override void GameComponentUpdate()
    {
        if (!Enabled || !_started || _done) return;
        // 任務進行中的暫停可能是 agent 自己用 set_time_speed 下的，等它跑完再判斷，不替它恢復。
        if (!Find.TickManager.Paused || Find.WindowStack.WindowsForcePause || AutoRunning(RimAgentGameComponent.Instance))
        {
            _pausedSince = -1f;
            return;
        }
        if (_pausedSince < 0f)
        {
            _pausedSince = Time.realtimeSinceStartup;
            return;
        }
        if (Time.realtimeSinceStartup - _pausedSince < PausedResumeSeconds) return;
        _pausedSince = -1f;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        Sample(Find.TickManager.TicksGame, "resumed after pause");
    }

    private void Start(int tick)
    {
        _started = true;
        RimAgentSettings settings = RimAgentMod.Settings;
        RimAgentGameComponent agent = RimAgentGameComponent.Instance;
        if (settings == null || agent == null)
        {
            _done = true;
            Log.Error(LogPrefix + $"not started: RimAgent settings={settings != null}, component={agent != null}");
            return;
        }
        // 模型沿用 seed 進來的玩家設定；只在隔離環境的記憶體裡改模式與紀錄開關，不寫回設定檔。
        settings.mode = AgentMode.Autonomous;
        settings.recordRuns = true;

        string folder = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent");
        Directory.CreateDirectory(folder);
        // 不清空：同一個隔離環境裡讀檔或開新局會重新 Start，以 "start" 註記分段，保留先前的取樣。
        _timelinePath = Path.Combine(folder, "RetryExperiment.jsonl");
        _world = new RimWorldGameWorld();
        _startTick = tick;
        _startRealtime = Time.realtimeSinceStartup;

        // quicktest 地圖開局就有敵對目標，會先觸發襲擊、蓋過基地狀況；清掉讓情境從「沒床」乾淨開始。
        int cleared = ClearHostiles(Find.CurrentMap);
        // 自主模式要等玩家先講過話才啟動排程器；實驗沒有玩家，直接開。
        agent.StartAutonomousAfterPlayerMessage();
        Log.Message(LogPrefix + "started at tick " + tick + "; cleared " + cleared + " hostiles; timeline " + _timelinePath + "; folder " + folder);
        Sample(tick, "start, cleared " + cleared + " hostiles");
    }

    /// <summary>與排程器的敵人計數同一來源（攻擊目標快取），砲塔、機兵群建築也一併清掉。</summary>
    private static int ClearHostiles(Map map)
    {
        List<Thing> hostiles = [.. map.attackTargetsCache.TargetsHostileToColony.Select(t => t.Thing).Where(t => t != null && !t.Destroyed)];
        foreach (Thing thing in hostiles) thing.Destroy(DestroyMode.Vanish);
        return hostiles.Count;
    }

    /// <summary>
    /// 原版 CurTimeSpeed 的 setter 在 Game.PlayerHasControl 為 false 時靜默忽略（開局頭幾個 tick 就是如此），
    /// 所以每 tick 再設一次，直到讀回來真的是 Superfast；之後不再碰，速度交給 agent 與暫停保護。
    /// </summary>
    private void ApplySpeed(int tick)
    {
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        if (Find.TickManager.CurTimeSpeed != TimeSpeed.Superfast) return;
        _speedApplied = true;
        Sample(tick, "speed applied");
    }

    private void Finish(int tick)
    {
        Sample(tick, "done");
        _done = true;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        Log.Message(LogPrefix + "done at tick " + tick + " after " + (tick - _startTick) + " ticks");
    }

    private void Sample(int tick, string note)
    {
        try
        {
            File.AppendAllText(_timelinePath, BuildLine(tick, note) + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 單次取樣失敗不中斷收集；同一種錯誤會每次都記，正好看得出是否持續。
            Log.Warning(LogPrefix + "sample at tick " + tick + " failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private string BuildLine(int tick, string note)
    {
        Map map = Find.CurrentMap;
        RimAgentGameComponent agent = RimAgentGameComponent.Instance;
        AutonomousSignals s = _world.GetAutonomousSignals();

        List<Thing> blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
        List<Thing> frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
        int bedBlueprints = blueprints.Count(t => IsBed(t.def.entityDefToBuild));
        List<Frame> bedFrames = [.. frames.OfType<Frame>().Where(f => IsBed(f.def.entityDefToBuild))];
        float bedFrameProgress = bedFrames.Sum(f => f.PercentComplete);
        int beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Count(b => !b.ForPrisoners && b.def.building.bed_humanlike);

        // 不碰 agent.Session：它的 getter 會延遲建立整個 session，改變 RimAgent 自己第一次起跑的時機。
        ColonyAgentSession session = Traverse.Create(agent).Field("_session").GetValue<ColonyAgentSession>();
        var line = new Dictionary<string, object>
        {
            ["tick"] = tick,
            ["elapsed"] = tick - _startTick,
            ["realSeconds"] = Time.realtimeSinceStartup - _startRealtime,
            ["speed"] = Find.TickManager.CurTimeSpeed.ToString(),
            ["autoRunning"] = AutoRunning(agent),
            ["instructionId"] = session?.Transcript.CurrentInstructionId ?? 0,
            ["colonists"] = s.ColonistCount,
            ["bedless"] = s.BedlessCount,
            ["outdoorsBeds"] = s.OutdoorsBedCount,
            ["cold"] = s.ColdColonistCount,
            ["lowMood"] = s.LowMoodCount,
            ["food"] = s.FoodNutrition,
            ["research"] = s.HasActiveResearch,
            ["power"] = s.AnyWorkingPower,
            ["hostiles"] = s.HostileCount,
            ["beds"] = beds,
            ["bedBlueprints"] = bedBlueprints,
            ["bedFrames"] = bedFrames.Count,
            ["bedFrameProgress"] = bedFrameProgress,
            ["blueprints"] = blueprints.Count,
            ["frames"] = frames.Count,
            ["watch"] = CaptureWatch(agent).Where(w => w.Fired).Select(w => new
            {
                trigger = ((AutonomousTrigger)w.Trigger).ToString(),
                level = w.Level,
                attempts = w.Attempts,
                lastTick = w.LastTick,
                gaveUp = w.GaveUp
            }).ToList()
        };
        if (note != null) line["note"] = note;
        return JsonSerializer.Serialize(line);
    }

    private static bool AutoRunning(RimAgentGameComponent agent) =>
        agent != null && Traverse.Create(agent).Field("_autoRunning").GetValue<bool>();

    private static IReadOnlyList<AutonomousWatch> CaptureWatch(RimAgentGameComponent agent)
    {
        AutonomousScheduler scheduler = Traverse.Create(agent).Field("_scheduler").GetValue<AutonomousScheduler>();
        return scheduler?.CaptureWatch() ?? (IReadOnlyList<AutonomousWatch>)[];
    }

    private static bool IsBed(BuildableDef def) => def is ThingDef thing && thing.IsBed;
}
