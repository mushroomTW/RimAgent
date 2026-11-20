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
    /// <summary>
    /// 日常自動調速：規則判定安全才加速（要開幾倍問決策模型），出事降回正常。
    /// Agent 或目標正在跑時不碰，速度由它們自己管。
    /// </summary>
    private void TickAutoTimeSpeed()
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || !settings.autoTimeSpeed || DateTime.UtcNow < _nextSpeedCheckAt) return;
        _nextSpeedCheckAt = DateTime.UtcNow.AddSeconds(SpeedCheckSeconds);
        if (_autoRunning || _goalRunning || Find.CurrentMap == null) return;
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

    /// <summary>
    /// 自主模式下出現新的抉擇信件或對話框就起一輪自主任務。放在 Update 而不是 Tick：
    /// 對話框會暫停遊戲，暫停時 GameComponentTick 不跑，只看 tick 會一路卡到玩家回來。
    /// 也不走排程器的冷卻：暫停中 tick 不前進，冷卻永遠等不完。
    /// </summary>
    private void TryStartChoiceRun()
    {
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || !settings.autonomousCare || DateTime.UtcNow < _nextChoiceCheckAt) return;
        _nextChoiceCheckAt = DateTime.UtcNow.AddSeconds(ChoiceCheckSeconds);
        if (_autoRunning || _goalRunning || Find.CurrentMap == null) return;
        if (Find.TickManager.TicksGame < _autoHoldUntilTick) return;
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
            _autoRunning = true;
            // 傳快照：代答後要拿「原本有哪些抉擇」比對剩下的，不能被即時清單改掉。
            _ = RunChoiceGuardedAsync(session, [.. choices], Find.TickManager.TicksGame, ToConfig(settings));
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent choice check failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 重試用完而放棄的條件要讓玩家知道，否則會以為 agent 還在處理。
    /// 條件沒解除前不再重試；解除後排程器會重新計次。
    /// </summary>
    private void ReportGiveUps(ColonyAgentSession session)
    {
        IReadOnlyList<AutonomousTrigger> giveUps = _scheduler.TakeGiveUps();
        for (int i = 0; i < giveUps.Count; i++)
        {
            WarnOnce("RimAgent autonomous task gave up after " + AutonomousScheduler.MaxAttempts + " attempts: " + giveUps[i]);
            session.Transcript.Add(new TranscriptEntry
            {
                Kind = TranscriptKind.Notice,
                Text = "Autonomous task stopped retrying after " + AutonomousScheduler.MaxAttempts +
                       " attempts: " + DescribeGiveUp(giveUps[i]) +
                       ". It will pick this up again once the situation clears."
            });
        }
    }

    /// <summary>放棄通知裡的人話名稱；與自主任務提示詞同一套說法。</summary>
    private static string DescribeGiveUp(AutonomousTrigger trigger)
    {
        return trigger switch
        {
            AutonomousTrigger.Fire => "the fire",
            AutonomousTrigger.EmergencyMedical => "the downed or bleeding colonist",
            AutonomousTrigger.MentalBreak => "the colonist near a mental break",
            AutonomousTrigger.FoodShortage => "the food shortage",
            AutonomousTrigger.Cold => "the dangerous temperatures",
            AutonomousTrigger.BedShortage => "the colonists without beds",
            AutonomousTrigger.OutdoorsBed => "the beds outdoors",
            AutonomousTrigger.PowerOutage => "the power outage",
            AutonomousTrigger.Research => "the missing research project",
            AutonomousTrigger.LowMood => "the low moods",
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
            // 主動照顧一律回應所有事件，不再逐項開關。
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

    /// <summary>UI 的停止鍵：取消進行中的自主任務（玩家指令另有自己的 token）。收尾憑 token 壓住重試，見 RunAutonomousAsync。</summary>
    public void StopAutonomousRun()
    {
        _autoCts?.Cancel();
    }

    /// <summary>關閉主動照顧時停止任務；重新開啟後仍可處理尚未解決的抉擇。</summary>
    public void DisableAutonomousCare()
    {
        StopAutonomousRun();
        _seenChoices.Clear();
    }

    /// <summary>具備分流檢查與防護機制的自主任務非同步執行進入點。</summary>
    /// <param name="session">當前殖民地 Agent 對話工作階段。</param>
    /// <param name="trigger">自主任務觸發類型。</param>
    /// <param name="tick">觸發時的遊戲 tick。</param>
    /// <param name="config">自主任務設定。</param>
    /// <param name="signals">自主任務信號；可為 null。</param>
    /// <param name="detail">接在任務文案後面的這一次的具體情況（誰死了、讀檔教訓）；沒有就是 null。</param>
    private async Task RunAutonomousGuardedAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config, AutonomousSignals? signals, string detail = null)
    {
        // 收尾只准清掉自己那一代；讀檔後新的一輪可能已經開始，舊的 finally 不能把它的旗標關掉。
        int generation = _autoGeneration;
        CancellationToken token = BeginAutoCts().Token;
        try
        {
            if (signals.HasValue && await TriageSkipsAsync(session, trigger, signals.Value, tick, generation, token)) return;
            if (generation != _autoGeneration) return;
            await RunAutonomousAsync(session, trigger, tick, config, signals, generation, detail);
        }
        finally
        {
            if (generation == _autoGeneration) _autoRunning = false;
        }
    }

    /// <summary>
    /// 抉擇先交給 System One 代答；它答不了（要額外輸入、信心不足、自動模式風險檢查不放行）
    /// 或沒設定決策模型的，照舊起一輪主 LLM 的抉擇任務。代答後才冒出的新對話框留給下一次檢查。
    /// </summary>
    private async Task RunChoiceGuardedAsync(ColonyAgentSession session, IReadOnlyList<PendingChoiceInfo> choices, int tick, AutonomousConfig config)
    {
        int generation = _autoGeneration;
        CancellationToken token = BeginAutoCts().Token;
        try
        {
            HashSet<string> answered = await AnswerChoicesWithSystemOneAsync(session, choices, generation, token);
            if (generation != _autoGeneration) return;
            bool leftover = _autoWorld.GetPendingChoices().Any(c => choices.Any(o => o.Id == c.Id) && !answered.Contains(c.Id));
            if (answered.Count > 0 && !leftover) return;
            await RunAutonomousAsync(session, AutonomousTrigger.Choice, tick, config, null, generation, null);
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent choice run failed: " + ex.Message);
        }
        finally
        {
            if (generation == _autoGeneration) _autoRunning = false;
        }
    }

    /// <summary>
    /// System One 代答：模式要跟主 LLM 一樣能免確認按選項（完整權限、執行模式且未要求自主重大動作確認），
    /// 自動模式另經同一個風險分類器放行。每個成功的代答都寫進對話、執行紀錄，並在下一句交給主 LLM。
    /// 回傳成功代答的抉擇 id。
    /// </summary>
    private async Task<HashSet<string>> AnswerChoicesWithSystemOneAsync(ColonyAgentSession session, IReadOnlyList<PendingChoiceInfo> choices, int generation, CancellationToken cancellationToken)
    {
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RimAgentSettings settings = RimAgentMod.Settings;
        if (settings == null || !settings.decisionAnswerChoices) return answered;
        AgentMode mode = session.Mode;
        bool autoMode = mode == AgentMode.Auto;
        bool allowed = mode == AgentMode.Autonomous || autoMode
            || (mode == AgentMode.Execute && !settings.confirmAutonomousMajor);
        if (!allowed) return answered;

        string colonyState = DecisionAutonomousTriage.DescribeSignals(GatherSignals());
        IReadOnlyList<ChoiceDecision> decisions = await ChoiceResponder.DecideAsync(choices, colonyState, cancellationToken);
        if (generation != _autoGeneration) return answered;
        string authorization = autoMode ? AutonomousPrompts.Build(_pack?.Autonomous, AutonomousTrigger.Choice) : null;
        foreach (ChoiceDecision decision in decisions)
        {
            if (!await ProcessSystemOneChoiceAsync(session, decision, answered, authorization, autoMode, generation, cancellationToken)) return answered;
        }
        return answered;
    }

    /// <summary>代答單一個抉擇：確認抉擇仍存在、自主模式下風險分類、執行選項並記錄結果。回傳 false 表示應停止後續處理。</summary>
    private async Task<bool> ProcessSystemOneChoiceAsync(
        ColonyAgentSession session, ChoiceDecision decision, HashSet<string> answered, string authorization, bool autoMode, int generation, CancellationToken cancellationToken)
    {
        PendingChoiceInfo choice = decision.Choice;
        // 前一個代答可能已讓這個抉擇消失（同一串對話框）：只答還在的。
        if (!_autoWorld.GetPendingChoices().Any(c => string.Equals(c.Id, choice.Id, StringComparison.OrdinalIgnoreCase))) return true;
        string preview = $"would answer \"{choice.Title}\" with option {decision.Option.Number}: {decision.Option.Text}.";
        if (autoMode)
        {
            RiskVerdict verdict = await session.ClassifyRiskAsync(new RiskQuestion
            {
                Instruction = authorization ?? string.Empty,
                ToolName = "choose_option",
                Arguments = System.Text.Json.JsonSerializer.Serialize(new { choiceId = choice.Id, option = decision.Option.Number }),
                Preview = preview
            }, cancellationToken);
            if (generation != _autoGeneration) return false;
            if (verdict == null || verdict.ShouldReject) return true;
        }
        CommandResult result = _autoWorld.ChooseOption(choice.Id, decision.Option.Number);
        if (!result.Success) return true;
        answered.Add(choice.Id);
        string chosen = string.IsNullOrEmpty(result.Message) ? decision.Option.Text : result.Message;
        session.DecisionReports.Add($"Answered \"{choice.Title}\" with \"{chosen}\".");
        string notice = $"System One answered \"{choice.Title}\" with \"{chosen}\" (confidence {ToolText.Pct((float)decision.Confidence)}).";
        session.Transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Notice, Text = notice });
        LogSystemOne("choice", notice);
        return true;
    }

    /// <summary>
    /// 基地狀況觸發先問 System One；判定不必處理就退回這次嘗試、稍後再問，不叫主 LLM。
    /// 決策模型不可用時回傳 false（照常叫主 LLM）。
    /// </summary>
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
        if (generation != _autoGeneration || verdict == null || !verdict.Skip) return false;
        _scheduler.Defer(trigger, Current.Game != null ? Find.TickManager.TicksGame : tick);
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
            _session?.RunLogSink?.Invoke(new RunLogRecord
            {
                Source = "system_one",
                Mode = _session.Mode.ToString(),
                Instruction = kind,
                Outcome = RunLogRecord.Limit(text),
                FinishedUtc = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            WarnOnce("RimAgent run log failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 每一輪自主任務（含 System One 篩選與代答）進場時換一個新的 CTS，
    /// 避免讀檔後尚未建立（null）或沿用上一輪已取消的 token。
    /// </summary>
    private CancellationTokenSource BeginAutoCts()
    {
        // 不 Dispose 舊的：還在收尾的上一輪可能仍要讀它的 Token；交給 GC 即可（沒有計時器或等待把手）。
        _autoCts = new CancellationTokenSource();
        return _autoCts;
    }

    private async Task RunAutonomousAsync(ColonyAgentSession session, AutonomousTrigger trigger, int tick, AutonomousConfig config, AutonomousSignals? signals, int generation, string detail)
    {
        // 起跑前遊戲已被換掉（例如排程器在同一幀觸發後玩家立刻讀檔）：不碰任何狀態。
        if (generation != _autoGeneration) return;
        // 沿用 guard 進場時建立的這一輪 CTS：篩選或代答期間按下停止，主 LLM 這一輪也跟著取消。
        CancellationTokenSource cts = _autoCts;
        // 送出前先算好這句會拿到的編號：busy 旗標保證成功送出的那句取的是下一個號；
        // 送完才讀 CurrentInstructionId 的話，玩家在旗標釋放的瞬間送指令會讓我們數到別句。
        int instructionId = session.Transcript.CurrentInstructionId + 1;
        try
        {
            string instruction = BuildAutonomousInstruction(trigger);
            if (string.IsNullOrWhiteSpace(instruction)) return;
            if (!string.IsNullOrEmpty(detail)) instruction += "\n\n" + detail;
            // 不加 ConfigureAwait(false)：收尾的 RecordRun 要回主線程，
            // 免得與主線程的 Evaluate 同時改排程器狀態（起跑空窗另由 _autoRunning 擋住）。
            string reply = await session.SendAsync(
                instruction,
                autonomous: true,
                maxToolSteps: session.Mode == AgentMode.Autonomous ? null : AutonomousRunMaxSteps,
                reasoningOverride: ReasoningLevel.Low,
                autonomousInfo: RimAgentMod.Settings?.recordRuns == true ? BuildRunInfo(trigger, tick, signals) : null,
                cancellationToken: cts.Token);
            HandleAutonomousReply(trigger, reply, generation);
        }
        catch (AgentBusyException)
        {
            // 玩家在同一幀搶先送了指令（session 被占用）：這次沒跑，不算一次自主任務、不吃冷卻。
            // 其他 InvalidOperationException 落到下面的通用 catch，照一般失敗記錄並套用冷卻。
            return;
        }
        catch (OperationCanceledException)
        {
            // 玩家主動停止自主任務（點擊停止或 /stop）：收尾統一壓住重試。
        }
        catch (Exception ex)
        {
            Log.Warning("RimAgent autonomous run failed: " + ex.Message);
        }
        // 讀檔／新局已把這個任務作廢：不要用舊遊戲的結果改新遊戲的排程器或冷卻。
        if (generation != _autoGeneration) return;
        RecordAutonomousRun(session, trigger, tick, cts, config, instructionId);
    }

    /// <summary>組裝自主任務指令；缺章節時警告並退回 Interval。</summary>
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

    /// <summary>收到自主任務回覆後，更新三分之一代答的最後回覆紀錄。</summary>
    private void HandleAutonomousReply(AutonomousTrigger trigger, string reply, int generation)
    {
        if (DecisionAutonomousTriage.IsTriaged(trigger) && generation == _autoGeneration && Current.Game != null)
        {
            _lastAutoReplies[trigger] = (reply ?? string.Empty, Find.TickManager.TicksGame);
            _triageNoticed.Remove(trigger);
        }
    }

    /// <summary>自主任務結束後的紀錄：冷卻、巡視退避、工具配額計數，以及設定錯誤後的保持。</summary>
    private void RecordAutonomousRun(ColonyAgentSession session, AutonomousTrigger trigger, int tick, CancellationTokenSource cts, AutonomousConfig config, int instructionId)
    {
        // 冷卻、定期巡視與重試退避都從任務結束起算：一輪在高速下常跑掉十幾個遊戲小時，
        // 從起跑算的話一結束就全部到期，上一輪的效果還沒反映到訊號上就接著再跑。
        // 已回到主線程（上面的 await 沒加 ConfigureAwait(false)），讀得到結束當下的 tick；
        // 任務期間玩家退回主選單時沒有遊戲可讀，這個排程器也跟著舊遊戲作廢，沿用起跑 tick 即可。
        int finishTick = Current.Game != null ? Find.TickManager.TicksGame : tick;
        // 玩家按了停止就壓住該條件的重試（解除前不再跑）：憑 token 判斷，
        // SendAsync 吃掉取消沒擲 OCE 時同樣生效；沒消耗工具配額就不吃冷卻（其他條件不受影響）。
        bool stopped = cts.IsCancellationRequested;
        if (stopped) _scheduler.Dismiss(trigger, finishTick);
        // 只數這一句的工具紀錄：transcript 會裁掉最舊的紀錄，前後相減會失準。
        int used = session.Transcript.Snapshot().Count(e =>
            e.Kind == TranscriptKind.Tool && e.InstructionId == instructionId && e.BudgetConsumed);
        if (used > 0 || !stopped)
        {
            _scheduler.RecordRun(finishTick, used, config);
        }
        _scheduler.RecordRunFinished(trigger, finishTick);
        HoldAfterConfigurationError(session, instructionId, tick);
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

    /// <summary>
    /// 自主任務重試時注入具體客觀訊號診斷，讓模型在自我改進閉環中得知前次處置之成效。
    /// 首次觸發（attempt <= 1）回傳 null。
    /// </summary>
    internal static string FormatRetryDetail(AutonomousTrigger trigger, int attempt, AutonomousSignals signals)
    {
        if (attempt <= 1) return null;
        string diagnostic = trigger switch
        {
            AutonomousTrigger.Fire =>
                $"active fires. There are still {signals.FireCount} fire(s) burning after previous orders. Check if assigned colonists are drafting/extinguishing, or assign more colonists.",
            AutonomousTrigger.Cold =>
                $"hypothermia risks. {signals.ColdColonistCount} colonist(s) remain dangerously cold. Check indoor heaters, fuel/power, or warm clothing.",
            AutonomousTrigger.EmergencyMedical =>
                $"medical emergencies. {signals.DownedOrBleedingCount} colonist(s) remain downed or bleeding. Ensure doctors are prioritized and medicine is accessible.",
            AutonomousTrigger.FoodShortage =>
                // 會送進模型提示，數字固定用不隨地區變動的格式。
                string.Create(CultureInfo.InvariantCulture, $"food shortage. Current food per colonist is {(signals.FoodNutrition / Math.Max(1, signals.ColonistCount)):F1} nutrition."),
            _ => $"condition: {trigger}."
        };
        return $"This is retry #{attempt} for {diagnostic}";
    }

    private void HoldAfterConfigurationError(ColonyAgentSession session, int instructionId, int tick)
    {
        if (session.Transcript.ErrorKindOf(instructionId) != AgentErrorKind.Configuration) return;
        _autoHoldUntilTick = tick + ConfigurationErrorHoldTicks;
        WarnOnce("RimAgent autonomous tasks paused for one in-game day after a provider configuration error.");
        session.Transcript.Add(new TranscriptEntry
        {
            Kind = TranscriptKind.Notice,
            Text = "Autonomous tasks are paused for one in-game day: the model provider reported a key, balance or availability problem. " +
                   "Fix it in the RimLLM Framework settings; player instructions still work."
        });
    }
}
