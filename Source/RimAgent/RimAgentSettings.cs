using RimAgent.Tools;
using Verse;

namespace RimAgent
{
    /// <summary>玩家可調的設定：十二類工具的開關與單句指令的步數上限。</summary>
    public class RimAgentSettings : ModSettings
    {
        public const int DefaultMaxSteps = 8;
        public const int MinSteps = 1;
        public const int MaxSteps = 20;

        public bool allowObserve = true;
        public bool allowWork = true;
        public bool allowBuild;
        public bool allowCombat;
        public bool allowTrade;
        public bool allowMedical;
        public bool allowPrisoner;
        public bool allowAnimal;
        public bool allowZone;
        public bool allowProduction;
        public bool allowTime;
        public bool allowMCP;
        public int maxSteps = DefaultMaxSteps;
        /// <summary>
        /// 「需確認」模式：開啟後 Work／Build／Combat 命令先回傳預覽，玩家按確認才執行。
        /// 查詢不受影響。
        /// </summary>
        public bool requireConfirmation;

        // 自主模式（排程器）：全局開關與配額在設定頁，玩家設的長期目標跟著存檔走。
        public const int DefaultAutoIntervalHours = 6;
        public const int MinAutoIntervalHours = 1;
        public const int MaxAutoIntervalHours = 24;
        public const int DefaultAutoBudget = 60;
        public const int MinAutoBudget = 10;
        public const int MaxAutoBudget = 300;

        public bool allowAutonomous;
        public int autonomousIntervalHours = DefaultAutoIntervalHours;
        public int autonomousBudgetPerDay = DefaultAutoBudget;
        public bool autoOnRaid = true;
        public bool autoOnMentalBreak = true;
        public bool autoOnFoodShortage = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref allowObserve, "allowObserve", true);
            Scribe_Values.Look(ref allowWork, "allowWork", true);
            Scribe_Values.Look(ref allowBuild, "allowBuild", false);
            Scribe_Values.Look(ref allowCombat, "allowCombat", false);
            Scribe_Values.Look(ref allowTrade, "allowTrade", false);
            Scribe_Values.Look(ref allowMedical, "allowMedical", false);
            Scribe_Values.Look(ref allowPrisoner, "allowPrisoner", false);
            Scribe_Values.Look(ref allowAnimal, "allowAnimal", false);
            Scribe_Values.Look(ref allowZone, "allowZone", false);
            Scribe_Values.Look(ref allowProduction, "allowProduction", false);
            Scribe_Values.Look(ref allowTime, "allowTime", false);
            Scribe_Values.Look(ref allowMCP, "allowMCP", false);
            Scribe_Values.Look(ref maxSteps, "maxSteps", DefaultMaxSteps);
            Scribe_Values.Look(ref requireConfirmation, "requireConfirmation", false);
            Scribe_Values.Look(ref allowAutonomous, "allowAutonomous", false);
            Scribe_Values.Look(ref autonomousIntervalHours, "autonomousIntervalHours", DefaultAutoIntervalHours);
            Scribe_Values.Look(ref autonomousBudgetPerDay, "autonomousBudgetPerDay", DefaultAutoBudget);
            Scribe_Values.Look(ref autoOnRaid, "autoOnRaid", true);
            Scribe_Values.Look(ref autoOnMentalBreak, "autoOnMentalBreak", true);
            Scribe_Values.Look(ref autoOnFoodShortage, "autoOnFoodShortage", true);
        }

        public ToolPermissions ToPermissions()
        {
            return new ToolPermissions
            {
                Observe = allowObserve,
                Work = allowWork,
                Build = allowBuild,
                Combat = allowCombat,
                Trade = allowTrade,
                Medical = allowMedical,
                Prisoner = allowPrisoner,
                Animal = allowAnimal,
                Zone = allowZone,
                Production = allowProduction,
                Time = allowTime,
                Mcp = allowMCP
            };
        }
    }
}
