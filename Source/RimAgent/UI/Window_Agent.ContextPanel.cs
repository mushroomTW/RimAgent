using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Agent;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

public partial class Window_Agent
{
    /// <summary>
    /// 點標題列上下文圓盤開啟的分析面板：依類別列出下一次請求的上下文分布、剩餘空間與自動壓縮門檻。
    /// 各類別是字元估算；供應商有回報總量時按比例縮放到回報值，只看分布不看絕對精準度。
    /// </summary>
    private sealed class ContextPanel : Window
    {
        private const float PanelWidth = 340f;
        private const float RowHeight = 22f;
        private const float Pad = 12f;
        private const float BarHeight = 8f;
        /// <summary>估算每隔多久重算一次（組系統提示不便宜，不每幀做）。</summary>
        private const float RefreshSeconds = 1f;

        private static readonly Dictionary<string, Color> PartColors = new()
        {
            ["SystemPrompt"] = new Color(0.90f, 0.55f, 0.20f),
            ["Tools"] = new Color(0.15f, 0.70f, 0.55f),
            ["ToolPacks"] = new Color(0.55f, 0.80f, 0.35f),
            ["Subagents"] = new Color(0.75f, 0.50f, 0.90f),
            ["Memory"] = new Color(0.85f, 0.75f, 0.30f),
            ["UserMessages"] = new Color(0.35f, 0.60f, 0.95f),
            ["AssistantMessages"] = new Color(0.55f, 0.78f, 1f),
            ["ToolCalls"] = new Color(0.65f, 0.68f, 0.72f),
            ["ToolResults"] = new Color(0.45f, 0.48f, 0.52f)
        };
        private static readonly Color FreeColor = new(0.20f, 0.23f, 0.27f);

        private readonly ColonyAgentSession _session;
        private IReadOnlyList<ColonyAgentSession.ContextPart> _parts = Array.Empty<ColonyAgentSession.ContextPart>();
        private float _nextRefresh;
        private static int _closedFrame = -1;

        /// <summary>面板是否在這一幀被關掉（例如按下滑鼠時被「點外面關閉」關掉）。</summary>
        public static bool ClosedThisFrame => _closedFrame == Time.frameCount;

        public override Vector2 InitialSize => new(PanelWidth, 420f);
        protected override float Margin => 0f;

        private ContextPanel(ColonyAgentSession session, Vector2 anchor)
        {
            _session = session;
            layer = WindowLayer.GameUI;
            doWindowBackground = false;
            drawShadow = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            closeOnAccept = false;
            soundAppear = null;
            windowRect = new Rect(anchor.x, anchor.y, PanelWidth, 420f);
        }

        /// <summary>開或關；anchor 是面板右上角要貼齊的螢幕座標。</summary>
        public static void Toggle(ColonyAgentSession session, Vector2 anchorTopRight)
        {
            ContextPanel existing = Find.WindowStack.WindowOfType<ContextPanel>();
            if (existing != null)
            {
                existing.Close(false);
                return;
            }
            float x = Mathf.Clamp(anchorTopRight.x - PanelWidth, 0f, Mathf.Max(0f, Verse.UI.screenWidth - PanelWidth));
            Find.WindowStack.Add(new ContextPanel(session, new Vector2(x, anchorTopRight.y)));
        }

        protected override void SetInitialSizeAndPosition()
        {
            // 位置在建構時就定好；這裡只保證不超出螢幕。
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Verse.UI.screenHeight - windowRect.height));
        }

        public override void PostClose()
        {
            base.PostClose();
            _closedFrame = Time.frameCount;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // session 被重建（讀檔、重置）後舊物件不再更新，面板直接關掉。
            if (RimAgentGameComponent.Instance?.Session != _session)
            {
                Close(false);
                return;
            }
            if (Time.realtimeSinceStartup >= _nextRefresh)
            {
                _nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
                _parts = _session.GetContextBreakdown();
            }

            ColonyAgentSession.ContextStatus status = _session.GetContextStatus();
            long estimated = _parts.Sum(p => p.Tokens);
            // 有回報總量就把估算按比例縮放到回報值。
            double scale = status.LastInputTokens is > 0 && estimated > 0 ? (double)status.LastInputTokens.Value / estimated : 1d;
            long used = status.LastInputTokens ?? estimated;
            long? window = status.ContextWindow;

            DrawRoundedBox(inRect, BgColor, BorderColor, WindowRadius);
            float y = Pad;
            float width = inRect.width - Pad * 2f;

            Text.Font = GameFont.Small;
            GUI.color = TitleColor;
            Widgets.Label(new Rect(Pad, y, width, 24f), "RimAgent.ContextPanel.Title".Translate());
            y += 26f;

            // 總覽：已使用比例與用量／上限。
            string usedLabel = window is > 0
                ? "RimAgent.ContextPanel.Used".Translate(Mathf.RoundToInt(100f * used / window.Value)).ToString()
                : "RimAgent.ContextPanel.WindowUnknown".Translate().ToString();
            GUI.color = Color.white;
            Widgets.Label(new Rect(Pad, y, width, 24f), usedLabel);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = ToolTextColor;
            Widgets.Label(new Rect(Pad, y, width, 24f), window is > 0 ? FormatTokens(used) + " / " + FormatTokens(window.Value) : FormatTokens(used));
            Text.Anchor = TextAnchor.UpperLeft;
            y += 26f;

            // 堆疊長條：各類別依序排開，剩下的是可用空間；門檻位置畫一條直線。
            var bar = new Rect(Pad, y, width, BarHeight);
            DrawRoundedBox(bar, FreeColor, Color.clear, BarHeight / 2f);
            double total = window is > 0 ? window.Value : Math.Max(1, used);
            float x = bar.x;
            foreach (ColonyAgentSession.ContextPart part in _parts)
            {
                float w = (float)(bar.width * Math.Min(1d, part.Tokens * scale / total));
                if (w < 0.5f) continue;
                w = Mathf.Min(w, bar.xMax - x);
                Widgets.DrawBoxSolid(new Rect(x, bar.y, w, bar.height), PartColors[part.Key]);
                x += w + 1f;
                if (x >= bar.xMax) break;
            }
            if (window is > 0 && status.AutoCompactEnabled)
            {
                float mark = bar.x + bar.width * status.Threshold / 100f;
                Widgets.DrawBoxSolid(new Rect(mark - 1f, bar.y - 3f, 2f, bar.height + 6f), PauseColor);
            }
            y += BarHeight + 12f;

            // 各類別明細。
            Text.Font = GameFont.Small;
            foreach (ColonyAgentSession.ContextPart part in _parts)
            {
                y = DrawRow(y, width, PartColors[part.Key], PartLabel(part), (long)Math.Round(part.Tokens * scale), window);
            }
            if (window is > 0)
            {
                y = DrawRow(y, width, FreeColor, "RimAgent.ContextPanel.Free".Translate(), Math.Max(0, window.Value - used), window);
            }

            y += 6f;
            Widgets.DrawBoxSolid(new Rect(Pad, y, width, 1f), DividerColor);
            y += 8f;

            // 自動壓縮門檻與剩餘量。
            GUI.color = ToolTextColor;
            Text.Font = GameFont.Tiny;
            string compact;
            if (!status.AutoCompactEnabled) compact = "RimAgent.ContextPanel.CompactOff".Translate();
            else if (window is > 0)
            {
                long budget = window.Value * status.Threshold / 100;
                compact = "RimAgent.ContextPanel.CompactAt".Translate(status.Threshold, FormatTokens(budget), FormatTokens(Math.Max(0, budget - used)));
            }
            else compact = "RimAgent.ContextPanel.CompactUnknown".Translate(status.Threshold);
            float compactHeight = Text.CalcHeight(compact, width);
            Widgets.Label(new Rect(Pad, y, width, compactHeight), compact);
            y += compactHeight + 4f;

            string note = status.LastInputTokens.HasValue
                ? "RimAgent.ContextPanel.NoteScaled".Translate()
                : "RimAgent.ContextPanel.NoteEstimated".Translate();
            GUI.color = ControlBorderColor;
            float noteHeight = Text.CalcHeight(note, width);
            Widgets.Label(new Rect(Pad, y, width, noteHeight), note);
            y += noteHeight + Pad;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 內容高度決定面板高度；下一幀生效。
            if (Mathf.Abs(windowRect.height - y) > 0.5f) windowRect.height = y;
        }

        private static float DrawRow(float y, float width, Color color, string label, long tokens, long? window)
        {
            var row = new Rect(Pad, y, width, RowHeight);
            var swatch = new Rect(row.x, row.y + 6f, 10f, 10f);
            DrawRoundedBox(swatch, color, Color.clear, 2f);
            GUI.color = new Color(0.90f, 0.93f, 0.96f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x + 18f, row.y, row.width - 140f, row.height), label);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = ToolTextColor;
            Widgets.Label(new Rect(row.xMax - 122f, row.y, 66f, row.height), FormatTokens(tokens));
            string percent = window is > 0 ? (100.0 * tokens / window.Value).ToString("0.0") + "%" : "—";
            Widgets.Label(new Rect(row.xMax - 56f, row.y, 56f, row.height), percent);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            return y + RowHeight;
        }

        private static string PartLabel(ColonyAgentSession.ContextPart part)
        {
            string name = ("RimAgent.ContextPanel.Part." + part.Key).Translate();
            return part.Count > 0 ? name + " (" + part.Count + ")" : name;
        }

        private static string FormatTokens(long tokens)
        {
            return tokens >= 1000 ? (tokens / 1000.0).ToString("0.0") + "k" : tokens.ToString();
        }
    }
}
