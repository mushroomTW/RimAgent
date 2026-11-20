#nullable disable

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimLLM_Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent
{
    // 上下文圓盤：每個百分比一張環形貼圖（0～100，用到才產生）。
    private const int GaugeTexSize = 34;

    private static readonly Dictionary<int, Texture2D> GaugeArcTex = [];

    /// <summary>
    /// 最近一次輸入 token 佔模型上下文上限的比例，畫成順時針填滿的圓環。
    /// 到自動壓縮門檻變黃、90% 以上變紅；上限或用量未知時只畫底環。
    /// </summary>
    private static void DrawContextGauge(Rect rect, ColonyAgentSession.ContextStatus status)
    {
        if (Event.current.type != EventType.Repaint) return;
        Color old = GUI.color;
        GUI.color = new Color(DividerColor.r, DividerColor.g, DividerColor.b, 0.55f);
        GUI.DrawTexture(rect, GetGaugeArc(100));
        if (status.LastInputTokens.HasValue && status.ContextWindow is > 0)
        {
            float ratio = Mathf.Clamp01((float)status.LastInputTokens.Value / status.ContextWindow.Value);
            int percent = Mathf.Clamp(Mathf.CeilToInt(ratio * 100f), ratio > 0f ? 1 : 0, 100);
            if (percent >= 90) GUI.color = EmergencyColor;
            else if (status.AutoCompactEnabled && percent >= status.Threshold) GUI.color = PauseColor;
            else GUI.color = ToolActionColor;
            if (percent > 0) GUI.DrawTexture(rect, GetGaugeArc(percent));
        }
        GUI.color = old;
    }

    private static Texture2D GetGaugeArc(int percent)
    {
        if (GaugeArcTex.TryGetValue(percent, out Texture2D cached)) return cached;
        const int size = GaugeTexSize;
        const float half = size / 2f;
        const float outer = half - 1f;
        const float inner = outer - 5f;
        float limit = percent / 100f;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
            float dx = x + 0.5f - half;
            float dy = y + 0.5f - half; // 貼圖 y 向上
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float alpha = Mathf.Clamp01(outer + 0.5f - distance) * Mathf.Clamp01(distance - inner + 0.5f);
            // 從正上方開始順時針。
            float angle = Mathf.Atan2(dx, dy);
            if (angle < 0f) angle += Mathf.PI * 2f;
            if (angle / (Mathf.PI * 2f) > limit) alpha = 0f;
            pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
        }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        GaugeArcTex[percent] = texture;
        return texture;
    }

    private void DrawTitleBar(Rect titleRect, ColonyAgentSession session)
    {
        var bgRect = new Rect(titleRect.x + 1f, titleRect.y + 1f, titleRect.width - 2f, titleRect.height - 1f);
        // 只圓上面兩角：在標題列範圍內裁切一個往下延伸的圓角塊，下緣的圓角被裁掉。
        Widgets.BeginGroup(bgRect);
        DrawRoundedBox(new Rect(0f, 0f, bgRect.width, bgRect.height + WindowRadius * 2f),
            new Color(0.08f, 0.11f, 0.14f, 0.6f), Color.clear, WindowRadius - 1f);
        Widgets.EndGroup();

        var labelRect = new Rect(titleRect.x + 12f, titleRect.y + 4f, titleRect.width - 100f, titleRect.height - 4f);
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = TitleColor;
        Widgets.Label(labelRect, "RimAgent.WindowTitle".Translate());
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;

        // 只顯示供應商實際回報的最近一次主模型輸入 token；
        // 訊息數與折疊門檻放在提示中，避免把訊息數誤標成 token 或視窗容量。
        ColonyAgentSession.ContextStatus status = session.GetContextStatus();
        var gaugeRect = new Rect(titleRect.xMax - 73f, titleRect.y + 7f, 17f, 17f);
        DrawContextGauge(gaugeRect, status);
        if (Mouse.IsOver(gaugeRect)) DrawRoundedBox(gaugeRect.ExpandedBy(2f), new Color(1f, 1f, 1f, 0.10f), Color.clear, 4f);
        // 面板開著時點圓盤：按下就被「點外面關閉」關掉，放開時按鈕才觸發，不擋會立刻又開回來。
        if (Event.current.type == EventType.MouseDown && Mouse.IsOver(gaugeRect.ExpandedBy(2f)))
            _gaugeClosedPanel = ContextPanel.ClosedThisFrame;
        if (Widgets.ButtonInvisible(gaugeRect.ExpandedBy(2f)) && !_gaugeClosedPanel)
        {
            ContextPanel.ToggleContextPanel(session, new Vector2(windowRect.x + gaugeRect.xMax + 2f, windowRect.y + gaugeRect.yMax + 6f));
        }
        // 提示字串只在滑鼠停在上面時才組。
        if (Mouse.IsOver(gaugeRect)) DrawContextGaugeTooltip(gaugeRect, status);
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        Text.Font = GameFont.Small;

        var closeRect = new Rect(titleRect.xMax - 26f, titleRect.y + 4f, 20f, 20f);
        if (Mouse.IsOver(closeRect))
        {
            DrawRoundedBox(closeRect, CloseButHoverColor, Color.clear, 4f);
        }
        Text.Anchor = TextAnchor.MiddleCenter;
        GUI.color = Mouse.IsOver(closeRect) ? EmergencyColor : BorderColor;
        Widgets.Label(closeRect, "✕");
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;

        if (Widgets.ButtonInvisible(closeRect))
        {
            Close();
            return;
        }

        var collapseRect = new Rect(closeRect.x - 24f, closeRect.y, 20f, 20f);
        if (Mouse.IsOver(collapseRect))
        {
            DrawRoundedBox(collapseRect, new Color(1f, 1f, 1f, 0.12f), Color.clear, 4f);
        }
        Text.Anchor = TextAnchor.MiddleCenter;
        GUI.color = Mouse.IsOver(collapseRect) ? TitleColor : BorderColor;
        Widgets.Label(collapseRect, "—");
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
        TooltipHandler.TipRegion(collapseRect, "RimAgent.Window.CollapseTip".Translate());

        if (Widgets.ButtonInvisible(collapseRect))
        {
            Collapse(session);
        }
    }

    private static void DrawContextGaugeTooltip(Rect gaugeRect, ColonyAgentSession.ContextStatus status)
    {
        string compact = status.AutoCompactEnabled
            ? "RimAgent.Context.CompactOn".Translate(status.Messages, status.Threshold)
            : "RimAgent.Context.CompactOff".Translate(status.Messages);
        string window = status.ContextWindow.HasValue
            ? "RimAgent.Context.WindowKnown".Translate(status.ContextWindow.Value.ToString("N0", CultureInfo.InvariantCulture))
            : "RimAgent.Context.WindowUnknown".Translate();
        TooltipHandler.TipRegion(gaugeRect, "RimAgent.Context.Tooltip".Translate(window, compact));
    }
}
