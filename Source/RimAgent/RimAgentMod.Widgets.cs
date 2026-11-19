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

public partial class RimAgentMod
{
    /// <summary>區段標題：金色圓號、標題，下方一行白話說明。</summary>
    private static void DrawSectionHeader(Listing_Standard listing, int number, string title, string hint)
    {
        listing.Gap(18f);
        Rect row = listing.GetRect(30f);
        var badge = new Rect(row.x, row.y + 3f, 24f, 24f);
        Widgets.DrawBoxSolid(badge, Gold);
        Text.Anchor = TextAnchor.MiddleCenter;
        GUI.color = new Color(0.12f, 0.12f, 0.12f);
        Widgets.Label(new Rect(badge.x, badge.y + 1f, badge.width, badge.height), number.ToString());
        Text.Font = GameFont.Medium;
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = Gold;
        Widgets.Label(new Rect(badge.xMax + 10f, row.y, row.width - badge.width - 10f, row.height), title);
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;
        DrawHint(listing, hint, 0f, Dim);
        listing.Gap(4f);
    }

    /// <summary>小字說明，依寬度自動換行。</summary>
    private static void DrawHint(Listing_Standard listing, string text, float indent, Color color)
    {
        Text.Font = GameFont.Tiny;
        float width = listing.ColumnWidth - indent;
        Rect rect = listing.GetRect(Text.CalcHeight(text, width));
        GUI.color = color;
        Widgets.Label(new Rect(rect.x + indent, rect.y, width, rect.height), text);
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
    }

    private static void DrawCooldownSegment(Rect segRow, float segX, float segWidth, int minutes)
    {
        bool on = Settings.autonomousCooldownMinutes == minutes;
        var seg = new Rect(segX, segRow.y, segWidth - 2f, segRow.height);
        Widgets.DrawBoxSolid(seg, on ? SelectedFill : PanelFill);
        GUI.color = on ? Gold : PanelBorder;
        Widgets.DrawBox(seg);
        GUI.color = on ? Gold : Color.white;
        Text.Anchor = TextAnchor.MiddleCenter;
        string label = minutes == 0 ? "RimAgent.Settings.CooldownNow".Translate() : "RimAgent.Settings.CooldownEvery".Translate(minutes);
        Widgets.Label(seg, label);
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = Color.white;
        if (Mouse.IsOver(seg))
        {
            Widgets.DrawHighlight(seg);
            TooltipHandler.TipRegion(seg, "RimAgent.Settings.AutoCooldownTip".Translate());
        }
        if (Widgets.ButtonInvisible(seg)) Settings.autonomousCooldownMinutes = minutes;
    }

    /// <summary>大開關卡片：整張可點，開啟時綠框。</summary>
    private static void DrawToggleCard(Listing_Standard listing, ref bool value, string title, string desc, string tip)
    {
        Rect card = listing.GetRect(58f);
        Widgets.DrawBoxSolid(card, PanelFill);
        GUI.color = value ? Green : PanelBorder;
        Widgets.DrawBox(card, 2);
        GUI.color = Color.white;
        if (Mouse.IsOver(card))
        {
            Widgets.DrawHighlight(card);
            TooltipHandler.TipRegion(card, tip);
        }
        Widgets.CheckboxDraw(card.x + 12f, card.y + (card.height - 24f) / 2f, value, false);
        float textX = card.x + 48f;
        float textWidth = card.xMax - textX - 10f;
        GUI.color = value ? Green : Color.white;
        Widgets.Label(new Rect(textX, card.y + 7f, textWidth, 24f), title);
        GUI.color = Dim;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(textX, card.y + 31f, textWidth, 20f), desc);
        Text.Font = GameFont.Small;
        GUI.color = Color.white;
        if (Widgets.ButtonInvisible(card)) value = !value;
    }

    /// <summary>一列勾選：名稱下面直接一行小字說明，整列可點；完整說明在提示。</summary>
    /// <param name="listing">介面繪製清單控制項。</param>
    /// <param name="value">勾選狀態布林變數參考。</param>
    /// <param name="label">選項名稱標籤。</param>
    /// <param name="desc">選項下方簡短說明文字。</param>
    /// <param name="tip">工具提示文字。</param>
    /// <param name="indent">左側縮排距離。</param>
    /// <param name="disabled">true 時勾選框與文字變灰、點了不會切換。</param>
    private static void DrawToggleRow(Listing_Standard listing, ref bool value, string label, string desc, string tip, float indent, bool disabled = false)
    {
        float textX = indent + 34f;
        float textWidth = listing.ColumnWidth - textX;
        Text.Font = GameFont.Tiny;
        float descHeight = Text.CalcHeight(desc, textWidth);
        Text.Font = GameFont.Small;
        Rect row = listing.GetRect(24f + descHeight + 6f);
        var hit = new Rect(row.x + indent, row.y, row.width - indent, row.height);
        Widgets.CheckboxDraw(hit.x + 4f, row.y + 2f, value, disabled);
        GUI.color = disabled ? Dim : Color.white;
        Widgets.Label(new Rect(row.x + textX, row.y + 1f, textWidth, 24f), label);
        Text.Font = GameFont.Tiny;
        GUI.color = disabled ? new Color(Dim.r, Dim.g, Dim.b, 0.6f) : Dim;
        Widgets.Label(new Rect(row.x + textX, row.y + 24f, textWidth, descHeight), desc);
        GUI.color = Color.white;
        Text.Font = GameFont.Small;
        if (Mouse.IsOver(hit))
        {
            if (!disabled) Widgets.DrawHighlight(hit);
            if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(hit, tip);
        }
        if (!disabled && Widgets.ButtonInvisible(hit)) value = !value;
        listing.Gap(4f);
    }

    /// <summary>展開／收合按鈕；按下回傳 true，由呼叫端切換狀態。</summary>
    private static bool DrawExpandButton(Listing_Standard listing, bool expanded, string showLabel, string hideLabel, string tip)
    {
        Rect row = listing.GetRect(30f);
        // ButtonText 靠左對齊時文字貼著框線；按鈕只畫底，文字另外留邊距畫，放不下就截斷。
        bool clicked = Widgets.ButtonText(row, string.Empty);
        const float Padding = 10f;
        var labelRect = new Rect(row.x + Padding, row.y, row.width - Padding * 2f, row.height);
        string label = expanded ? "[ − ]  " + hideLabel : "[ + ]  " + showLabel;
        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        Widgets.Label(labelRect, label.Truncate(labelRect.width));
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;
        if (!string.IsNullOrEmpty(tip)) TooltipHandler.TipRegion(row, tip);
        return clicked;
    }
}
