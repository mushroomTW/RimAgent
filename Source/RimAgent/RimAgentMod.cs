using System;
using System.IO;
using RimAgent.Agent;
using RimAgent.Tools;
using UnityEngine;
using Verse;

namespace RimAgent
{
    /// <summary>Mod 進入點與設定頁。執行模式由 Agent 視窗切換。</summary>
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
            return "RimAgent.Settings.Category".CanTranslate()
                ? "RimAgent.Settings.Category".Translate().Resolve()
                : "RimAgent";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 24f, _settingsHeight);
            Widgets.BeginScrollView(inRect, ref _settingsScrollPosition, viewRect);

            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("RimAgent.Settings.PermissionsHeader".Translate());
            listing.GapLine();
            foreach (ToolCategory category in (ToolCategory[])Enum.GetValues(typeof(ToolCategory)))
            {
                // 設定鍵以類別英文命名，只有 MCP 全大寫。
                string suffix = category == ToolCategory.Mcp ? "MCP" : category.ToString();
                string key = "RimAgent.Settings." + suffix;
                bool allowed = Settings.GetAllow(category);
                listing.CheckboxLabeled(key.Translate(), ref allowed, (key + "Tip").Translate());
                Settings.SetAllow(category, allowed);
            }

            listing.Gap();
            Rect classifierRow = listing.GetRect(28f);
            float indent = 0f;
            float labelWidth = 140f;
            float btnWidth = 80f;
            float inputWidth = Mathf.Max(60f, classifierRow.width - indent - labelWidth - btnWidth - 12f);

            Rect labelRect = new Rect(classifierRow.x + indent, classifierRow.y + 3f, labelWidth, 24f);
            Rect inputRect = new Rect(labelRect.xMax + 4f, classifierRow.y, inputWidth, 26f);
            Rect btnRect = new Rect(inputRect.xMax + 8f, classifierRow.y, btnWidth, 26f);

            Widgets.Label(labelRect, "RimAgent.Settings.ClassifierModel".Translate());
            Settings.classifierModel = Widgets.TextField(inputRect, Settings.classifierModel ?? string.Empty);
            if (Widgets.ButtonText(btnRect, "RimAgent.Settings.ClassifierModelDefault".Translate()))
            {
                Settings.classifierModel = string.Empty;
            }
            TooltipHandler.TipRegion(classifierRow, "RimAgent.Settings.ClassifierModelTip".Translate());
            listing.Gap(6f);

            listing.Gap();
            listing.Label("RimAgent.Settings.AutonomousHeader".Translate());
            listing.GapLine();
            listing.Label("RimAgent.Settings.AutoInterval".Translate(Settings.autonomousIntervalHours));
            Settings.autonomousIntervalHours = Mathf.RoundToInt(listing.Slider(Settings.autonomousIntervalHours, RimAgentSettings.MinAutoIntervalHours, RimAgentSettings.MaxAutoIntervalHours));

            TaggedString cooldownLabel = Settings.autonomousCooldownMinutes == 0
                ? "RimAgent.Settings.AutoCooldownOff".Translate()
                : "RimAgent.Settings.AutoCooldown".Translate(Settings.autonomousCooldownMinutes);
            listing.Label(cooldownLabel, -1f, "RimAgent.Settings.AutoCooldownTip".Translate());
            Settings.autonomousCooldownMinutes = Mathf.RoundToInt(listing.Slider(Settings.autonomousCooldownMinutes, RimAgentSettings.MinCooldownMinutes, RimAgentSettings.MaxCooldownMinutes));

            listing.CheckboxLabeled("RimAgent.Settings.AutoOnRaid".Translate(), ref Settings.autoOnRaid);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnMentalBreak".Translate(), ref Settings.autoOnMentalBreak);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnFoodShortage".Translate(), ref Settings.autoOnFoodShortage);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnEmergencyMedical".Translate(), ref Settings.autoOnEmergencyMedical);
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnFire".Translate(), ref Settings.autoOnFire);

            listing.Gap();
            listing.Label("RimAgent.Settings.CompactionHeader".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimAgent.Settings.AutoCompact".Translate(), ref Settings.autoCompact, "RimAgent.Settings.AutoCompactTip".Translate());
            if (Settings.autoCompact)
            {
                listing.Label("RimAgent.Settings.AutoCompactThreshold".Translate(Settings.autoCompactThreshold), -1f, "RimAgent.Settings.AutoCompactThresholdTip".Translate());
                Settings.autoCompactThreshold = Mathf.RoundToInt(listing.Slider(Settings.autoCompactThreshold, RimAgentSettings.MinAutoCompactThreshold, RimAgentSettings.MaxAutoCompactThreshold));
            }

            listing.Gap();
            listing.Label("RimAgent.Settings.RunLogHeader".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimAgent.Settings.RecordRuns".Translate(), ref Settings.recordRuns,
                "RimAgent.Settings.RecordRunsTip".Translate());
            if (Settings.recordRuns)
            {
                string logPath = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent", "RimAgent-runs.jsonl");
                listing.Label("RimAgent.Settings.RunLogPath".Translate(logPath));
            }

            listing.Gap();
            listing.Label("RimAgent.Settings.FrameworkNote".Translate());

            listing.End();
            _settingsHeight = Mathf.Max(listing.CurHeight + 24f, inRect.height);
            Widgets.EndScrollView();
        }

    }
}
