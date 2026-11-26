#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using Verse;

namespace RimAgent;

public partial class RimAgentGameComponent
{
    /// <summary>Agent 或目標正在跑時不碰速度，由它們自己管。</summary>
    private void TickAutoTimeSpeed()
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || !settings.autoTimeSpeed || DateTime.UtcNow < _nextSpeedCheckAt) return;
        _nextSpeedCheckAt = DateTime.UtcNow.AddSeconds(SpeedCheckSeconds);
        if (_runner.IsRunning || IsGoalRunning || Find.CurrentMap == null) return;
        try
        {
            ColonyAgentSession session = Session;
            if (session.IsBusy || session.HasPendingConfirmation) return;
            bool safe = AutoTimeSpeed.IsSafe(
                _autoWorld.GetHostileCount(), _autoWorld.GetFireCount(), _autoWorld.GetColonistsInMedicalEmergency().Count);
            string target = _autoSpeed.Step(
                _autoWorld.GetTimeSpeed().Current, safe, DateTime.UtcNow, () => ObserveTools.FormatOverview(_autoWorld));
            if (target != null) _autoWorld.SetTimeSpeed(target);
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent auto time speed failed: " + ex.Message);
        }
    }

    /// <summary>放在 Update 而不是 Tick：對話框會暫停遊戲，暫停時 GameComponentTick 不跑；也不走排程器冷卻，因暫停中 tick 不前進。</summary>
    private void TryStartChoiceRun()
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || !settings.autonomousCare || DateTime.UtcNow < _nextChoiceCheckAt) return;
        _nextChoiceCheckAt = DateTime.UtcNow.AddSeconds(ChoiceCheckSeconds);
        if (_runner.IsRunning || IsGoalRunning || Find.CurrentMap == null) return;
        if (Find.TickManager.TicksGame < _runner.HoldUntilTick) return;
        try
        {
            ColonyAgentSession session = Session;
            if (session.IsBusy || session.HasPendingConfirmation) return;
            IReadOnlyList<PendingChoiceInfo> choices = _autoWorld.GetPendingChoices();
            bool fresh = false;
            foreach (PendingChoiceInfo choice in choices) fresh |= _seenChoices.Add(choice.Id);
            _seenChoices.RemoveWhere(id => choices.All(c => c.Id != id));
            // 開著的畫面可能讓遊戲一直暫停，tick 觸發不會來：上一輪沒答完就隔一陣子再叫一次。
            bool dialogLeft = choices.Any(c => c.Id.StartsWith(ChoiceTools.DialogIdPrefix, StringComparison.OrdinalIgnoreCase));
            if (!fresh && (!dialogLeft || DateTime.UtcNow < _nextDialogRetryAt)) return;
            _nextDialogRetryAt = DateTime.UtcNow.AddSeconds(DialogRetrySeconds);
            _ = RunChoiceGuardedAsync(session, Find.TickManager.TicksGame, ToConfig(settings));
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent choice check failed: " + ex.Message);
        }
    }

    /// <summary>重試用完而放棄的條件要通知玩家，否則玩家會以為 agent 還在處理。</summary>
    private void ReportGiveUps(ColonyAgentSession session)
    {
        IReadOnlyList<AutonomousTrigger> giveUps = _scheduler.TakeGiveUps();
        for (int i = 0; i < giveUps.Count; i++)
        {
            WarnOnce("RimAgent autonomous task gave up after " + AutonomousScheduler.MaxAttempts.ToString(CultureInfo.InvariantCulture) + " attempts: " + giveUps[i]);
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Autonomous task stopped retrying after " + AutonomousScheduler.MaxAttempts.ToString(CultureInfo.InvariantCulture) +
                       " attempts: " + DescribeGiveUp(giveUps[i]) +
                       ". It will pick this up again once the situation clears."
            });
        }
    }

    private static string DescribeGiveUp(AutonomousTrigger trigger)
    {
        return trigger switch
        {
            AutonomousTrigger.Fire => "the fire",
            AutonomousTrigger.EmergencyMedical => "the downed or bleeding colonist",
            AutonomousTrigger.MentalBreak => "the colonist near a mental break",
            AutonomousTrigger.FoodShortage => "the food shortage",
            AutonomousTrigger.Alert => "the game alerts",
            AutonomousTrigger.CriticalAlert => "the critical game alerts",
            AutonomousTrigger.Raid => "the ongoing raid",
            AutonomousTrigger.RaidEnded => "the raid cleanup",
            _ => trigger.ToString(),
        };
    }

    private static AutonomousConfig ToConfig(RimAgentSettings settings)
    {
        return new AutonomousConfig
        {
            Enabled = true,
            // 定期巡檢的節奏是設計決定，不隨設定變。
            IntervalTicks = AutonomousScheduler.HeartbeatTicks,
            WindowTicks = 60000,
            CooldownTicks = UnityEngine.Mathf.RoundToInt(settings.autonomousCooldownMinutes * (2500f / 60f)),
            OnRaid = true,
            OnMentalBreak = true,
            OnFoodShortage = true,
            OnEmergencyMedical = true,
            OnFire = true,
            OnUpkeep = true
        };
    }

    private static AutonomousSignals GatherSignals()
    {
        return RimWorldGameWorld.GetAutonomousSignals();
    }

    /// <summary>持續性問題只警告一種一次（缺文案之類），免得每 500 tick 洗一次除錯記錄。</summary>
    private void WarnOnce(string message)
    {
        if (_lastAutoError != message)
        {
            _lastAutoError = message;
            Log.Warning(message);
        }
    }

    public void StopAutonomousRun()
    {
        _runner.Stop();
    }

    public void DisableAutonomousCare()
    {
        StopAutonomousRun();
        _seenChoices.Clear();
    }

    private Task RunAutonomousGuardedAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config, AutonomousSignals? signals, string detail = null)
    {
        return _runner.RunGuardedAsync(async (generation, token) =>
        {
            try
            {
                if (signals.HasValue && await TriageSkipsAsync(session, trigger, signals.Value, tick, generation, token)) return;
                if (!_runner.IsCurrent(generation)) return;
                await RunAutonomousAsync(new AutonomousRunContext(session, trigger, tick, config, signals, detail), generation, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 玩家停止或讀檔取消。
            }
            catch (Exception ex)
            {
                WarnOnce("RimAgent autonomous run failed: " + ex.Message);
            }
        });
    }

    private Task RunChoiceGuardedAsync(ColonyAgentSession session, int tick, AutonomousConfig config)
    {
        return _runner.RunGuardedAsync(async (generation, token) =>
        {
            try
            {
                await RunAutonomousAsync(new AutonomousRunContext(session, AutonomousTrigger.Choice, tick, config, Signals: null, Detail: null), generation, token);
            }
            catch (Exception ex)
            {
                WarnOnce("RimAgent choice run failed: " + ex.Message);
            }
        });
    }

    /// <summary>決策模型不可用時回傳 false（照常叫主 LLM）。</summary>
    private async Task<bool> TriageSkipsAsync(ColonyAgentSession session, AutonomousTrigger trigger, AutonomousSignals signals, int tick, int generation, CancellationToken cancellationToken)
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || !settings.decisionTriageUpkeep || !DecisionAutonomousTriage.IsTriaged(trigger)) return false;
        string lastReply = null;
        float? hoursAgo = null;
        if (_lastAutoReplies.TryGetValue(trigger, out (string Reply, int Tick) last))
        {
            lastReply = last.Reply;
            hoursAgo = Math.Max(0, tick - last.Tick) / 2500f;
        }
        TriageVerdict verdict = await Triage.EvaluateAsync(trigger, signals, lastReply, hoursAgo, cancellationToken);
        if (!_runner.IsCurrent(generation) || verdict == null || !verdict.Skip) return false;
        _scheduler.Defer(trigger, CurrentTick() ?? tick);
        string notice = $"System One judged that {DescribeGiveUp(trigger)} needs no action yet " +
                        $"(need {ToolText.Pct((float)verdict.Probability)}); it will check again in an hour.";
        if (_triageNoticed.Add(trigger)) session.Transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Notice, Text = notice });
        LogSystemOne("triage", notice);
        return true;
    }

    /// <summary>System One 不經主 LLM 的判斷也記進執行紀錄（開啟執行紀錄時），之後才量得出準確率。</summary>
    private void LogSystemOne(string kind, string text)
    {
        if (RimAgentMod.Settings?.recordRuns != true) return;
        try
        {
            ColonyAgentSession session = _session;
            session?.RunLogs?.Note(new RunLogHeader
            {
                Source = "system_one",
                Mode = session.Mode.ToString(),
                Instruction = kind
            }, text);
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent run log failed: " + ex.Message);
        }
    }

    /// <summary>沿用 guard 進場時取得的 token：篩選或代答期間按下停止，主 LLM 這一輪也跟著取消。</summary>
    private async Task RunAutonomousAsync(AutonomousRunContext run, int generation, CancellationToken token)
    {
        if (!_runner.IsCurrent(generation)) return;
        string instruction = BuildAutonomousInstruction(run.Trigger);
        if (string.IsNullOrWhiteSpace(instruction)) return;
        if (!string.IsNullOrEmpty(run.Detail)) instruction += "\n\n" + run.Detail;
        (bool completed, string reply) = await _runner.RunTaskAsync(new AutonomousRunRequest
        {
            Session = run.Session,
            Trigger = run.Trigger,
            Tick = run.Tick,
            Config = run.Config,
            Instruction = instruction,
            MaxToolSteps = run.Session.Mode == AgentMode.Autonomous ? null : AutonomousRunMaxSteps,
            RunInfo = RimAgentMod.Settings?.recordRuns == true ? BuildRunInfo(run.Trigger, run.Tick, run.Signals) : null
        }, generation, token);
        if (completed) HandleAutonomousReply(run.Trigger, reply, generation);
    }

    private readonly record struct AutonomousRunContext(
        ColonyAgentSession Session,
        AutonomousTrigger Trigger,
        int Tick,
        AutonomousConfig Config,
        AutonomousSignals? Signals,
        string Detail);

    private string BuildAutonomousInstruction(AutonomousTrigger trigger)
    {
        AutonomousPack pack = _pack?.Autonomous;
        if (pack != null && !AutonomousPrompts.HasTask(pack, trigger) && AutonomousPrompts.HasTask(pack, AutonomousTrigger.Interval))
        {
            // 章節缺失（通常是拼字錯誤）時 Build 會靜默退回 Interval：警告一次讓問題可見。
            WarnOnce("RimAgent autonomous task missing section for " + trigger + ", falling back to Interval");
        }
        string instruction = AutonomousPrompts.Build(pack, trigger);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            // 自主任務文案缺檔：送空話只會燒 API，直接跳過本輪（沿用錯誤節流，只記一種一次）。
            WarnOnce("RimAgent autonomous run skipped: Prompts/Autonomous.en.md is missing or has no task for " + trigger);
        }
        return instruction;
    }

    private void HandleAutonomousReply(AutonomousTrigger trigger, string reply, int generation)
    {
        if (DecisionAutonomousTriage.IsTriaged(trigger) && _runner.IsCurrent(generation) && Current.Game != null)
        {
            _lastAutoReplies[trigger] = (reply ?? string.Empty, Find.TickManager.TicksGame);
            _triageNoticed.Remove(trigger);
        }
    }

    /// <summary>執行紀錄用的起跑脈絡；在主線程起跑前組好，嘗試次數才是這一次的。</summary>
    private AutonomousRunInfo BuildRunInfo(AutonomousTrigger trigger, int tick, AutonomousSignals? signals)
    {
        int attempt = _scheduler.AttemptOf(trigger);
        return new AutonomousRunInfo
        {
            Trigger = trigger.ToString(),
            Attempt = attempt > 0 ? attempt : (int?)null,
            GameTick = tick,
            Signals = signals
        };
    }

    internal static string FormatRetryDetail(AutonomousTrigger trigger, int attempt, AutonomousSignals signals)
    {
        bool alert = trigger is AutonomousTrigger.Alert or AutonomousTrigger.CriticalAlert;
        if (attempt <= 1) return alert ? "Active alerts: " + signals.Alerts : null;
        string diagnostic = trigger switch
        {
            AutonomousTrigger.Fire =>
                string.Create(CultureInfo.InvariantCulture, $"active fires. There are still {signals.FireCount} fire(s) burning after previous orders. Check if assigned colonists are drafting/extinguishing, or assign more colonists."),
            AutonomousTrigger.Alert or AutonomousTrigger.CriticalAlert => "alerts that are still active: " + signals.Alerts + ".",
            AutonomousTrigger.EmergencyMedical =>
                string.Create(CultureInfo.InvariantCulture, $"medical emergencies. {signals.DownedOrBleedingCount} colonist(s) remain downed or bleeding. Ensure doctors are prioritized and medicine is accessible."),
            AutonomousTrigger.FoodShortage =>
                // 會送進模型提示，數字固定用不隨地區變動的格式。
                string.Create(CultureInfo.InvariantCulture, $"food shortage. Current food per colonist is {(signals.FoodNutrition / Math.Max(1, signals.ColonistCount)):F1} nutrition."),
            _ => $"condition: {trigger}."
        };
        return string.Create(CultureInfo.InvariantCulture, $"This is retry #{attempt} for {diagnostic}");
    }
}
