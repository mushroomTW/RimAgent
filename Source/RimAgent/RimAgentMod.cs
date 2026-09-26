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
        private static float _settingsHeight = 960f;

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
            listing.Label("RimAgent.Settings.WebSearchHeader".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimAgent.Settings.UseLocalSearch".Translate(), ref Settings.useLocalSearch,
                "RimAgent.Settings.UseLocalSearchTip".Translate());
            if (Settings.useLocalSearch)
            {
                listing.Label("RimAgent.Settings.SearxngUrl".Translate());
                Settings.searxngUrl = Widgets.TextField(listing.GetRect(28f), Settings.searxngUrl ?? string.Empty);
            }

            listing.Gap();
            listing.Label("RimAgent.Settings.ClassifierHeader".Translate());
            listing.GapLine();
            // 模型下拉列共用同一形狀：Fallback＋備援鏈條目；分類器多一項「跟隨 Agent 模型」。
            DrawModelDropdown(listing, "RimAgent.Settings.ClassifierModel",
                () => Settings.classifierModel, v => Settings.classifierModel = v,
                "RimAgent.Settings.ClassifierModelTip",
                RimAgentSettings.FollowAgentModel, "RimAgent.Model.FollowAgent");

            listing.Gap();
            listing.Label("RimAgent.Settings.AutonomousHeader".Translate());
            listing.GapLine();

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
            listing.CheckboxLabeled("RimAgent.Settings.AutoOnUpkeep".Translate(), ref Settings.autoOnUpkeep, "RimAgent.Settings.AutoOnUpkeepTip".Translate());

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

        /// <summary>十四類工具權限：標題列右側一鍵全開／全關，勾選框排成兩欄（先填滿左欄再換右欄）。</summary>
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
        /// 模型下拉列：Fallback＋備援鏈條目，備援鏈之後可再插一個「跟隨 Agent 模型」。
        /// 選項與 Agent 視窗的模型選單相同。
        /// </summary>
        /// <param name="followValue">「跟隨 Agent 模型」的設定值；null 表示不提供這個選項。</param>
        /// <param name="followLabelKey">該選項的譯文鍵；提示沿用同一鍵加 "Tip"。</param>
        private static void DrawModelDropdown(
            Listing_Standard listing, string labelKey, Func<string> current, Action<string> set, string tipKey,
            string followValue = null, string followLabelKey = null)
        {
            Rect row = listing.GetRect(28f);
            const float labelWidth = 140f;
            var labelRect = new Rect(row.x, row.y + 3f, labelWidth, 24f);
            var dropdownRect = new Rect(labelRect.xMax + 4f, row.y, row.xMax - labelRect.xMax - 4f, 26f);

            Widgets.Label(labelRect, labelKey.Translate());
            string value = (current() ?? string.Empty).Trim();
            bool following = followValue != null && string.Equals(value, followValue, StringComparison.Ordinal);
            string shown = value.Length == 0
                ? "RimAgent.Model.Default".Translate().ToString()
                : following ? followLabelKey.Translate().ToString() : value;
            if (Widgets.ButtonText(dropdownRect, shown.Truncate(dropdownRect.width - 12f)))
            {
                var options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("RimAgent.Model.Default".Translate(), () => set(string.Empty))
                    {
                        tooltip = new TipSignal("RimAgent.Model.DefaultTip".Translate())
                    }
                };
                if (followValue != null)
                {
                    string captured = followValue;
                    options.Add(new FloatMenuOption(followLabelKey.Translate(), () => set(captured))
                    {
                        tooltip = new TipSignal(followLabelKey + "Tip".Translate())
                    });
                }
                foreach (string entry in Window_Agent.ConfiguredModels())
                {
                    string captured = entry;
                    options.Add(new FloatMenuOption(captured, () => set(captured)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Mouse.IsOver(row))
            {
                TooltipHandler.TipRegion(row, following ? followLabelKey + "Tip".Translate() : tipKey.Translate());
            }
            listing.Gap(6f);
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
