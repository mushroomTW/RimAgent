using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RimAgent.Agent;
using RimAgent.Tools;
using RimAgent.UI;
using RimLLM_Framework;
using RimLLM_Framework.Manager.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

/// <summary>Mod 進入點與設定頁。執行模式由 Agent 視窗切換。</summary>
public class RimAgentMod : Mod
{
    public static RimAgentSettings Settings { get; internal set; }

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

    // 進階設定預設收合；只在本次遊戲期間記住，不寫入設定檔。
    private static bool _showAdvanced;

    private Vector2 _settingsScrollPosition = Vector2.zero;
    private float _settingsHeight = 960f;

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
        // 捲動檢視裡必須單欄：Listing.GetRect 內容超過檢視高度會自動另起一欄，
        // 超出的內容會跑到檢視寬度外的第二欄（看起來像被截斷且捲不下去），
        // 高度也永遠長不大、捲軸出不來。權限的雙欄是手排的絕對座標，不受影響。
        listing.maxOneColumn = true;
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
        listing.Label("RimAgent.Settings.AutonomousHeader".Translate());
        listing.GapLine();

        bool careWasEnabled = Settings.autonomousCare;
        listing.CheckboxLabeled("RimAgent.Settings.AutonomousCare".Translate(), ref Settings.autonomousCare,
            "RimAgent.Settings.AutonomousCareTip".Translate());
        if (careWasEnabled && !Settings.autonomousCare) RimAgentGameComponent.Instance?.DisableAutonomousCare();
        listing.CheckboxLabeled("RimAgent.Settings.ConfirmAutonomousMajor".Translate(), ref Settings.confirmAutonomousMajor,
            "RimAgent.Settings.ConfirmAutonomousMajorTip".Translate());

        listing.CheckboxLabeled("RimAgent.Settings.AutoTimeSpeed".Translate(), ref Settings.autoTimeSpeed,
            "RimAgent.Settings.AutoTimeSpeedTip".Translate());

        TaggedString cooldownLabel = Settings.autonomousCooldownMinutes == 0
            ? "RimAgent.Settings.AutoCooldownOff".Translate()
            : "RimAgent.Settings.AutoCooldown".Translate(Settings.autonomousCooldownMinutes);
        listing.Label(cooldownLabel, -1f, "RimAgent.Settings.AutoCooldownTip".Translate());
        Settings.autonomousCooldownMinutes = Mathf.RoundToInt(listing.Slider(Settings.autonomousCooldownMinutes, RimAgentSettings.MinCooldownMinutes, RimAgentSettings.MaxCooldownMinutes));

        listing.Gap();
        listing.GapLine();
        listing.CheckboxLabeled("RimAgent.Settings.ShowAdvanced".Translate(), ref _showAdvanced, "RimAgent.Settings.ShowAdvancedTip".Translate());
        if (_showAdvanced) DrawAdvanced(listing);

        listing.Gap();
        listing.Label("RimAgent.Settings.FrameworkNote".Translate());

        listing.End();
        _settingsHeight = Mathf.Max(listing.CurHeight + 24f, inRect.height);
        Widgets.EndScrollView();
    }

    /// <summary>進階設定：模型分工、決策模型（含分流）、上下文壓縮與執行紀錄。</summary>
    private static void DrawAdvanced(Listing_Standard listing)
    {
        DrawModelRoles(listing);

        listing.Gap();
        listing.Label("RimAgent.Settings.DecisionModelHeader".Translate());
        listing.GapLine();
        // 分流靠決策模型分類，沒有可用的決策模型時細項沒有作用，不顯示。
        if (DrawDecisionSettings(listing))
        {
            listing.CheckboxLabeled("RimAgent.Settings.DecisionAnswerChoices".Translate(), ref Settings.decisionAnswerChoices,
                "RimAgent.Settings.DecisionAnswerChoicesTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.DecisionTriageUpkeep".Translate(), ref Settings.decisionTriageUpkeep,
                "RimAgent.Settings.DecisionTriageUpkeepTip".Translate());
            listing.CheckboxLabeled("RimAgent.Settings.DecisionPreloadPacks".Translate(), ref Settings.decisionPreloadPacks,
                "RimAgent.Settings.DecisionPreloadPacksTip".Translate());
            listing.Gap(6f);
            DrawDecisionRoutingSettings(listing);
        }

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
    }

    /// <summary>十五類工具權限：標題列右側一鍵全開／全關，勾選框排成兩欄（先填滿左欄再換右欄）。</summary>
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
    /// 模型分工（參照 hermes-agent 的 auxiliary 模型）：Agent 主模型在 Agent 視窗切換，
    /// 其餘每種模型呼叫各一格，預設跟隨 Agent 模型；所有子代理共用同一格。
    /// </summary>
    private static void DrawModelRoles(Listing_Standard listing)
    {
        listing.Gap();
        listing.Label("RimAgent.Settings.ModelRolesHeader".Translate());
        listing.GapLine();
        DrawModelDropdown(listing, "RimAgent.Settings.SubagentModel".Translate(),
            () => Settings.subagentModel, v => Settings.subagentModel = v,
            "RimAgent.Settings.SubagentModelTip".Translate(),
            () => Settings.subagentReasoning, v => Settings.subagentReasoning = v);
        DrawModelDropdown(listing, "RimAgent.Settings.ClassifierModel".Translate(),
            () => Settings.classifierModel, v => Settings.classifierModel = v,
            "RimAgent.Settings.ClassifierModelTip".Translate(),
            () => Settings.classifierReasoning, v => Settings.classifierReasoning = v);
        DrawModelDropdown(listing, "RimAgent.Settings.CompactModel".Translate(),
            () => Settings.compactModel, v => Settings.compactModel = v,
            "RimAgent.Settings.CompactModelTip".Translate(),
            () => Settings.compactReasoning, v => Settings.compactReasoning = v);
    }

    /// <summary>畫決策模型的狀態與選單；回傳框架是否已啟用決策模型。</summary>
    private static bool DrawDecisionSettings(Listing_Standard listing)
    {
        DecisionAvailability availability = DecisionServiceFactory.GetAvailability();
        string statusText;
        if (availability == null || !availability.Enabled)
        {
            statusText = "RimAgent.Settings.DecisionStatusDisabled".Translate();
        }
        else if (availability.RequiresKey && !availability.HasKey)
        {
            statusText = "RimAgent.Settings.DecisionStatusNoKey".Translate(availability.ProviderId);
        }
        else
        {
            string prov = string.IsNullOrEmpty(Settings.decisionProvider)
                ? availability.ProviderId
                : Settings.decisionProvider;
            string mdl = string.IsNullOrEmpty(Settings.decisionModel)
                ? (availability.Model ?? "default")
                : Settings.decisionModel;
            statusText = "RimAgent.Settings.DecisionStatusEnabled".Translate(prov, mdl);
        }

        listing.Label("RimAgent.Settings.DecisionStatus".Translate(statusText));
        DrawDecisionTest(listing);

        // 供應商下拉
        Rect provRow = listing.GetRect(28f);
        const float labelWidth = 160f;
        var provLabelRect = new Rect(provRow.x, provRow.y + 3f, labelWidth, 24f);
        var provDropdownRect = new Rect(provLabelRect.xMax + 4f, provRow.y, provRow.xMax - provLabelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(provLabelRect, "RimAgent.Settings.DecisionProvider".Translate().Truncate(labelWidth));
        Text.WordWrap = true;

        string currentProv = Settings.decisionProvider;
        string shownProv = string.IsNullOrEmpty(currentProv)
            ? "RimAgent.Settings.DecisionDefault".Translate().ToString()
            : currentProv;

        if (Widgets.ButtonText(provDropdownRect, shownProv.Truncate(provDropdownRect.width - 12f)))
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimAgent.Settings.DecisionDefault".Translate(), () => Settings.decisionProvider = string.Empty)
            };
            try
            {
                AddDecisionProviderOptions(options);
            }
            catch (MissingMethodException)
            {
                // 舊版 RimLLM 沒有這個目錄；保留下拉選單的預設項即可。
            }
            catch (TypeLoadException)
            {
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        if (Mouse.IsOver(provDropdownRect) || Mouse.IsOver(provLabelRect))
        {
            TooltipHandler.TipRegion(new Rect(provRow.x, provRow.y, provRow.width, provRow.height), "RimAgent.Settings.DecisionProviderTip".Translate());
        }
        listing.Gap(6f);

        // 模型下拉
        Rect modelRow = listing.GetRect(28f);
        var modelLabelRect = new Rect(modelRow.x, modelRow.y + 3f, labelWidth, 24f);
        var modelDropdownRect = new Rect(modelLabelRect.xMax + 4f, modelRow.y, modelRow.xMax - modelLabelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(modelLabelRect, "RimAgent.Settings.DecisionModel".Translate().Truncate(labelWidth));
        Text.WordWrap = true;

        string currentModel = Settings.decisionModel;
        string shownModel = string.IsNullOrEmpty(currentModel)
            ? "RimAgent.Settings.DecisionDefault".Translate().ToString()
            : currentModel;

        if (Widgets.ButtonText(modelDropdownRect, shownModel.Truncate(modelDropdownRect.width - 12f)))
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimAgent.Settings.DecisionDefault".Translate(), () => Settings.decisionModel = string.Empty)
            };
            string targetProv = string.IsNullOrEmpty(Settings.decisionProvider)
                ? availability?.ProviderId
                : Settings.decisionProvider;
            try
            {
                AddDecisionModelOptions(options, targetProv);
            }
            catch (MissingMethodException)
            {
                // 舊版 RimLLM 沒有供應商目錄；只留預設模型一項。
            }
            catch (TypeLoadException)
            {
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
        if (Mouse.IsOver(modelDropdownRect) || Mouse.IsOver(modelLabelRect))
        {
            TooltipHandler.TipRegion(new Rect(modelRow.x, modelRow.y, modelRow.width, modelRow.height), "RimAgent.Settings.DecisionModelTip".Translate());
        }
        listing.Gap(6f);
        return availability != null && availability.Enabled;
    }

    /// <summary>測試按鈕與最近一次結果；送出的是設定頁目前選的供應商與模型。</summary>
    private static void DrawDecisionTest(Listing_Standard listing)
    {
        Rect row = listing.GetRect(28f);
        const float buttonWidth = 130f;
        var buttonRect = new Rect(row.x, row.y, buttonWidth, 26f);
        bool running = DecisionModelTester.Running;
        string buttonLabel = running
            ? "RimAgent.Settings.DecisionTestRunning".Translate().ToString()
            : "RimAgent.Settings.DecisionTest".Translate().ToString();
        if (Widgets.ButtonText(buttonRect, buttonLabel) && !running)
        {
            DecisionModelTester.Start();
        }
        TooltipHandler.TipRegion(buttonRect, "RimAgent.Settings.DecisionTestTip".Translate());

        string result;
        switch (DecisionModelTester.Outcome)
        {
            case DecisionModelTester.TestOutcome.Ok:
                result = "RimAgent.Settings.DecisionTestOk".Translate(DecisionModelTester.Detail);
                break;
            case DecisionModelTester.TestOutcome.Unavailable:
                result = "RimAgent.Settings.DecisionTestUnavailable".Translate();
                break;
            case DecisionModelTester.TestOutcome.NoAnswer:
                result = "RimAgent.Settings.DecisionTestNoAnswer".Translate();
                break;
            case DecisionModelTester.TestOutcome.Failed:
                result = "RimAgent.Settings.DecisionTestFailed".Translate(DecisionModelTester.Detail);
                break;
            default:
                result = string.Empty;
                break;
        }
        var resultRect = new Rect(buttonRect.xMax + 8f, row.y + 3f, row.xMax - buttonRect.xMax - 8f, 24f);
        Text.WordWrap = false;
        Widgets.Label(resultRect, result.Truncate(resultRect.width));
        Text.WordWrap = true;
        TooltipHandler.TipRegion(resultRect, result);
        listing.Gap(6f);
    }

    /// <summary>RimLLM 的決策供應商目錄可能不存在於舊版；單獨成方法，讓呼叫端能攔截型別載入錯誤。</summary>
    private static void AddDecisionProviderOptions(List<FloatMenuOption> options)
    {
        foreach (RimLLMDecisionProvider provider in RimLLMDecisionProviderCatalog.All)
        {
            string id = provider.Id;
            if (string.IsNullOrWhiteSpace(id)) continue;
            options.Add(new FloatMenuOption(id, () => Settings.decisionProvider = id));
        }
    }

    /// <summary>同上：只把目前供應商提供的預設模型加進選單，目錄不可用時保持預設項。</summary>
    private static void AddDecisionModelOptions(List<FloatMenuOption> options, string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        if (!RimLLMDecisionProviderCatalog.TryGet(providerId, out RimLLMDecisionProvider provider)) return;
        foreach (string model in provider.PresetModels)
        {
            string captured = model;
            options.Add(new FloatMenuOption(captured, () => Settings.decisionModel = captured));
        }
    }

    /// <summary>自訂分類的識別碼必須唯一；刪除中間項目後不能再用清單長度 + 1。</summary>
    internal static string NextCustomCategoryId(IReadOnlyList<DecisionRouteCategory> categories)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (categories != null)
        {
            foreach (DecisionRouteCategory category in categories)
            {
                if (category != null && !string.IsNullOrWhiteSpace(category.id)) used.Add(category.id.Trim());
            }
        }
        int number = 1;
        while (used.Contains("custom_" + number)) number++;
        return "custom_" + number;
    }

    private static void DrawDecisionRoutingSettings(Listing_Standard listing)
    {
        // 備援思考強度
        DrawReasoningRow(
            listing,
            "RimAgent.Settings.DecisionFallbackReasoning".Translate(),
            () => Settings.decisionFallbackReasoning,
            v => Settings.decisionFallbackReasoning = v,
            "RimAgent.Settings.DecisionFallbackReasoningTip".Translate());

        // 最小信心門檻
        listing.Label(
            "RimAgent.Settings.DecisionMinConfidence".Translate(Settings.decisionRoutingMinConfidence.ToString("P0")),
            -1f,
            "RimAgent.Settings.DecisionMinConfidenceTip".Translate());
        Settings.decisionRoutingMinConfidence = listing.Slider(Settings.decisionRoutingMinConfidence, 0f, 1f);

        listing.Gap(6f);

        // 類別清單
        Rect catHeader = listing.GetRect(28f);
        const float ActionButtonWidth = 90f;
        Rect resetRect = new Rect(catHeader.xMax - ActionButtonWidth, catHeader.y, ActionButtonWidth, 26f);
        Rect addRect = new Rect(resetRect.x - ActionButtonWidth - 6f, catHeader.y, ActionButtonWidth, 26f);
        Widgets.Label(new Rect(catHeader.x, catHeader.y + 3f, addRect.x - catHeader.x - 8f, 24f), "RimAgent.Settings.DecisionCategoriesHeader".Translate());

        if (Widgets.ButtonText(addRect, "RimAgent.Settings.DecisionCategoryAdd".Translate()))
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimAgent.Settings.DecisionCategoryAddCustom".Translate(), () =>
                {
                    string id = NextCustomCategoryId(Settings.decisionCategories);
                    int nextNum = 1;
                    if (id.Length > "custom_".Length) int.TryParse(id.Substring("custom_".Length), out nextNum);
                    Settings.decisionCategories.Add(new DecisionRouteCategory(
                        id,
                        "RimAgent.Settings.DecisionCategoryCustomName".Translate(nextNum),
                        "Custom category description"));
                })
            };

            foreach (DecisionRouteCategory builtIn in DecisionRouteCategory.CreateDefaultCategories())
            {
                if (!Settings.decisionCategories.Any(c => string.Equals(c.id, builtIn.id, StringComparison.OrdinalIgnoreCase)))
                {
                    DecisionRouteCategory captured = builtIn;
                    options.Add(new FloatMenuOption("RimAgent.Settings.DecisionCategoryRestore".Translate(captured.name, captured.id), () =>
                    {
                        Settings.decisionCategories.Add(captured);
                    }));
                }
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        if (Widgets.ButtonText(resetRect, "RimAgent.Settings.DecisionCategoriesReset".Translate()))
        {
            Settings.decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
        }

        listing.Gap(4f);

        DecisionRouteCategory toDelete = null;
        for (int i = 0; i < Settings.decisionCategories.Count; i++)
        {
            DecisionRouteCategory cat = Settings.decisionCategories[i];
            Rect row = listing.GetRect(24f);
            const float DeleteBtnWidth = 50f;
            Rect delRect = new Rect(row.xMax - DeleteBtnWidth, row.y, DeleteBtnWidth, 22f);
            Rect titleRect = new Rect(row.x, row.y + 2f, row.width - DeleteBtnWidth - 6f, 20f);

            Text.Font = GameFont.Tiny;
            string displayName = string.IsNullOrEmpty(cat.name) ? cat.id : cat.name;
            Widgets.Label(titleRect, $"{displayName} ({cat.id})");
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(delRect, "RimAgent.Settings.DecisionCategoryDelete".Translate()))
            {
                toDelete = cat;
            }

            DrawReasoningRow(
                listing,
                "  " + "RimAgent.Settings.CategoryReasoning".Translate(),
                () => cat.reasoning,
                v => cat.reasoning = v,
                cat.description);
        }

        if (toDelete != null)
        {
            Settings.decisionCategories.Remove(toDelete);
        }
    }

    /// <summary>
    /// 一格模型槽：模型下拉（跟隨 Agent／Fallback／備援鏈條目，選項與 Agent 視窗的模型選單相同）
    /// 加上選用的思考強度下拉。
    /// </summary>
    /// <param name="reasoning">思考強度的讀取；null 表示這列不顯示思考強度。</param>
    private static void DrawModelDropdown(
        Listing_Standard listing, string label, Func<string> current, Action<string> set, string tip,
        Func<ReasoningLevel> reasoning = null, Action<ReasoningLevel> setReasoning = null)
    {
        Rect row = listing.GetRect(28f);
        const float labelWidth = 160f;
        const float reasoningWidth = 90f;
        var labelRect = new Rect(row.x, row.y + 3f, labelWidth, 24f);
        float dropdownRight = reasoning != null ? row.xMax - reasoningWidth - 6f : row.xMax;
        var dropdownRect = new Rect(labelRect.xMax + 4f, row.y, dropdownRight - labelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(labelRect, label.Truncate(labelWidth));
        Text.WordWrap = true;
        string raw = current();
        string shown = GetModelDropdownDisplayText(raw);
        if (Widgets.ButtonText(dropdownRect, shown.Truncate(dropdownRect.width - 12f)))
        {
            ShowModelDropdownMenu(set);
        }
        if (Mouse.IsOver(dropdownRect) || Mouse.IsOver(labelRect))
        {
            TooltipHandler.TipRegion(new Rect(row.x, row.y, dropdownRect.xMax - row.x, row.height), tip);
        }
        if (reasoning != null)
        {
            var reasoningRect = new Rect(row.xMax - reasoningWidth, row.y, reasoningWidth, 26f);
            if (Widgets.ButtonText(reasoningRect, ReasoningKey(reasoning()).Translate()))
            {
                ShowReasoningDropdownMenu(setReasoning);
            }
            TooltipHandler.TipRegion(reasoningRect, "RimAgent.Settings.SlotReasoningTip".Translate());
        }
        listing.Gap(6f);
    }

    private static string GetModelDropdownDisplayText(string raw)
    {
        string value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "RimAgent.Model.Default".Translate().ToString();
        }
        if (string.Equals(value, RimAgentSettings.FollowAgentModel, StringComparison.Ordinal))
        {
            return "RimAgent.Model.FollowAgent".Translate().ToString();
        }
        return value;
    }

    private static void ShowModelDropdownMenu(Action<string> set)
    {
        var options = new List<FloatMenuOption>();
        options.Add(new FloatMenuOption("RimAgent.Model.FollowAgent".Translate(), () => set(RimAgentSettings.FollowAgentModel))
        {
            tooltip = new TipSignal("RimAgent.Model.FollowAgentTip".Translate())
        });
        options.Add(new FloatMenuOption("RimAgent.Model.Default".Translate(), () => set(string.Empty))
        {
            tooltip = new TipSignal("RimAgent.Model.DefaultTip".Translate())
        });
        foreach (string entry in Window_Agent.ConfiguredModels())
        {
            string captured = entry;
            options.Add(new FloatMenuOption(captured, () => set(captured)));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>只有思考強度下拉的一列：分流類別與備援思考強度用。</summary>
    private static void DrawReasoningRow(
        Listing_Standard listing,
        string label,
        Func<ReasoningLevel> reasoning,
        Action<ReasoningLevel> setReasoning,
        string tip)
    {
        Rect row = listing.GetRect(28f);
        const float ReasoningWidth = 90f;
        var labelRect = new Rect(row.x, row.y + 3f, row.width - ReasoningWidth - 8f, 24f);
        var reasoningRect = new Rect(row.xMax - ReasoningWidth, row.y, ReasoningWidth, 26f);
        Text.WordWrap = false;
        Widgets.Label(labelRect, label.Truncate(labelRect.width));
        Text.WordWrap = true;
        if (Widgets.ButtonText(reasoningRect, ReasoningKey(reasoning()).Translate()))
        {
            ShowReasoningDropdownMenu(setReasoning);
        }
        if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, tip);
        listing.Gap(6f);
    }

    private static void ShowReasoningDropdownMenu(Action<ReasoningLevel> setReasoning)
    {
        var options = new List<FloatMenuOption>();
        foreach (ReasoningLevel level in (ReasoningLevel[])Enum.GetValues(typeof(ReasoningLevel)))
        {
            ReasoningLevel captured = level;
            options.Add(new FloatMenuOption(ReasoningKey(level).Translate(), () => setReasoning(captured)));
        }
        Find.WindowStack.Add(new FloatMenu(options));
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
            string key = PermissionKey(Categories[i]);
            string label = key.Translate();
            string tip = (key + "Tip").Translate();
            _permissionLabels[i] = label.Truncate(labelWidth);
            _permissionTips[i] = _permissionLabels[i] == label ? tip : label + "\n\n" + tip;
        }
    }

    /// <summary>工具類別權限的翻譯鍵（提示另加 "Tip" 後綴）。設定鍵以類別英文命名，只有 MCP 全大寫。</summary>
    internal static string PermissionKey(ToolCategory category)
    {
        return "RimAgent.Settings." + (category == ToolCategory.Mcp ? "MCP" : category.ToString());
    }

    /// <summary>思考強度的翻譯鍵。</summary>
    internal static string ReasoningKey(ReasoningLevel level)
    {
        return "RimAgent.Reasoning." + level;
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
