using RimAgent.Tools;
using Verse;

namespace RimAgent
{
    /// <summary>玩家可調的設定：十類工具的開關與單次任務的步數上限。</summary>
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
        public int maxSteps = DefaultMaxSteps;
        /// <summary>
        /// 「需確認」模式：開啟後 Work／Build／Combat 命令先回傳預覽，玩家按確認才執行。
        /// 查詢不受影響。
        /// </summary>
        public bool requireConfirmation;

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
            Scribe_Values.Look(ref maxSteps, "maxSteps", DefaultMaxSteps);
            Scribe_Values.Look(ref requireConfirmation, "requireConfirmation", false);
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
                Production = allowProduction
            };
        }
    }
}
