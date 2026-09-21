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
        Fire
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
        /// <summary>預算視窗長度（一天）。</summary>
        public int WindowTicks { get; set; }
        /// <summary>一個視窗內自主任務最多消耗的工具呼叫次數（每句上限另由 maxSteps 管）。</summary>
        public int BudgetPerWindow { get; set; }
        /// <summary>是否不設預算上限（自主模式為 true）。</summary>
        public bool UnlimitedBudget { get; set; }
        /// <summary>兩次自主任務之間的最短冷卻間隔 ticks，0 代表關閉冷卻。</summary>
        public int CooldownTicks { get; set; }
        public bool OnRaid { get; set; }
        public bool OnMentalBreak { get; set; }
        public bool OnFoodShortage { get; set; }
        public bool OnEmergencyMedical { get; set; }
        public bool OnFire { get; set; }
    }

    /// <summary>存檔用的自主排程器狀態；避免讀檔後重置預算與邊緣偵測。</summary>
    public sealed class AutonomousSchedulerState : IExposable
    {
        public long WindowStartTick { get; set; }
        public bool WindowOpen { get; set; }
        public int ToolsUsed { get; set; }
        public int Runs { get; set; }
        public long LastRunTick { get; set; }
        public bool PrevRaid { get; set; }
        public bool PrevMental { get; set; }
        public bool PrevFoodOk { get; set; } = true;
        public bool PrevEmergency { get; set; }
        public bool PrevFire { get; set; }

        public void ExposeData()
        {
            long windowStartTick = WindowStartTick;
            bool windowOpen = WindowOpen;
            int toolsUsed = ToolsUsed;
            int runs = Runs;
            long lastRunTick = LastRunTick;
            bool prevRaid = PrevRaid;
            bool prevMental = PrevMental;
            bool prevFoodOk = PrevFoodOk;
            bool prevEmergency = PrevEmergency;
            bool prevFire = PrevFire;
            Scribe_Values.Look(ref windowStartTick, "windowStartTick");
            Scribe_Values.Look(ref windowOpen, "windowOpen");
            Scribe_Values.Look(ref toolsUsed, "toolsUsed");
            Scribe_Values.Look(ref runs, "runs");
            Scribe_Values.Look(ref lastRunTick, "lastRunTick");
            Scribe_Values.Look(ref prevRaid, "prevRaid");
            Scribe_Values.Look(ref prevMental, "prevMental");
            Scribe_Values.Look(ref prevFoodOk, "prevFoodOk", true);
            Scribe_Values.Look(ref prevEmergency, "prevEmergency");
            Scribe_Values.Look(ref prevFire, "prevFire");
            WindowStartTick = windowStartTick;
            WindowOpen = windowOpen;
            ToolsUsed = toolsUsed;
            Runs = runs;
            LastRunTick = lastRunTick;
            PrevRaid = prevRaid;
            PrevMental = prevMental;
            PrevFoodOk = prevFoodOk;
            PrevEmergency = prevEmergency;
            PrevFire = prevFire;
        }
    }

    /// <summary>
    /// 自主排程器：純邏輯，不碰遊戲 API，方便測試。
    /// 語義：事件採邊緣觸發（無→有才跑一次），任何兩次自主任務間至少隔 <see cref="CooldownTicks"/>，
    /// 一個視窗內的工具呼叫總數不超過預算。觸發優先序：襲擊＞心情＞缺糧＞間隔。
    /// </summary>
    public sealed class AutonomousScheduler
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

        public int RemainingBudget(AutonomousConfig config)
        {
            if (config.UnlimitedBudget) return int.MaxValue;
            return Math.Max(0, config.BudgetPerWindow - _toolsUsed);
        }

        /// <summary>現在是否該起一次自主任務；回傳觸發原因或 None。</summary>
        public AutonomousTrigger Evaluate(long tick, AutonomousSignals signals, AutonomousConfig config)
        {
            if (!config.Enabled) return AutonomousTrigger.None;
            ResetWindowIfNeeded(tick, config);
            if (!config.UnlimitedBudget && _toolsUsed >= config.BudgetPerWindow) return AutonomousTrigger.None;
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

            _prevRaid = raid;
            _prevMental = mental;
            _prevFoodOk = foodOk;
            _prevEmergency = emergency;
            _prevFire = fire;
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

        public AutonomousSchedulerState CaptureState()
        {
            return new AutonomousSchedulerState
            {
                WindowStartTick = _windowStartTick, WindowOpen = _windowOpen, ToolsUsed = _toolsUsed, Runs = _runs,
                LastRunTick = _lastRunTick, PrevRaid = _prevRaid, PrevMental = _prevMental, PrevFoodOk = _prevFoodOk,
                PrevEmergency = _prevEmergency, PrevFire = _prevFire
            };
        }

        public void RestoreState(AutonomousSchedulerState state)
        {
            if (state == null) return;
            _windowStartTick = state.WindowStartTick;
            _windowOpen = state.WindowOpen;
            _toolsUsed = Math.Max(0, state.ToolsUsed);
            _runs = Math.Max(0, state.Runs);
            _lastRunTick = state.LastRunTick;
            _prevRaid = state.PrevRaid;
            _prevMental = state.PrevMental;
            _prevFoodOk = state.PrevFoodOk;
            _prevEmergency = state.PrevEmergency;
            _prevFire = state.PrevFire;
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
