using System;
using Verse;

namespace RimAgent.Agent
{
    /// <summary>自主模式一次自主任務的觸發原因。None 代表現在不該跑。</summary>
    public enum AutonomousTrigger
    {
        None,
        Interval,
        Raid,
        RaidEnded,
        MentalBreak,
        FoodShortage,
        EmergencyMedical,
        Fire,
        /// <summary>出現新的抉擇信件或對話框；由 GameComponent 在 Update 偵測，不經排程器。</summary>
        Choice
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
    }

    /// <summary>排程器設定（單位皆為 tick；2500 tick＝1 遊戲小時，60000 tick＝1 天）。</summary>
    public struct AutonomousConfig
    {
        public bool Enabled { get; set; }
        /// <summary>例行巡檢間隔；0 代表關掉間隔觸發，只留事件觸發。</summary>
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
    }

    /// <summary>
    /// 自主排程器：純邏輯，不碰遊戲 API，方便測試。
    /// 語義：事件採邊緣觸發（無→有才跑一次），任何兩次自主任務間至少隔 <see cref="CooldownTicks"/>；
    /// 同時發生的事件依優先序逐次觸發，不互相吃掉。每輪工具呼叫上限由呼叫端控制，這裡只統計當日用量。
    /// 存檔鍵與舊版 AutonomousSchedulerState 完全相同，舊存檔直接相容。
    /// </summary>
    public sealed class AutonomousScheduler : IExposable
    {
        /// <summary>兩次自主任務的最短間隔（1 遊戲小時），避免觸發風暴。</summary>
        public const int CooldownTicks = 2500;

        /// <summary>缺糧線：人均可食營養低於此值視為短缺（殖民者一天約吃 1.6）。</summary>
        public const float FoodShortagePerColonist = 4f;

        private long _windowStartTick;
        private bool _windowOpen;
        private int _toolsUsed;
        private int _runs;
        private long _lastRunTick = -CooldownTicks;
        private bool _prevRaid;
        private bool _prevMental;
        private bool _prevFoodOk = true;
        private bool _prevEmergency;
        private bool _prevFire;

        public int ToolsUsed => _toolsUsed;
        public int Runs => _runs;

        /// <summary>現在是否該起一次自主任務；回傳觸發原因或 None。</summary>
        public AutonomousTrigger Evaluate(long tick, AutonomousSignals signals, AutonomousConfig config)
        {
            if (!config.Enabled) return AutonomousTrigger.None;
            ResetWindowIfNeeded(tick, config);
            if (config.CooldownTicks > 0 && tick - _lastRunTick < config.CooldownTicks) return AutonomousTrigger.None;

            bool raid = signals.HostileCount > 0;
            bool mental = signals.NearMentalBreakCount > 0;
            bool foodOk = !IsShortage(signals);
            bool emergency = signals.DownedOrBleedingCount > 0;
            bool fire = signals.FireCount > 0;
            AutonomousTrigger trigger = AutonomousTrigger.None;

            if (config.OnRaid && raid && !_prevRaid) trigger = AutonomousTrigger.Raid;
            else if (config.OnRaid && !raid && _prevRaid) trigger = AutonomousTrigger.RaidEnded;
            else if (config.OnEmergencyMedical && emergency && !_prevEmergency) trigger = AutonomousTrigger.EmergencyMedical;
            else if (config.OnFire && fire && !_prevFire) trigger = AutonomousTrigger.Fire;
            else if (config.OnMentalBreak && mental && !_prevMental) trigger = AutonomousTrigger.MentalBreak;
            else if (config.OnFoodShortage && !foodOk && _prevFoodOk) trigger = AutonomousTrigger.FoodShortage;
            else if (config.IntervalTicks > 0 && tick - _lastRunTick >= config.IntervalTicks) trigger = AutonomousTrigger.Interval;

            // 一次只起一個任務：沒輪到的邊緣先不記下，下次檢查再觸發，不被優先序較高的事件吃掉。
            if (!(config.OnRaid && raid != _prevRaid) || trigger == AutonomousTrigger.Raid || trigger == AutonomousTrigger.RaidEnded) _prevRaid = raid;
            if (!(config.OnEmergencyMedical && emergency && !_prevEmergency) || trigger == AutonomousTrigger.EmergencyMedical) _prevEmergency = emergency;
            if (!(config.OnFire && fire && !_prevFire) || trigger == AutonomousTrigger.Fire) _prevFire = fire;
            if (!(config.OnMentalBreak && mental && !_prevMental) || trigger == AutonomousTrigger.MentalBreak) _prevMental = mental;
            if (!(config.OnFoodShortage && !foodOk && _prevFoodOk) || trigger == AutonomousTrigger.FoodShortage) _prevFoodOk = foodOk;
            return trigger;
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
            _prevMental = false;
            _prevFoodOk = true;
            _prevEmergency = false;
            _prevFire = false;
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
            other._prevMental = _prevMental;
            other._prevFoodOk = _prevFoodOk;
            other._prevEmergency = _prevEmergency;
            other._prevFire = _prevFire;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref _windowStartTick, "windowStartTick");
            Scribe_Values.Look(ref _windowOpen, "windowOpen");
            Scribe_Values.Look(ref _toolsUsed, "toolsUsed");
            Scribe_Values.Look(ref _runs, "runs");
            Scribe_Values.Look(ref _lastRunTick, "lastRunTick");
            Scribe_Values.Look(ref _prevRaid, "prevRaid");
            Scribe_Values.Look(ref _prevMental, "prevMental");
            Scribe_Values.Look(ref _prevFoodOk, "prevFoodOk", true);
            Scribe_Values.Look(ref _prevEmergency, "prevEmergency");
            Scribe_Values.Look(ref _prevFire, "prevFire");
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

        private static bool IsShortage(AutonomousSignals signals)
        {
            if (signals.ColonistCount <= 0) return false;
            return signals.FoodNutrition < signals.ColonistCount * FoodShortagePerColonist;
        }
    }
}
