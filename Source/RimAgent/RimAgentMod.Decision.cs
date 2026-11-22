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

    private static void DrawDecisionRoutingSettings(Listing_Standard listing)
    {
        listing.Label(
            "RimAgent.Settings.DecisionMinConfidence".Translate(Settings.decisionRoutingMinConfidence.ToString("P0", CultureInfo.CurrentCulture)),
            -1f,
            "RimAgent.Settings.DecisionMinConfidenceTip".Translate());
        Settings.decisionRoutingMinConfidence = listing.Slider(Settings.decisionRoutingMinConfidence, 0f, 1f);

        DrawReasoningRow(
            listing,
            "RimAgent.Settings.DecisionFallbackReasoning".Translate(),
            () => Settings.decisionFallbackReasoning,
            v => Settings.decisionFallbackReasoning = v,
            "RimAgent.Settings.DecisionFallbackReasoningTip".Translate());
    }
}
