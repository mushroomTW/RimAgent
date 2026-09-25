using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using RimAgent.Agent;
using RimAgent.Tools;
using RimAgent.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent
{
    /// <summary>Mod 進入點與設定頁。執行模式由 Agent 視窗切換。</summary>
    public class RimAgentMod : Mod
    {
        public static RimAgentSettings Settings { get; private set; }

        private const float PermissionRowHeight = 28f;
        private const float PermissionColumnGap = 24f;
        private const float BulkButtonWidth = 100f;
        private const float OpenFolderButtonWidth = 110f;

        private static readonly ToolCategory[] Categories = (ToolCategory[])Enum.GetValues(typeof(ToolCategory));
        // 設定頁每個 IMGUI 事件都會重畫：權限標籤與提示快取起來，免得每幀翻譯與組字串。
        private static string[] _permissionLabels;
        private static string[] _permissionTips;
        private static LoadedLanguage _permissionLanguage;
        private static float _permissionLabelWidth = -1f;

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

            DrawPermissions(listing);

            listing.Gap();
            listing.Label("RimAgent.Settings.ClassifierHeader".Translate());
            listing.GapLine();
            Rect classifierRow = listing.GetRect(28f);
            float labelWidth = 140f;
            Rect labelRect = new Rect(classifierRow.x, classifierRow.y + 3f, labelWidth, 24f);
            Rect dropdownRect = new Rect(labelRect.xMax + 4f, classifierRow.y, classifierRow.xMax - labelRect.xMax - 4f, 26f);

            Widgets.Label(labelRect, "RimAgent.Settings.ClassifierModel".Translate());
            string classifier = string.IsNullOrWhiteSpace(Settings.classifierModel)
                ? "RimAgent.Model.Default".Translate().ToString()
                : Settings.classifierModel.Trim();
            if (Widgets.ButtonText(dropdownRect, classifier.Truncate(dropdownRect.width - 12f)))
            {
                // 選項與 Agent 視窗的模型選單相同：預設（依框架備援鏈）＋備援鏈上的條目。
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimAgent.Model.Default".Translate(), () => Settings.classifierModel = string.Empty)
                    {
                        tooltip = new TipSignal("RimAgent.Model.DefaultTip".Translate())
                    }
                };
                foreach (string entry in Window_Agent.ConfiguredModels())
                {
                    options.Add(new FloatMenuOption(entry, () => Settings.classifierModel = entry));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Mouse.IsOver(classifierRow))
            {
                TooltipHandler.TipRegion(classifierRow, "RimAgent.Settings.ClassifierModelTip".Translate());
            }
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

            listing.CheckboxLabeled("RimAgent.Settings.AutoOnRaid".Translate(), ref Settings.autoOnRaid, "RimAgent.Settings.AutoOnRaidTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnMentalBreak".Translate(), ref Settings.autoOnMentalBreak, "RimAgent.Settings.AutoOnMentalBreakTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnFoodShortage".Translate(), ref Settings.autoOnFoodShortage, "RimAgent.Settings.AutoOnFoodShortageTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnEmergencyMedical".Translate(), ref Settings.autoOnEmergencyMedical, "RimAgent.Settings.AutoOnEmergencyMedicalTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnFire".Translate(), ref Settings.autoOnFire, "RimAgent.Settings.AutoOnFireTip".Translate());

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
                string logFolder = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgent");
                string logPath = Path.Combine(logFolder, "RimAgent-runs.jsonl");
                Rect logRow = listing.GetRect(28f);
                Rect openRect = new Rect(logRow.xMax - OpenFolderButtonWidth, logRow.y, OpenFolderButtonWidth, 26f);
                Rect pathRect = new Rect(logRow.x, logRow.y + 3f, openRect.x - logRow.x - 8f, 24f);
                // 路徑很長，單行截斷，完整路徑放在提示。
                Text.WordWrap = false;
                Widgets.Label(pathRect, "RimAgent.Settings.RunLogPath".Translate(logPath).ToString().Truncate(pathRect.width));
                Text.WordWrap = true;
                TooltipHandler.TipRegion(pathRect, logPath);
                if (Widgets.ButtonText(openRect, "RimAgent.Settings.OpenLogFolder".Translate()))
                {
                    OpenFolder(logFolder);
                }
            }

            listing.Gap();
            listing.Label("RimAgent.Settings.FrameworkNote".Translate());

            listing.End();
            _settingsHeight = Mathf.Max(listing.CurHeight + 24f, inRect.height);
            Widgets.EndScrollView();
        }

        /// <summary>十二類工具權限：標題列右側一鍵全開／全關，勾選框排成兩欄（先填滿左欄再換右欄）。</summary>
        private static void DrawPermissions(Listing_Standard listing)
        {
            ToolCategory[] categories = Categories;

            Rect header = listing.GetRect(28f);
            Rect denyRect = new Rect(header.xMax - BulkButtonWidth, header.y, BulkButtonWidth, 26f);
            Rect allowRect = new Rect(denyRect.x - BulkButtonWidth - 6f, header.y, BulkButtonWidth, 26f);
            Widgets.Label(new Rect(header.x, header.y + 3f, allowRect.x - header.x - 8f, 24f), "RimAgent.Settings.PermissionsHeader".Translate());
            bool? bulk = null;
            if (Widgets.ButtonText(allowRect, "RimAgent.Settings.AllowAll".Translate())) bulk = true;
            if (Widgets.ButtonText(denyRect, "RimAgent.Settings.DenyAll".Translate())) bulk = false;
            if (bulk.HasValue)
            {
                foreach (ToolCategory category in categories) Settings.SetAllow(category, bulk.Value);
            }
            listing.GapLine();

            int rows = (categories.Length + 1) / 2;
            Rect grid = listing.GetRect(rows * PermissionRowHeight);
            float columnWidth = (grid.width - PermissionColumnGap) / 2f;
            // CheckboxLabeled 的文字區扣掉右側 24px 勾選框，再留一點邊距。
            EnsurePermissionLabels(columnWidth - 24f - 4f);
            for (int i = 0; i < categories.Length; i++)
            {
                ToolCategory category = categories[i];
                int column = i / rows;
                int row = i % rows;
                var cell = new Rect(grid.x + column * (columnWidth + PermissionColumnGap), grid.y + row * PermissionRowHeight, columnWidth, PermissionRowHeight - 4f);
                bool allowed = Settings.GetAllow(category);
                Widgets.CheckboxLabeled(cell, _permissionLabels[i], ref allowed);
                if (Mouse.IsOver(cell))
                {
                    Widgets.DrawHighlight(cell);
                    TooltipHandler.TipRegion(cell, _permissionTips[i]);
                }
                Settings.SetAllow(category, allowed);
            }
        }

        /// <summary>
        /// 標籤與提示只在語言或欄寬變了才重組。半欄放不下的標籤截成一行（不換行被格子高度切掉），
        /// 完整標籤改放在提示最前面。
        /// </summary>
        private static void EnsurePermissionLabels(float labelWidth)
        {
            LoadedLanguage language = LanguageDatabase.activeLanguage;
            if (_permissionLabels != null && language == _permissionLanguage && Mathf.Abs(labelWidth - _permissionLabelWidth) < 0.5f) return;
            _permissionLanguage = language;
            _permissionLabelWidth = labelWidth;
            _permissionLabels = new string[Categories.Length];
            _permissionTips = new string[Categories.Length];
            for (int i = 0; i < Categories.Length; i++)
            {
                // 設定鍵以類別英文命名，只有 MCP 全大寫。
                string suffix = Categories[i] == ToolCategory.Mcp ? "MCP" : Categories[i].ToString();
                string key = "RimAgent.Settings." + suffix;
                string label = key.Translate();
                string tip = (key + "Tip").Translate();
                _permissionLabels[i] = label.Truncate(labelWidth);
                _permissionTips[i] = _permissionLabels[i] == label ? tip : label + "\n\n" + tip;
            }
        }

        /// <summary>還沒寫過紀錄時資料夾可能不存在，先建立再開；建不了（唯讀、權限不足）就提示玩家，不讓例外打斷設定頁繪製。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Messages.Message("RimAgent.Settings.OpenLogFolderFailed".Translate(ex.Message), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Application.OpenURL(folder);
        }
    }
}
