using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using RimAgent.Agent;
using RimAgent.Tools;
using RimLLM_Framework;
using RimLLM_Framework.Manager.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent;

public partial class RimAgentMod
{
    /// <summary>三張權限方案卡片；目前權限不符合任何方案時下方提示「自訂」。</summary>
    private static void DrawPresetCards(Listing_Standard listing)
    {
        RimAgentSettings.PermissionPreset? current = Settings.MatchPreset();
        Rect row = listing.GetRect(PresetCardHeight);
        float width = (row.width - PresetCardGap * (Presets.Length - 1)) / Presets.Length;
        for (int i = 0; i < Presets.Length; i++)
        {
            RimAgentSettings.PermissionPreset preset = Presets[i];
            bool on = current == preset;
            var card = new Rect(row.x + i * (width + PresetCardGap), row.y, width, row.height);
            Widgets.DrawBoxSolid(card, on ? SelectedFill : PanelFill);
            GUI.color = on ? Gold : PanelBorder;
            Widgets.DrawBox(card, 2);
            GUI.color = Color.white;
            if (Mouse.IsOver(card)) Widgets.DrawHighlight(card);

            Rect inner = card.ContractedBy(10f);
            string key = PresetKey(preset);
            if (preset == RimAgentSettings.PermissionPreset.Recommended)
            {
                string tag = "RimAgent.Settings.PresetTagRecommended".Translate();
                Text.Font = GameFont.Tiny;
                float tagWidth = Text.CalcSize(tag).x + 10f;
                var tagRect = new Rect(card.xMax - tagWidth - 6f, card.y + 6f, tagWidth, 18f);
                Widgets.DrawBoxSolid(tagRect, Gold);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = new Color(0.12f, 0.12f, 0.12f);
                Widgets.Label(tagRect, tag);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            GUI.color = on ? Gold : Color.white;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 24f), key.Translate());
            GUI.color = Dim;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y + 26f, inner.width, inner.height - 26f), (key + "Desc").Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(card)) Settings.ApplyPreset(preset);
        }
        if (current == null)
        {
            listing.Gap(4f);
            DrawHint(listing, "RimAgent.Settings.PresetCustomNote".Translate(), 0f, Gold);
        }
    }

    /// <summary>可收合的權限清單：依綠／黃／紅分組，每項一行名稱加一行說明，整格可點。</summary>
    private static void DrawPermissionList(Listing_Standard listing)
    {
        listing.Gap(8f);
        if (DrawExpandButton(listing, _showPermissionList, "RimAgent.Settings.PermissionListShow".Translate(), "RimAgent.Settings.PermissionListHide".Translate(), null))
        {
            _showPermissionList = !_showPermissionList;
        }
        if (!_showPermissionList) return;

        EnsurePermissionLabels();
        foreach ((string key, Color color, ToolCategory[] members) in PermissionGroups)
        {
            listing.Gap(8f);
            Rect header = listing.GetRect(24f);
            Widgets.DrawBoxSolid(new Rect(header.x + 2f, header.y + 7f, 10f, 10f), color);
            Widgets.Label(new Rect(header.x + 20f, header.y, header.width - 20f, header.height), key.Translate());

            int rows = (members.Length + 1) / 2;
            Rect grid = listing.GetRect(rows * PermissionRowHeight);
            float columnWidth = (grid.width - PermissionColumnGap) / 2f;
            for (int i = 0; i < members.Length; i++)
            {
                int index = (int)members[i];
                var cell = new Rect(grid.x + (i % 2) * (columnWidth + PermissionColumnGap), grid.y + (i / 2) * PermissionRowHeight, columnWidth, PermissionRowHeight - 4f);
                Widgets.DrawBoxSolid(new Rect(cell.x, cell.y, 3f, cell.height), color);
                bool allowed = Settings.GetAllow(members[i]);
                Widgets.CheckboxDraw(cell.x + 10f, cell.y + 3f, allowed, false);
                float textX = cell.x + 42f;
                float textWidth = cell.xMax - textX;
                Text.WordWrap = false;
                Widgets.Label(new Rect(textX, cell.y, textWidth, 22f), _permissionLabels[index].Truncate(textWidth));
                Text.Font = GameFont.Tiny;
                GUI.color = Dim;
                Widgets.Label(new Rect(textX, cell.y + 21f, textWidth, 18f), _permissionDescs[index].Truncate(textWidth));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                if (Mouse.IsOver(cell))
                {
                    Widgets.DrawHighlight(cell);
                    TooltipHandler.TipRegion(cell, _permissionTips[index]);
                }
                if (Widgets.ButtonInvisible(cell)) Settings.SetAllow(members[i], !allowed);
            }
        }
    }

    /// <summary>名稱、一行說明與完整提示只在語言變了才重組；提示最前面放名稱與說明，截斷時仍看得到全文。</summary>
    private static void EnsurePermissionLabels()
    {
        LoadedLanguage language = LanguageDatabase.activeLanguage;
        if (_permissionLabels != null && language == _permissionLanguage) return;
        _permissionLanguage = language;
        _permissionLabels = new string[Categories.Length];
        _permissionDescs = new string[Categories.Length];
        _permissionTips = new string[Categories.Length];
        for (int i = 0; i < Categories.Length; i++)
        {
            string key = PermissionKey(Categories[i]);
            _permissionLabels[i] = key.Translate();
            _permissionDescs[i] = (key + "Desc").Translate();
            _permissionTips[i] = _permissionLabels[i] + "\n" + _permissionDescs[i] + "\n\n" + (key + "Tip").Translate();
        }
    }

    /// <summary>工具類別權限的翻譯鍵（提示另加 "Tip" 後綴）。設定鍵以類別英文命名，只有 MCP 全大寫。</summary>
    internal static string PermissionKey(ToolCategory category)
    {
        return "RimAgent.Settings." + (category == ToolCategory.Mcp ? "MCP" : category.ToString());
    }

    /// <summary>權限方案卡片的翻譯鍵（說明另加 "Desc" 後綴）。</summary>
    internal static string PresetKey(RimAgentSettings.PermissionPreset preset)
    {
        return "RimAgent.Settings.Preset." + preset;
    }
}
