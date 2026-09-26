using System;
using System.Collections.Generic;
using Verse;

namespace RimAgent.Agent
{
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
        // 以下為基地狀況：條件成立就代表基地在慢慢壞掉，不會有人死亡也不會響警報，
        // 但不處理就會累積成前面那些應急事件。
        /// <summary>環境溫度已對某位殖民者構成低溫風險。</summary>
        Cold,
        /// <summary>有人沒有分到床。</summary>
        BedShortage,
        /// <summary>床鋪露在戶外（房間不存在或遊戲判定為戶外）。</summary>
        OutdoorsBed,
        /// <summary>基地沒有東西在通電運作。</summary>
        PowerOutage,
        /// <summary>沒有進行中的研究項目。</summary>
        Research,
        /// <summary>心情低落但尚未逼近崩潰。</summary>
        LowMood
    }

    /// <summary>排程器做判斷用的殖民地訊號（呼叫端在主線程收集的純資料）。</summary>
    public struct AutonomousSignals
    {
        public int HostileCount { get; set; }
        public int NearMentalBreakCount { get; set; }
        public float FoodNutrition { get; set; }
        public int ColonistCount { get; set; }
        public int DownedOrBleedingCount { get; set; }
        public int FireCount { get; set; }
        /// <summary>環境溫度對某位殖民者已構成低溫風險。</summary>
        public int ColdColonistCount { get; set; }
        /// <summary>沒有分到床的殖民者數。</summary>
        public int BedlessCount { get; set; }
        /// <summary>床鋪所在房間不存在或被遊戲判定為戶外的殖民者數。</summary>
        public int OutdoorsBedCount { get; set; }
        /// <summary>心情已低到影響生產但尚未逼近崩潰的殖民者數。</summary>
        public int LowMoodCount { get; set; }
        /// <summary>是否有進行中的研究項目。</summary>
        public bool HasActiveResearch { get; set; }
        /// <summary>任一地圖有東西真的通電在跑。</summary>
        public bool AnyWorkingPower { get; set; }
    }

    /// <summary>排程器設定（單位皆為 tick；2500 tick＝1 遊戲小時，60000 tick＝1 天）。</summary>
    public struct AutonomousConfig
    {
        public bool Enabled { get; set; }
        /// <summary>定期巡檢間隔。設定頁不再開放，呼叫端用 <see cref="AutonomousScheduler.HeartbeatTicks"/>。</summary>
        public int IntervalTicks { get; set; }
        /// <summary>工具呼叫統計視窗長度（一天）。</summary>
        public int WindowTicks { get; set; }
        /// <summary>兩次自主任務之間的最短冷卻間隔 ticks，0 代表關閉冷卻。</summary>
        public int CooldownTicks { get; set; }
        public bool OnRaid { get; set; }
        public bool OnMentalBreak { get; set; }
        public bool OnFoodShortage { get; set; }
        public bool OnEmergencyMedical { get; set; }
        public bool OnFire { get; set; }
        /// <summary>基地狀況（受寒、沒床、露宿、停電、停研、心情低落）共用一組開關。</summary>
        public bool OnUpkeep { get; set; }
    }

    /// <summary>
    /// 自主排程器：純邏輯，不碰遊戲 API，方便測試。
    ///
    /// 每個條件都是「跨過門檻就問一次，沒解決就退避重試」：舊版只在無→有的那一次邊緣觸發，
    /// 火還在燒就再也不管了，等於放棄在燒的建築。現在問題持續存在就會重試，退避 1→2 遊戲小時，
    /// <see cref="MaxAttempts"/> 次後放棄並回報，等條件解除才重新計次。
    ///
    /// 定期巡檢（<see cref="AutonomousTrigger.Interval"/>）是優先序最低的保底：
    /// 它不負責發現問題，只負責在一切正常時推進一件事。節奏由 <see cref="HeartbeatTicks"/> 固定。
    ///
    /// 存檔鍵沿用舊版，另加重試表（舊存檔沒有該鍵 → 所有條件從未觸發起算，向後相容）。
    /// </summary>
    public sealed class AutonomousScheduler : IExposable
    {
        /// <summary>兩次自主任務的最短間隔（1 遊戲小時），避免觸發風暴。</summary>
        public const int CooldownTicks = 2500;

        /// <summary>
        /// 定期巡檢節奏（6 遊戲小時）。刻意不進設定頁：這是 agent 的運轉節奏而非「多吵」的偏好；
        /// 讓玩家調只會變成一個看不出在做什麼的旋鈕，按到底就是關掉主動巡視。
        /// </summary>
        public const int HeartbeatTicks = 15000;

        /// <summary>單一條件最多嘗試幾次（含首次），之後放棄並回報。</summary>
        public const int MaxAttempts = 3;

        /// <summary>第一次重試的等待（1 遊戲小時），之後每次翻倍。</summary>
        public const int RetryBaseTicks = 2500;

        /// <summary>缺糧危急線：人均可食營養低於此值（殖民者一天約吃 1.6）。</summary>
        public const float FoodShortagePerColonist = 4f;

        /// <summary>缺糧警告線；跨過它就先準備，不必等到快餓死才動。</summary>
        public const float FoodWarningPerColonist = 8f;

        /// <summary>心情低於此比例就算影響生產，不必等到逼近崩潰。</summary>
        public const float LowMoodThreshold = 0.5f;

        /// <summary>人口少於此數不要求有研究進行中。</summary>
        public const int MinColonistsForResearch = 2;

        private const int WatcherCount = (int)AutonomousTrigger.LowMood + 1;

        private long _windowStartTick;
        private bool _windowOpen;
        private int _toolsUsed;
        private int _runs;
        private long _lastRunTick = -CooldownTicks;
        private bool _prevRaid;
        private readonly Watcher[] _watch = new Watcher[WatcherCount];
        private readonly List<Condition> _conditions = new List<Condition>(WatcherCount);
        private readonly List<AutonomousTrigger> _giveUps = new List<AutonomousTrigger>();

        public int ToolsUsed => _toolsUsed;
        public int Runs => _runs;

        /// <summary>
        /// 單一條件的嚴重度變化與重試狀態。未用過的欄位保持預設（Level=0、Fired=false），
        /// 所以舊存檔讀回來等同「所有條件都還沒被觸發過」。
        /// </summary>
        private struct Watcher
        {
            /// <summary>上一次評估時的嚴重度；0 代表問題不存在。</summary>
            public int Level;
            public bool Fired;
            public bool GaveUp;
            public long LastTick;
            public int Attempts;
        }

        /// <summary>
        /// 一個待判斷的條件。<see cref="Level"/> 是嚴重度（越高越糟，0 代表沒問題）。
        /// 用層級而不是布林，才能同時表達兩件事：
        /// 布林條件（0／1）跨過門檻立刻觸發，問題持續就留著重試；
        /// 分級條件（缺糧 0／1／2）只在「層級變高」時立刻觸發，層級持平則繼續重試。
        /// 用單一布林做不到第二點——0→1 和 1→2 都是 true，第二次惡化會被誤判成同一個邊緣。
        /// </summary>
        private struct Condition
        {
            public AutonomousTrigger Trigger;
            /// <summary>玩家是否開著這個條件的開關。關掉的仍會列出，好讓評估迴圈清空它的狀態。</summary>
            public bool Enabled;
            public int Level;
        }

        /// <summary>現在是否該起一次自主任務；回傳觸發原因或 None。</summary>
        public AutonomousTrigger Evaluate(long tick, AutonomousSignals signals, AutonomousConfig config)
        {
            if (!config.Enabled) return AutonomousTrigger.None;
            ResetWindowIfNeeded(tick, config);
            // 冷卻期間完全不動條件狀態：被冷卻擋下的事件，等冷卻一過仍是新的邊緣，照樣會觸發。
            if (config.CooldownTicks > 0 && tick - _lastRunTick < config.CooldownTicks) return AutonomousTrigger.None;

            // 襲擊是雙向條件：上升邊是 Raid，下降邊是 RaidEnded。
            // 兩邊都值得重試——敵人還在就再部署一次，收尾沒做完就再收一次。
            bool raid = signals.HostileCount > 0;
            if (config.OnRaid)
            {
                if (raid != _prevRaid)
                {
                    _prevRaid = raid;
                    // 襲擊走 _prevRaid 的轉態判定，不吃迴圈的層級比較；這裡只把在場狀態記進去，
                    // 讓存檔後重試退避的計算維持一致。
                    _watch[(int)AutonomousTrigger.Raid].Level = raid ? 1 : 0;
                    return Armed(raid ? AutonomousTrigger.Raid : AutonomousTrigger.RaidEnded, tick);
                }
                AutonomousTrigger retry = Retry(raid ? AutonomousTrigger.Raid : AutonomousTrigger.RaidEnded, tick);
                if (retry != AutonomousTrigger.None) return retry;
            }
            else
            {
                // 關掉時不追蹤襲擊狀態：_prevRaid 若跟著更新，重新開啟就看不到上升邊，
                // 明明還在打卻再也不會觸發。清成 false 代表「開啟的那一刻就是新事件」。
                _prevRaid = false;
                _watch[(int)AutonomousTrigger.Raid] = default(Watcher);
                _watch[(int)AutonomousTrigger.RaidEnded] = default(Watcher);
            }

            int foodTier = FoodTier(signals);
            foreach (Condition condition in Build(signals, config, foodTier))
            {
                ref Watcher w = ref _watch[(int)condition.Trigger];
                if (!condition.Enabled)
                {
                    // 開關被關掉：狀態要立刻清空。留著會讓玩家把開關關掉再開時，
                    // 條件還沒恢復就先吃到「次數用完」的舊狀態，之後再也不處理也沒人知道。
                    w = default(Watcher);
                    continue;
                }
                int previousLevel = w.Level;
                w.Level = condition.Level;
                if (condition.Level <= 0)
                {
                    // 問題解除：清空重試狀態，等它再出現時算新的一次事件。
                    w = default(Watcher);
                    continue;
                }
                // 層級變高＝新情況惡化，立刻叫；持平代表上次已經看過，繼續走重試。
                if (condition.Level > previousLevel) return Armed(condition.Trigger, tick);
                AutonomousTrigger retry = Retry(condition.Trigger, tick);
                if (retry != AutonomousTrigger.None) return retry;
            }

            // 優先序最低的保底：一切正常也定期推進一件事。
            if (config.IntervalTicks > 0 && tick - _lastRunTick >= config.IntervalTicks)
            {
                return AutonomousTrigger.Interval;
            }
            return AutonomousTrigger.None;
        }

        /// <summary>
        /// 依優先序列出所有待判斷的條件；一次只回傳一個，沒輪到的留到下次（不被優先序較高的吃掉）。
        /// 開關關掉的條件照樣列出（<see cref="Condition.Enabled"/> 為 false），讓這裡成為
        /// 「哪個開關管哪個條件」的唯一對應處——新增觸發時不會忘了在別處補清狀態的邏輯。
        /// 純函式：只組清單，不動任何狀態。
        /// </summary>
        private List<Condition> Build(AutonomousSignals signals, AutonomousConfig config, int foodTier)
        {
            List<Condition> list = _conditions;
            list.Clear();
            Flag(list, AutonomousTrigger.EmergencyMedical, config.OnEmergencyMedical, signals.DownedOrBleedingCount > 0);
            Flag(list, AutonomousTrigger.Fire, config.OnFire, signals.FireCount > 0);
            Flag(list, AutonomousTrigger.Cold, config.OnUpkeep, signals.ColdColonistCount > 0);
            Flag(list, AutonomousTrigger.BedShortage, config.OnUpkeep, signals.BedlessCount > 0);
            Flag(list, AutonomousTrigger.OutdoorsBed, config.OnUpkeep, signals.OutdoorsBedCount > 0);
            Flag(list, AutonomousTrigger.PowerOutage, config.OnUpkeep, signals.ColonistCount > 0 && !signals.AnyWorkingPower);
            Flag(list, AutonomousTrigger.Research, config.OnUpkeep, signals.ColonistCount >= MinColonistsForResearch && !signals.HasActiveResearch);
            Flag(list, AutonomousTrigger.MentalBreak, config.OnMentalBreak, signals.NearMentalBreakCount > 0);
            // 缺糧是分級的：偏緊就準備，掉到危急要再叫一次。
            list.Add(new Condition { Trigger = AutonomousTrigger.FoodShortage, Enabled = config.OnFoodShortage, Level = foodTier });
            Flag(list, AutonomousTrigger.LowMood, config.OnUpkeep, signals.LowMoodCount > 0);
            return list;
        }

        /// <summary>單一門檻的布林條件：成立為 1、不成立為 0。</summary>
        private static void Flag(List<Condition> list, AutonomousTrigger trigger, bool enabled, bool now)
        {
            list.Add(new Condition { Trigger = trigger, Enabled = enabled, Level = now ? 1 : 0 });
        }

        /// <summary>
        /// 首次跨過門檻：記下這是第一次嘗試並回傳觸發原因。
        /// 不動 <c>Level</c>——那是評估迴圈剛寫進去的嚴重度，在這裡覆寫會讓下一次
        /// 層級變差（例如缺糧從偏緊掉到危急）誤判成同一個邊緣。
        /// </summary>
        private AutonomousTrigger Armed(AutonomousTrigger trigger, long tick)
        {
            ref Watcher w = ref _watch[(int)trigger];
            w.Fired = true;
            w.GaveUp = false;
            w.LastTick = tick;
            w.Attempts = 1;
            return trigger;
        }

        /// <summary>
        /// 條件仍在成立：夠久就再試一次；用完 <see cref="MaxAttempts"/> 就放棄並回報一次。
        /// 尚未成立回傳 None（不觸發，狀態留給下次）。
        /// </summary>
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

        /// <summary>取走「重試用完而放棄」的條件，讓呼叫端告知玩家；讀完即清空。</summary>
        public IReadOnlyList<AutonomousTrigger> TakeGiveUps()
        {
            if (_giveUps.Count == 0) return Array.Empty<AutonomousTrigger>();
            var copy = _giveUps.ToArray();
            _giveUps.Clear();
            return copy;
        }

        /// <summary>自主任務結束後記錄：推進冷卻、累計本次消耗的工具呼叫次數。</summary>
        public void RecordRun(long tick, int toolCalls, AutonomousConfig config)
        {
            ResetWindowIfNeeded(tick, config);
            _lastRunTick = tick;
            _toolsUsed += toolCalls < 0 ? 0 : toolCalls;
            _runs++;
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

        /// <summary>
        /// 把重試表攤平成可存檔的清單（<c>readonly</c> 陣列沒法直接過 Scribe）。
        /// 沒用過的條件不輸出，舊存檔讀回空清單等同「所有條件都還沒被觸發過」。
        /// </summary>
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

        /// <summary>套用讀回的重試表，避免讀檔後每個未解決問題都從零開始重算次數。</summary>
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
        public int Trigger;
        /// <summary>上次評估時的嚴重度；0 代表問題當時不存在。</summary>
        public int Level;
        public bool Fired;
        public bool GaveUp;
        public long LastTick;
        public int Attempts;

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
}
