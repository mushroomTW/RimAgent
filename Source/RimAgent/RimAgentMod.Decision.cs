using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Agent;
using RimAgent.UI;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

public partial class RimAgentMod
{
    /// <summary>決策模型的連線狀態一行，沒設定時附「前往」按鈕；下方是測試按鈕。</summary>
    private static bool IsDecisionReady(DecisionAvailability availability)
    {
        return availability != null && availability.Enabled && !string.IsNullOrWhiteSpace(availability.Model)
            && (!availability.RequiresKey || availability.HasKey);
    }

    private static void DrawDecisionStatus(Listing_Standard listing, DecisionAvailability availability)
    {
        string statusText;
        bool ok = false;
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
            string prov = string.IsNullOrEmpty(Settings.decisionProvider) ? availability.ProviderId : Settings.decisionProvider;
            string mdl = string.IsNullOrEmpty(Settings.decisionModel) ? (availability.Model ?? "default") : Settings.decisionModel;
            statusText = "RimAgent.Settings.DecisionStatusEnabled".Translate(prov, mdl);
            ok = true;
        }

        Rect row = listing.GetRect(28f);
        const float ButtonWidth = 110f;
        float textWidth = row.width - SubIndent - (ok ? 0f : ButtonWidth + 8f);
        Text.WordWrap = false;
        GUI.color = ok ? Green : Yellow;
        Widgets.Label(new Rect(row.x + SubIndent, row.y + 3f, textWidth, 24f), statusText.Truncate(textWidth));
        GUI.color = Color.white;
        Text.WordWrap = true;
        TooltipHandler.TipRegion(new Rect(row.x + SubIndent, row.y, textWidth, row.height), statusText);
        if (!ok && Widgets.ButtonText(new Rect(row.xMax - ButtonWidth, row.y, ButtonWidth, 26f), "RimAgent.Settings.GoShort".Translate()))
        {
            OpenFrameworkSettings();
        }
        if (ok) DrawDecisionTest(listing, SubIndent);
    }

    /// <summary>畫決策模型的供應商與模型選單（狀態與測試在主頁）；回傳框架是否已啟用決策模型。</summary>
    private static bool DrawDecisionSettings(Listing_Standard listing)
    {
        DecisionAvailability availability = DecisionServiceFactory.GetAvailability();

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
            ? DecisionDefaultKey.Translate().ToString()
            : currentProv;

        if (Widgets.ButtonText(provDropdownRect, shownProv.Truncate(provDropdownRect.width - 12f)))
        {
            // 供應商一律沿用框架啟用的那一個；這裡只讓玩家選擇「本模組不使用決策模型」。
            // 關掉後整段隱藏，可在主頁的決策模型開關重新啟用。
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption(DecisionDefaultKey.Translate(), () => Settings.decisionProvider = string.Empty),
                new FloatMenuOption("RimAgent.Settings.DecisionProviderNone".Translate(), () => Settings.decisionEnabled = false)
            };
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
            ? DecisionDefaultKey.Translate().ToString()
            : currentModel;

        if (Widgets.ButtonText(modelDropdownRect, shownModel.Truncate(modelDropdownRect.width - 12f)))
        {
            // 只列框架給出的模型（玩家在 RimLLM 決策分頁設定的那個），不另外向伺服器拉清單。
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption(DecisionDefaultKey.Translate(), () => Settings.decisionModel = string.Empty)
            };
            string frameworkModel = availability?.Enabled == true ? availability.Model : null;
            if (!string.IsNullOrEmpty(frameworkModel))
            {
                options.Add(new FloatMenuOption(frameworkModel, () => Settings.decisionModel = frameworkModel));
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
    private static void DrawDecisionTest(Listing_Standard listing, float indent)
    {
        Rect row = listing.GetRect(28f);
        const float buttonWidth = 130f;
        var buttonRect = new Rect(row.x + indent, row.y, buttonWidth, 26f);
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

    /// <summary>自訂分類的識別碼必須唯一；刪除中間項目後不能再用清單長度 + 1。</summary>
    internal static string NextCustomCategoryId(IReadOnlyList<DecisionRouteCategory> categories)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (categories != null)
        {
            foreach (string id in categories.Where(c => c != null && !string.IsNullOrWhiteSpace(c.Id)).Select(c => c.Id.Trim())) used.Add(id);
        }
        int number = 1;
        while (used.Contains(CustomIdPrefix + number)) number++;
        return CustomIdPrefix + number;
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
                    if (id.Length > CustomIdPrefix.Length && int.TryParse(id[CustomIdPrefix.Length..], out nextNum))
                    {
                        Settings.decisionCategories.Add(new DecisionRouteCategory(
                            id,
                            "RimAgent.Settings.DecisionCategoryCustomName".Translate(nextNum),
                            "Custom category description"));
                    }
                })
            };

            foreach (DecisionRouteCategory builtIn in DecisionRouteCategory.CreateDefaultCategories()
                .Where(b => !Settings.decisionCategories.Any(c => c != null && string.Equals(c.Id, b.Id, StringComparison.OrdinalIgnoreCase))))
            {
                DecisionRouteCategory captured = builtIn;
                options.Add(new FloatMenuOption("RimAgent.Settings.DecisionCategoryRestore".Translate(captured.Name, captured.Id), () =>
                {
                    Settings.decisionCategories.Add(captured);
                }));
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
            string displayName = string.IsNullOrEmpty(cat.Name) ? cat.Id : cat.Name;
            Widgets.Label(titleRect, $"{displayName} ({cat.Id})");
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(delRect, "RimAgent.Settings.DecisionCategoryDelete".Translate()))
            {
                toDelete = cat;
            }

            DrawReasoningRow(
                listing,
                "  " + "RimAgent.Settings.CategoryReasoning".Translate(),
                () => cat.Reasoning,
                v => cat.Reasoning = v,
                cat.Description);
        }

        if (toDelete != null)
        {
            Settings.decisionCategories.Remove(toDelete);
        }
    }
}
