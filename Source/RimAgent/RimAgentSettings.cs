using RimAgent.Agent;
using RimAgent.Tools;
using Verse;

namespace RimAgent
{
    /// <summary>玩家可調的設定：執行模式、十二類工具的開關與單句指令的步數上限。</summary>
    public class RimAgentSettings : ModSettings
    {
        public const int DefaultMaxSteps = 8;
        public const int MinSteps = 1;
        public const int MaxSteps = 20;

#pragma warning disable S1104 // RimWorld ModSettings and Listing_Standard require public fields passed by ref
        public bool allowObserve = true;
        public bool allowWork = true;
        public bool allowBuild = true;
        public bool allowCombat = true;
        public bool allowTrade = true;
        public bool allowMedical = true;
        public bool allowPrisoner = true;
        public bool allowAnimal = true;
        public bool allowZone = true;
        public bool allowProduction = true;
        public bool allowTime = true;
        public bool allowMCP = true;
        public int maxSteps = DefaultMaxSteps;
        /// <summary>執行模式：詢問／確認／自動／自主，見 <see cref="AgentMode"/>。</summary>
        public AgentMode mode = AgentMode.Confirm;
        /// <summary>模型思考強度，見 <see cref="ReasoningLevel"/>；Auto 沿用框架預設。</summary>
        public ReasoningLevel reasoning = ReasoningLevel.Auto;

        // 自主模式（排程器）的節奏與配額在設定頁，玩家設的長期目標跟著存檔走。
        public const int DefaultAutoIntervalHours = 6;
        public const int MinAutoIntervalHours = 1;
        public const int MaxAutoIntervalHours = 24;
        public const int DefaultAutoBudget = 60;
        public const int MinAutoBudget = 10;
        public const int MaxAutoBudget = 300;

        public int autonomousIntervalHours = DefaultAutoIntervalHours;
        public int autonomousBudgetPerDay = DefaultAutoBudget;
        public bool autoOnRaid = true;
        public bool autoOnMentalBreak = true;
        public bool autoOnFoodShortage = true;
#pragma warning restore S1104

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref allowObserve, "allowObserve", true);
            Scribe_Values.Look(ref allowWork, "allowWork", true);
            Scribe_Values.Look(ref allowBuild, "allowBuild", true);
            Scribe_Values.Look(ref allowCombat, "allowCombat", true);
            Scribe_Values.Look(ref allowTrade, "allowTrade", true);
            Scribe_Values.Look(ref allowMedical, "allowMedical", true);
            Scribe_Values.Look(ref allowPrisoner, "allowPrisoner", true);
            Scribe_Values.Look(ref allowAnimal, "allowAnimal", true);
            Scribe_Values.Look(ref allowZone, "allowZone", true);
            Scribe_Values.Look(ref allowProduction, "allowProduction", true);
            Scribe_Values.Look(ref allowTime, "allowTime", true);
            Scribe_Values.Look(ref allowMCP, "allowMCP", true);
            Scribe_Values.Look(ref maxSteps, "maxSteps", DefaultMaxSteps);
            Scribe_Values.Look(ref mode, "mode", AgentMode.Confirm);
            Scribe_Values.Look(ref reasoning, "reasoning", ReasoningLevel.Auto);
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
