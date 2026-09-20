using RimAgent.Agent;
using UnityEngine;
using Verse;

namespace RimAgent
{
    /// <summary>Mod 進入點與設定頁。LLM 相關設定全在 RimLLM Framework，這裡只有工具權限、執行模式與步數上限。</summary>
    public class RimAgentMod : Mod
    {
        public static RimAgentSettings Settings { get; private set; }

        private static Vector2 _settingsScrollPosition = Vector2.zero;
        private static float _settingsHeight = 920f;

        public RimAgentMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimAgentSettings>();
        }

        public override string SettingsCategory()
        {
            return "RimAgent.Settings.Category".Translate().Resolve() ?? "RimAgent";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 24f, _settingsHeight);
            Widgets.BeginScrollView(inRect, ref _settingsScrollPosition, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("RimAgent.Settings.PermissionsHeader".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimAgent.Settings.Observe".Translate(), ref Settings.allowObserve, "RimAgent.Settings.ObserveTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Work".Translate(), ref Settings.allowWork, "RimAgent.Settings.WorkTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Build".Translate(), ref Settings.allowBuild, "RimAgent.Settings.BuildTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Combat".Translate(), ref Settings.allowCombat, "RimAgent.Settings.CombatTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Trade".Translate(), ref Settings.allowTrade, "RimAgent.Settings.TradeTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Medical".Translate(), ref Settings.allowMedical, "RimAgent.Settings.MedicalTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Prisoner".Translate(), ref Settings.allowPrisoner, "RimAgent.Settings.PrisonerTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Animal".Translate(), ref Settings.allowAnimal, "RimAgent.Settings.AnimalTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Zone".Translate(), ref Settings.allowZone, "RimAgent.Settings.ZoneTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Production".Translate(), ref Settings.allowProduction, "RimAgent.Settings.ProductionTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.Time".Translate(), ref Settings.allowTime, "RimAgent.Settings.TimeTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.MCP".Translate(), ref Settings.allowMCP, "RimAgent.Settings.MCPTip".Translate());

            listing.Gap();
            listing.Label("RimAgent.Settings.MaxSteps".Translate(Settings.maxSteps), -1f, "RimAgent.Settings.MaxStepsTip".Translate());
            Settings.maxSteps = Mathf.RoundToInt(listing.Slider(Settings.maxSteps, RimAgentSettings.MinSteps, RimAgentSettings.MaxSteps));

            listing.Gap();
            listing.Label("RimAgent.Settings.ModeHeader".Translate());
            listing.GapLine();
            ModeOption(listing, AgentMode.Ask, "RimAgent.Settings.Mode.Ask", "RimAgent.Settings.Mode.AskDesc");
            ModeOption(listing, AgentMode.Confirm, "RimAgent.Settings.Mode.Confirm", "RimAgent.Settings.Mode.ConfirmDesc");
            ModeOption(listing, AgentMode.Auto, "RimAgent.Settings.Mode.Auto", "RimAgent.Settings.Mode.AutoDesc");
            ModeOption(listing, AgentMode.Autonomous, "RimAgent.Settings.Mode.Autonomous", "RimAgent.Settings.Mode.AutonomousDesc");

            listing.Gap();
            listing.Label("RimAgent.Settings.AutonomousHeader".Translate());
            listing.GapLine();
            listing.Label("RimAgent.Settings.AutoInterval".Translate(Settings.autonomousIntervalHours));
            Settings.autonomousIntervalHours = Mathf.RoundToInt(listing.Slider(Settings.autonomousIntervalHours, RimAgentSettings.MinAutoIntervalHours, RimAgentSettings.MaxAutoIntervalHours));
            listing.Label("RimAgent.Settings.AutoBudget".Translate(Settings.autonomousBudgetPerDay));
            Settings.autonomousBudgetPerDay = Mathf.RoundToInt(listing.Slider(Settings.autonomousBudgetPerDay, RimAgentSettings.MinAutoBudget, RimAgentSettings.MaxAutoBudget));
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnRaid".Translate(), ref Settings.autoOnRaid);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnMentalBreak".Translate(), ref Settings.autoOnMentalBreak);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnFoodShortage".Translate(), ref Settings.autoOnFoodShortage);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnEmergencyMedical".Translate(), ref Settings.autoOnEmergencyMedical);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnFire".Translate(), ref Settings.autoOnFire);

            listing.Gap();
            listing.Label("RimAgent.Settings.FrameworkNote".Translate());

            listing.End();
            _settingsHeight = Mathf.Max(listing.CurHeight + 24f, inRect.height);
            Widgets.EndScrollView();
        }

        /// <summary>模式單選列：標題一行、灰字說明一行，點任一行都選取。</summary>
        private static void ModeOption(Listing_Standard listing, AgentMode mode, string titleKey, string descKey)
        {
            if (listing.RadioButton(titleKey.Translate(), Settings.mode == mode))
            {
                Settings.mode = mode;
            }
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            listing.Label(descKey.Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            listing.Gap(4f);
        }
    }
}
