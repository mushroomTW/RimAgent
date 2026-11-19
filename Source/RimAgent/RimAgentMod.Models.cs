using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Agent;
using RimAgent.UI;
using RimLLM_Framework.Manager.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

public partial class RimAgentMod
{
    /// <summary>
    /// 開 RimLLM Framework 的設定頁。Dialog_ModSettings 一次只允許一個，會換掉目前這頁（關閉時照常存檔）。
    /// </summary>
    private static void OpenFrameworkSettings()
    {
        Mod framework = LoadedModManager.ModHandles.FirstOrDefault(m =>
            string.Equals(m.Content?.PackageId, FrameworkPackageId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(m.SettingsCategory()));
        if (framework == null)
        {
            Messages.Message("RimAgent.Settings.FrameworkNotFound".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        Find.WindowStack.Add(new Dialog_ModSettings(framework));
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

    /// <summary>
    /// 一格模型槽：模型下拉（跟隨 Agent／Fallback／備援鏈條目，選項與 Agent 視窗的模型選單相同）
    /// 加上選用的思考強度下拉。
    /// </summary>
    /// <param name="listing">介面繪製清單控制項。</param>
    /// <param name="label">模型槽標籤文字。</param>
    /// <param name="current">取得當前選定模型識別碼的委派。</param>
    /// <param name="set">設定選定模型識別碼的回呼。</param>
    /// <param name="tip">工具提示文字。</param>
    /// <param name="reasoning">思考強度的讀取；null 表示這列不顯示思考強度。</param>
    /// <param name="setReasoning">設定思考強度的回呼；null 表示不提供修改。</param>
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
        foreach (ReasoningLevel level in Enum.GetValues<ReasoningLevel>())
        {
            ReasoningLevel captured = level;
            options.Add(new FloatMenuOption(ReasoningKey(level).Translate(), () => setReasoning(captured)));
        }
        Find.WindowStack.Add(new FloatMenu(options));
    }

    /// <summary>思考強度的翻譯鍵。</summary>
    internal static string ReasoningKey(ReasoningLevel level)
    {
        return "RimAgent.Reasoning." + level;
    }
}
