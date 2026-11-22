using System;
using System.Collections.Generic;
using Verse;

namespace RimAgent.Agent;

/// <summary>自主模式一次自主任務的觸發原因。None 代表現在不該跑。</summary>
public enum AutonomousTrigger
{
    None,
    /// <summary>定期巡視：即使一切正常也定期推進一件事，節奏固定不開放給玩家調。</summary>
    Interval,
    Raid,
    RaidEnded,
    MentalBreak,
    FoodShortage,
    EmergencyMedical,
    Fire,
    /// <summary>出現新的抉擇信件或對話框；由 GameComponent 在 Update 偵測，不經排程器。</summary>
    Choice,
    // 以下為遊戲警報（右下角那排提示）：判斷規則跟著遊戲與其他模組走，不自己重寫。
    /// <summary>非紅色警報：基地在慢慢壞掉，先由決策模型篩過要不要叫主模型。</summary>
    Alert,
    /// <summary>紅色（Critical）警報：跟襲擊、火災一樣直接叫主模型。</summary>
    CriticalAlert,
    // 以下由 GameComponent 在 Update 起跑，不經排程器（需開啟存檔權限）。
    /// <summary>有殖民者死亡：由模型決定要不要讀檔重來。</summary>
    ColonistDied,
    /// <summary>剛讀檔重來：把教訓交給讀回來的這一局。</summary>
    Reloaded
}

public struct AutonomousSignals
{
    public int HostileCount { get; set; }
    public int NearMentalBreakCount { get; set; }
    public float FoodNutrition { get; set; }
    public int ColonistCount { get; set; }
    public int DownedOrBleedingCount { get; set; }
    public int FireCount { get; set; }
    public int AlertCount { get; set; }
    public int CriticalAlertCount { get; set; }
    /// <summary>目前的警報標題，給模型看；「[Critical] 標題; 標題」。</summary>
    public string Alerts { get; set; }
}

/// <summary>排程器設定（單位皆為 tick；2500 tick＝1 遊戲小時，60000 tick＝1 天）。</summary>
public struct AutonomousConfig
{
    public bool Enabled { get; set; }
    /// <summary>定期巡檢間隔。設定頁不再開放，呼叫端用 <see cref="AutonomousScheduler.HeartbeatTicks"/>。</summary>
    public int IntervalTicks { get; set; }
    public int WindowTicks { get; set; }
    /// <summary>兩次自主任務之間的最短冷卻間隔 ticks，0 代表關閉冷卻。</summary>
    public int CooldownTicks { get; set; }
    public bool OnRaid { get; set; }
    public bool OnMentalBreak { get; set; }
    public bool OnFoodShortage { get; set; }
    public bool OnEmergencyMedical { get; set; }
    public bool OnFire { get; set; }
    /// <summary>遊戲警報（一般與紅色）共用一組開關。</summary>
    public bool OnUpkeep { get; set; }
}

/// <summary>
/// 每個條件都是「跨過門檻就問一次，沒解決就退避重試」，<see cref="MaxAttempts"/> 次後放棄並回報，等條件解除才重新計次。
/// 定期巡檢（<see cref="AutonomousTrigger.Interval"/>）是優先序最低的保底，只負責在一切正常時推進一件事。
/// 存檔鍵沿用舊版，另加重試表（舊存檔沒有該鍵 → 所有條件從未觸發起算，向後相容）。
/// </summary>
public sealed class AutonomousScheduler : IExposable
{
    public const int CooldownTicks = 2500;

    /// <summary>定期巡檢節奏（6 遊戲小時）；刻意不進設定頁，旋鈕按到底就是關掉主動巡視。</summary>
    public const int HeartbeatTicks = 15000;

    public const int MaxAttempts = 3;

    public const int RetryBaseTicks = 2500;

    /// <summary>缺糧危急線：人均可食營養低於此值（殖民者一天約吃 1.6）。</summary>
    public const float FoodShortagePerColonist = 4f;

    public const float FoodWarningPerColonist = 8f;

    private const int WatcherCount = (int)AutonomousTrigger.CriticalAlert + 1;

    private long _windowStartTick;
    private bool _windowOpen;
    private int _toolsUsed;
    private int _runs;
    private long _lastRunTick = -CooldownTicks;
    private bool _prevRaid;
    private readonly Watcher[] _watch = new Watcher[WatcherCount];
    private readonly List<Condition> _conditions = new(WatcherCount);
    private readonly List<AutonomousTrigger> _giveUps = [];

    public int ToolsUsed => _toolsUsed;
    public int Runs => _runs;

    /// <summary>單一條件的嚴重度變化與重試狀態；未用過的欄位保持預設，舊存檔讀回來等同「所有條件都還沒被觸發過」。</summary>
    private struct Watcher
    {
        public int Level;
        public bool Fired;
        public bool GaveUp;
        public long LastTick;
        public int Attempts;
    }

    /// <summary>用嚴重度層級而不是布林：布林條件（0／1）跨過門檻立刻觸發，分級條件（缺糧 0／1／2）只在層級變高時立刻觸發；單一布林做不到第二點，0→1 與 1→2 都是 true，第二次惡化會被誤判成同一個邊緣。</summary>
    private struct Condition
    {
        public AutonomousTrigger Trigger;
        /// <summary>玩家是否開著這個條件的開關。關掉的仍會列出，好讓評估迴圈清空它的狀態。</summary>
        public bool Enabled;
        public int Level;
    }

    public AutonomousTrigger Evaluate(long tick, AutonomousSignals signals, AutonomousConfig config)
    {
        if (!config.Enabled) return AutonomousTrigger.None;
        ResetWindowIfNeeded(tick, config);
        // 冷卻期間完全不動條件狀態：被冷卻擋下的事件，等冷卻一過仍是新的邊緣，照樣會觸發。
        if (config.CooldownTicks > 0 && tick - _lastRunTick < config.CooldownTicks) return AutonomousTrigger.None;

        if (TryEvaluateRaid(tick, signals, config, out AutonomousTrigger raidTrigger))
        {
            return raidTrigger;
        }

        int foodTier = FoodTier(signals);
        foreach (Condition condition in Build(signals, config, foodTier))
        {
            if (TryEvaluateCondition(condition, tick, out AutonomousTrigger conditionTrigger))
            {
                return conditionTrigger;
            }
        }

        if (config.IntervalTicks > 0 && tick - _lastRunTick >= config.IntervalTicks)
        {
            return AutonomousTrigger.Interval;
        }
        return AutonomousTrigger.None;
    }

    private bool TryEvaluateRaid(long tick, AutonomousSignals signals, AutonomousConfig config, out AutonomousTrigger trigger)
    {
        trigger = AutonomousTrigger.None;
        if (!config.OnRaid)
        {
            // 關掉時不追蹤襲擊狀態：_prevRaid 若跟著更新，重新開啟就看不到上升邊，
            // 明明還在打卻再也不會觸發。清成 false 代表「開啟的那一刻就是新事件」。
            _prevRaid = false;
            _watch[(int)AutonomousTrigger.Raid] = default(Watcher);
            _watch[(int)AutonomousTrigger.RaidEnded] = default(Watcher);
            return false;
        }

        // 襲擊是雙向條件：上升邊是 Raid，下降邊是 RaidEnded。
        // 兩邊都值得重試——敵人還在就再部署一次，收尾沒做完就再收一次。
        bool raid = signals.HostileCount > 0;
        if (raid != _prevRaid)
        {
            _prevRaid = raid;
            // 襲擊走 _prevRaid 的轉態判定，不吃迴圈的層級比較；這裡只把在場狀態記進去，
            // 讓存檔後重試退避的計算維持一致。
            _watch[(int)AutonomousTrigger.Raid].Level = raid ? 1 : 0;
            trigger = Armed(raid ? AutonomousTrigger.Raid : AutonomousTrigger.RaidEnded, tick);
            return true;
        }

        AutonomousTrigger retry = Retry(raid ? AutonomousTrigger.Raid : AutonomousTrigger.RaidEnded, tick);
        if (retry != AutonomousTrigger.None)
        {
            trigger = retry;
            return true;
        }

        return false;
    }

    private bool TryEvaluateCondition(Condition condition, long tick, out AutonomousTrigger trigger)
    {
        trigger = AutonomousTrigger.None;
        ref Watcher w = ref _watch[(int)condition.Trigger];
        if (!condition.Enabled)
        {
            // 開關被關掉：狀態要立刻清空。留著會讓玩家把開關關掉再開時，
            // 條件還沒恢復就先吃到「次數用完」的舊狀態，之後再也不處理也沒人知道。
            w = default(Watcher);
            return false;
        }
        int previousLevel = w.Level;
        w.Level = condition.Level;
        if (condition.Level <= 0)
        {
            w = default(Watcher);
            return false;
        }
        // 層級變高＝新情況惡化，立刻叫；持平代表上次已經看過，繼續走重試。
        if (condition.Level > previousLevel)
        {
            trigger = Armed(condition.Trigger, tick);
            return true;
        }
        AutonomousTrigger retry = Retry(condition.Trigger, tick);
        if (retry != AutonomousTrigger.None)
        {
            trigger = retry;
            return true;
        }
        return false;
    }

    /// <summary>依優先序列出所有待判斷的條件；關掉開關的也照樣列出（<see cref="Condition.Enabled"/> 為 false），讓這裡成為「哪個開關管哪個條件」的唯一對應處。</summary>
    private List<Condition> Build(AutonomousSignals signals, AutonomousConfig config, int foodTier)
    {
        List<Condition> list = _conditions;
        list.Clear();
        Flag(list, AutonomousTrigger.EmergencyMedical, config.OnEmergencyMedical, signals.DownedOrBleedingCount > 0);
        Flag(list, AutonomousTrigger.Fire, config.OnFire, signals.FireCount > 0);
        // 警報層級用數量：多出一個新警報就算惡化，立刻叫，不等重試退避。
        list.Add(new Condition { Trigger = AutonomousTrigger.CriticalAlert, Enabled = config.OnUpkeep, Level = signals.CriticalAlertCount });
        Flag(list, AutonomousTrigger.MentalBreak, config.OnMentalBreak, signals.NearMentalBreakCount > 0);
        // 缺糧是分級的：偏緊就準備，掉到危急要再叫一次。
        list.Add(new Condition { Trigger = AutonomousTrigger.FoodShortage, Enabled = config.OnFoodShortage, Level = foodTier });
        list.Add(new Condition { Trigger = AutonomousTrigger.Alert, Enabled = config.OnUpkeep, Level = signals.AlertCount });
        return list;
    }

    private static void Flag(List<Condition> list, AutonomousTrigger trigger, bool enabled, bool now)
    {
        list.Add(new Condition { Trigger = trigger, Enabled = enabled, Level = now ? 1 : 0 });
    }

    /// <summary>首次跨過門檻；不動 <c>Level</c>（評估迴圈剛寫進去的嚴重度），覆寫會讓缺糧從偏緊掉到危急被誤判成同一個邊緣。</summary>
    private AutonomousTrigger Armed(AutonomousTrigger trigger, long tick)
    {
        ref Watcher w = ref _watch[(int)trigger];
        w.Fired = true;
        w.GaveUp = false;
        w.LastTick = tick;
        w.Attempts = 1;
        return trigger;
    }

    private AutonomousTrigger Retry(AutonomousTrigger trigger, long tick)
    {
        ref Watcher w = ref _watch[(int)trigger];
        if (!w.Fired) return AutonomousTrigger.None;
        if (w.Attempts >= MaxAttempts)
        {
            if (!w.GaveUp)
            {
                w.GaveUp = true;
                _giveUps.Add(trigger);
            }
            return AutonomousTrigger.None;
        }
        if (tick - w.LastTick < RetryDelayTicks(w.Attempts)) return AutonomousTrigger.None;
        w.LastTick = tick;
        w.Attempts++;
        return trigger;
    }

    /// <summary>條件目前是第幾次嘗試（含首次）；定期巡視與抉擇不走重試表，回傳 0。</summary>
    public int AttemptOf(AutonomousTrigger trigger)
    {
        int index = (int)trigger;
        if (index < 0 || index >= WatcherCount) return 0;
        return _watch[index].Attempts;
    }

    public IReadOnlyList<AutonomousTrigger> TakeGiveUps()
    {
        if (_giveUps.Count == 0) return [];
        var copy = _giveUps.ToArray();
        _giveUps.Clear();
        return copy;
    }

    /// <summary>玩家主動中止進行中的自主任務：該條件在解除前不再重試，不經 <see cref="TakeGiveUps"/> 回報。定期巡視把心跳時鐘撥到現在；抉擇只對新 id 觸發一次，無需處理。冪等。</summary>
    public void Dismiss(AutonomousTrigger trigger, long tick)
    {
        if (trigger == AutonomousTrigger.None || trigger == AutonomousTrigger.Choice) return;
        if (trigger == AutonomousTrigger.Interval)
        {
            _lastRunTick = tick;
            return;
        }
        int index = (int)trigger;
        if (index < 0 || index >= WatcherCount) return;
        ref Watcher w = ref _watch[index];
        if (!w.Fired) return;
        w.Attempts = MaxAttempts;
        w.GaveUp = true;
    }

    /// <summary>System One 判定這次觸發暫不需要主 LLM：退回這次嘗試，不吃重試次數也不吃冷卻，從 <paramref name="tick"/> 起等第一次重試的間隔（1 遊戲小時）再問一次。</summary>
    public void Defer(AutonomousTrigger trigger, long tick)
    {
        int index = (int)trigger;
        if (index < 0 || index >= WatcherCount) return;
        ref Watcher w = ref _watch[index];
        if (!w.Fired) return;
        w.Attempts = Math.Max(0, w.Attempts - 1);
        w.LastTick = tick;
    }

    /// <summary><paramref name="tick"/> 傳任務結束的時刻，冷卻與定期巡視都從這裡起算。</summary>
    public void RecordRun(long tick, int toolCalls, AutonomousConfig config)
    {
        ResetWindowIfNeeded(tick, config);
        _lastRunTick = tick;
        _toolsUsed += toolCalls < 0 ? 0 : toolCalls;
        _runs++;
    }

    /// <summary>觸發條件改從結束時刻起算重試退避：一輪任務在高速下常跑掉十幾個遊戲小時，從觸發時刻算的話，重試一結束就到期，上一輪做的事還沒反映到訊號上就被當成失敗重來。</summary>
    public void RecordRunFinished(AutonomousTrigger trigger, long tick)
    {
        int index = (int)trigger;
        if (index < 0 || index >= WatcherCount) return;
        ref Watcher w = ref _watch[index];
        if (!w.Fired) return;
        w.LastTick = tick;
    }

    public void Reset()
    {
        _windowOpen = false;
        _toolsUsed = 0;
        _runs = 0;
        _lastRunTick = -CooldownTicks;
        _prevRaid = false;
        for (int i = 0; i < _watch.Length; i++) _watch[i] = default(Watcher);
        _giveUps.Clear();
    }

    /// <summary>讀檔時把載入的暫存實例倒回正式排程器（正式實例是唯讀欄位，不能直接 Scribe）。</summary>
    public void CopyTo(AutonomousScheduler other)
    {
        if (other == null) return;
        other._windowStartTick = _windowStartTick;
        other._windowOpen = _windowOpen;
        other._toolsUsed = Math.Max(0, _toolsUsed);
        other._runs = Math.Max(0, _runs);
        other._lastRunTick = _lastRunTick;
        other._prevRaid = _prevRaid;
        other._giveUps.Clear();
        for (int i = 0; i < _watch.Length; i++) other._watch[i] = _watch[i];
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref _windowStartTick, "windowStartTick");
        Scribe_Values.Look(ref _windowOpen, "windowOpen");
        Scribe_Values.Look(ref _toolsUsed, "toolsUsed");
        Scribe_Values.Look(ref _runs, "runs");
        Scribe_Values.Look(ref _lastRunTick, "lastRunTick");
        Scribe_Values.Look(ref _prevRaid, "prevRaid");
    }

    /// <summary>把重試表攤平成可存檔的清單（<c>readonly</c> 陣列沒法直接過 Scribe）；沒用過的條件不輸出。</summary>
    public IReadOnlyList<AutonomousWatch> CaptureWatch()
    {
        var capture = new List<AutonomousWatch>();
        for (int i = 0; i < _watch.Length; i++)
        {
            Watcher w = _watch[i];
            if (w.Level <= 0 && !w.Fired) continue;
            capture.Add(new AutonomousWatch
            {
                Trigger = i,
                Level = w.Level,
                Fired = w.Fired,
                GaveUp = w.GaveUp,
                LastTick = w.LastTick,
                Attempts = w.Attempts
            });
        }
        return capture;
    }

    public void ApplyWatch(IReadOnlyList<AutonomousWatch> states)
    {
        if (states == null) return;
        for (int i = 0; i < states.Count; i++)
        {
            AutonomousWatch state = states[i];
            if (state == null) continue;
            int index = state.Trigger;
            if (index <= 0 || index >= WatcherCount) continue;
            _watch[index] = new Watcher
            {
                Level = state.Level,
                Fired = state.Fired,
                GaveUp = state.GaveUp,
                LastTick = state.LastTick,
                Attempts = state.Attempts
            };
        }
    }

    /// <summary>第 N 次之後要等多久才重試：1、2、4 遊戲小時，封頂在 8 小時免得退避失控。</summary>
    private static int RetryDelayTicks(int attempts)
    {
        int shift = Math.Max(0, Math.Min(3, attempts - 1));
        return RetryBaseTicks << shift;
    }

    private void ResetWindowIfNeeded(long tick, AutonomousConfig config)
    {
        int window = config.WindowTicks > 0 ? config.WindowTicks : 60000;
        if (!_windowOpen || tick - _windowStartTick >= window)
        {
            _windowOpen = true;
            _windowStartTick = tick;
            _toolsUsed = 0;
            _runs = 0;
        }
    }

    /// <summary>缺糧分級：0 正常、1 偏緊、2 危急。分級變差才需要立刻叫醒模型。</summary>
    private static int FoodTier(AutonomousSignals signals)
    {
        if (signals.ColonistCount <= 0) return 0;
        float per = signals.FoodNutrition / signals.ColonistCount;
        if (per < FoodShortagePerColonist) return 2;
        if (per < FoodWarningPerColonist) return 1;
        return 0;
    }
}

/// <summary>重試表的存檔項目。欄位預設值即「尚未觸發」，舊存檔沒有這個型別時自然相容。</summary>
public class AutonomousWatch : IExposable
{
    #pragma warning disable S1104 // Required for RimWorld Scribe serialization via ref parameter
    public int Trigger;
    public int Level;
    public bool Fired;
    public bool GaveUp;
    public long LastTick;
    public int Attempts;
    #pragma warning restore S1104

    public void ExposeData()
    {
        Scribe_Values.Look(ref Trigger, "trigger", 0);
        Scribe_Values.Look(ref Level, "level", 0);
        Scribe_Values.Look(ref Fired, "fired");
        Scribe_Values.Look(ref GaveUp, "gaveUp");
        Scribe_Values.Look(ref LastTick, "lastTick");
        Scribe_Values.Look(ref Attempts, "attempts", 0);
    }
}
