#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
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

    private static bool DrawDecisionSettings(Listing_Standard listing)
    {
        DecisionAvailability availability = DecisionServiceFactory.GetAvailability();

        DrawDecisionProviderRow(listing);

        DrawDecisionModelRow(listing, availability);
        return availability != null && availability.Enabled;
    }

    private static void DrawDecisionProviderRow(Listing_Standard listing)
    {
        // 這裡只讓玩家選擇「本模組不使用決策模型」；關掉後整段隱藏，可在主頁的決策模型開關重新啟用。
        DrawDecisionDropdownRow(listing, "RimAgent.Settings.DecisionProvider", Settings.decisionProvider, "RimAgent.Settings.DecisionProviderTip",
            () =>
            [
                new(DecisionDefaultKey.Translate(), () => Settings.decisionProvider = string.Empty),
                new("RimAgent.Settings.DecisionProviderNone".Translate(), () => Settings.decisionEnabled = false)
            ]);
    }

    /// <summary>決策模型下拉：只列框架給出的模型（玩家在 RimLLM 決策分頁設定的那個），不另外向伺服器拉清單。</summary>
    private static void DrawDecisionModelRow(Listing_Standard listing, DecisionAvailability availability)
    {
        DrawDecisionDropdownRow(listing, "RimAgent.Settings.DecisionModel", Settings.decisionModel, "RimAgent.Settings.DecisionModelTip", () =>
        {
            var options = new List<FloatMenuOption>
            {
                new(DecisionDefaultKey.Translate(), () => Settings.decisionModel = string.Empty)
            };
            string frameworkModel = availability?.Enabled == true ? availability.Model : null;
            if (!string.IsNullOrEmpty(frameworkModel))
            {
                options.Add(new FloatMenuOption(frameworkModel, () => Settings.decisionModel = frameworkModel));
            }
            return options;
        });
    }

    private static void DrawDecisionDropdownRow(Listing_Standard listing, string labelKey, string current, string tipKey, Func<List<FloatMenuOption>> options)
    {
        const float labelWidth = 160f;
        Rect row = listing.GetRect(28f);
        var labelRect = new Rect(row.x, row.y + 3f, labelWidth, 24f);
        var dropdownRect = new Rect(labelRect.xMax + 4f, row.y, row.xMax - labelRect.xMax - 4f, 26f);

        Text.WordWrap = false;
        Widgets.Label(labelRect, labelKey.Translate().Truncate(labelWidth));
        Text.WordWrap = true;

        string shown = string.IsNullOrEmpty(current) ? DecisionDefaultKey.Translate().ToString() : current;
        if (Widgets.ButtonText(dropdownRect, shown.Truncate(dropdownRect.width - 12f)))
        {
            Find.WindowStack.Add(new FloatMenu(options()));
        }
        if (Mouse.IsOver(dropdownRect) || Mouse.IsOver(labelRect))
        {
            TooltipHandler.TipRegion(row, tipKey.Translate());
        }
        listing.Gap(6f);
    }

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
        string result = DecisionModelTester.Outcome switch
        {
            DecisionModelTester.TestOutcome.Ok => (string)"RimAgent.Settings.DecisionTestOk".Translate(DecisionModelTester.Detail),
            DecisionModelTester.TestOutcome.Unavailable => (string)"RimAgent.Settings.DecisionTestUnavailable".Translate(),
            DecisionModelTester.TestOutcome.NoAnswer => (string)"RimAgent.Settings.DecisionTestNoAnswer".Translate(),
            DecisionModelTester.TestOutcome.Failed => (string)"RimAgent.Settings.DecisionTestFailed".Translate(DecisionModelTester.Detail),
            _ => string.Empty,
        };
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
        while (used.Contains(CustomIdPrefix + number.ToString(CultureInfo.InvariantCulture))) number++;
        return CustomIdPrefix + number.ToString(CultureInfo.InvariantCulture);
    }

    private static void DrawDecisionRoutingSettings(Listing_Standard listing)
    {
        DrawReasoningRow(
            listing,
            "RimAgent.Settings.DecisionFallbackReasoning".Translate(),
            () => Settings.decisionFallbackReasoning,
            v => Settings.decisionFallbackReasoning = v,
            "RimAgent.Settings.DecisionFallbackReasoningTip".Translate());

        listing.Label(
            "RimAgent.Settings.DecisionMinConfidence".Translate(Settings.decisionRoutingMinConfidence.ToString("P0", CultureInfo.CurrentCulture)),
            -1f,
            "RimAgent.Settings.DecisionMinConfidenceTip".Translate());
        Settings.decisionRoutingMinConfidence = listing.Slider(Settings.decisionRoutingMinConfidence, 0f, 1f);

        listing.Gap(6f);

        Rect catHeader = listing.GetRect(28f);
        const float ActionButtonWidth = 90f;
        Rect resetRect = new(catHeader.xMax - ActionButtonWidth, catHeader.y, ActionButtonWidth, 26f);
        Rect addRect = new(resetRect.x - ActionButtonWidth - 6f, catHeader.y, ActionButtonWidth, 26f);
        Widgets.Label(new Rect(catHeader.x, catHeader.y + 3f, addRect.x - catHeader.x - 8f, 24f), "RimAgent.Settings.DecisionCategoriesHeader".Translate());

        if (Widgets.ButtonText(addRect, "RimAgent.Settings.DecisionCategoryAdd".Translate()))
        {
            ShowAddCategoryMenu();
        }

        if (Widgets.ButtonText(resetRect, "RimAgent.Settings.DecisionCategoriesReset".Translate()))
        {
            Settings.decisionCategories = DecisionRouteCategory.CreateDefaultCategories();
        }

        listing.Gap(4f);

        DrawCategoryList(listing);
    }

    private static void ShowAddCategoryMenu()
    {
        var options = new List<FloatMenuOption>
        {
            new("RimAgent.Settings.DecisionCategoryAddCustom".Translate(), () =>
            {
                string id = NextCustomCategoryId(Settings.decisionCategories);
                int nextNum = 1;
                if (id.Length > CustomIdPrefix.Length && int.TryParse(id[CustomIdPrefix.Length..], CultureInfo.InvariantCulture, out nextNum))
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

    /// <summary>逐一列出分類與各自的思考強度；刪除留到迴圈結束後才執行。</summary>
    private static void DrawCategoryList(Listing_Standard listing)
    {
        DecisionRouteCategory toDelete = null;
        for (int i = 0; i < Settings.decisionCategories.Count; i++)
        {
            DecisionRouteCategory cat = Settings.decisionCategories[i];
            Rect row = listing.GetRect(24f);
            const float DeleteBtnWidth = 50f;
            Rect delRect = new(row.xMax - DeleteBtnWidth, row.y, DeleteBtnWidth, 22f);
            Rect titleRect = new(row.x, row.y + 2f, row.width - DeleteBtnWidth - 6f, 20f);

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
