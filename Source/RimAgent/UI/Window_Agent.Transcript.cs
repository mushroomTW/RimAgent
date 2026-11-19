using System;
using System.Collections.Generic;
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
    private void DrawTranscript(Rect outRect, ColonyAgentSession session)
    {
        float width = outRect.width - 16f;
        EnsureTranscriptLines(session.Transcript, width, out bool deferReflow);

        float lineWidth = deferReflow ? _linesWidth : width;
        GetWorkingIndicator(session.IsBusy, session.Activity.Current, width, out string working, out float workingHeight);
        float total = _linesTotal + (working != null ? workingHeight + EntryGap : 0f);

        TranscriptScrollLayout scrollLayout = UpdateScrollPosition(total, width, outRect);
        float visibleTop = Mathf.Clamp(_scroll.y, 0f, scrollLayout.MaxScroll);
        float visibleBottom = visibleTop + outRect.height;

        Widgets.BeginScrollView(outRect, ref _scroll, scrollLayout.ViewRect);
        float y = 0f;
        TranscriptEntry clickedTool = null;
        TranscriptEntry clickedGroup = null;
        var drawContext = new TranscriptDrawContext(visibleTop, visibleBottom, lineWidth, width, scrollLayout.MouseOnJump);
        DrawTranscriptLines(_lines, y, in drawContext, ref clickedTool, ref clickedGroup, out y);
        if (working != null)
        {
            Widgets.Label(new Rect(0f, y, width, workingHeight), working);
        }
        GUI.color = Color.white;
        Widgets.EndScrollView();

        if (clickedTool != null)
        {
            _toolExpanded[clickedTool] = !IsToolExpanded(clickedTool);
            RelayoutEntry(clickedTool);
        }
        if (clickedGroup != null)
        {
            _groupExpanded[clickedGroup] = !IsGroupExpanded(clickedGroup);
            // 群組展開會增減整串行：直接下一幀整份重排，比逐筆修補簡單。
            _linesTranscript = null;
        }

        if (scrollLayout.ShowJump)
        {
            DrawJumpButton(scrollLayout.JumpRect);
        }
    }

    private void DrawTranscriptLines(List<TranscriptLine> lines, float y, in TranscriptDrawContext ctx, ref TranscriptEntry clickedTool, ref TranscriptEntry clickedGroup, out float outY)
    {
        outY = y;
        foreach (TranscriptLine line in lines)
        {
            if (line.TurnStart)
            {
                if (outY + TurnGap >= ctx.VisibleTop && outY <= ctx.VisibleBottom)
                {
                    Widgets.DrawBoxSolid(new Rect(0f, outY + TurnGap / 2f, ctx.LineWidth, 1f), DividerColor);
                }
                outY += TurnGap;
            }
            if (outY + line.Height >= ctx.VisibleTop && outY <= ctx.VisibleBottom)
            {
                DrawTranscriptLine(line, outY, ctx.LineWidth, ctx.Width, ctx.MouseOnJump, ref clickedTool, ref clickedGroup);
            }
            outY += line.Height + EntryGap;
        }
    }

    private readonly struct TranscriptDrawContext
    {
        public readonly float VisibleTop;
        public readonly float VisibleBottom;
        public readonly float LineWidth;
        public readonly float Width;
        public readonly bool MouseOnJump;

        public TranscriptDrawContext(float visibleTop, float visibleBottom, float lineWidth, float width, bool mouseOnJump)
        {
            VisibleTop = visibleTop;
            VisibleBottom = visibleBottom;
            LineWidth = lineWidth;
            Width = width;
            MouseOnJump = mouseOnJump;
        }
    }

    private struct TranscriptScrollLayout
    {
        public Rect ViewRect;
        public float MaxScroll;
        public bool ShowJump;
        public Rect JumpRect;
        public bool MouseOnJump;
    }

    private void EnsureTranscriptLines(AgentTranscript transcript, float width, out bool deferReflow)
    {
        bool sameTranscript = ReferenceEquals(transcript, _linesTranscript);
        bool widthChanged = Mathf.Abs(width - _linesWidth) > 0.001f;
        deferReflow = widthChanged && sameTranscript && transcript.Version == _linesTranscriptVersion
                           && _linesWidth > 0f && !WidthSettled(width);
        if (!deferReflow && (!sameTranscript || transcript.Version != _linesTranscriptVersion || widthChanged))
        {
            IReadOnlyList<TranscriptEntry> entries = transcript.Snapshot(out int version, out int epoch);
            bool appendOnly = sameTranscript
                              && epoch == _linesTranscriptEpoch
                              && Mathf.Abs(width - _linesWidth) <= 0.001f
                              && entries.Count >= _linesEntryCount;
            if (appendOnly) AppendLines(entries, _linesEntryCount, width);
            else RebuildLines(entries, width);
            _linesTranscript = transcript;
            _linesTranscriptVersion = version;
            _linesTranscriptEpoch = epoch;
            _linesWidth = width;
        }
    }

    private void GetWorkingIndicator(bool isBusy, AgentActivity activity, float width, out string working, out float workingHeight)
    {
        working = null;
        workingHeight = 0f;
        if (!isBusy) return;

        int phase = WorkingPhase();
        if (_workingText == null || !ReferenceEquals(activity, _workingActivity) || phase != _workingPhase)
        {
            _workingActivity = activity;
            _workingPhase = phase;
            _workingText = ColorTagPrefix + PauseHex + ">"
                + ActivityText(activity).TrimEnd('…', '.', ' ') + WorkingDots(phase) + ColorTagClose;
        }
        working = _workingText;
        if (Mathf.Abs(width - _workingHeightWidth) > 0.001f || !ReferenceEquals(working, _workingHeightText))
        {
            _workingHeight = Text.CalcHeight(working, width);
            _workingHeightWidth = width;
            _workingHeightText = working;
        }
        workingHeight = _workingHeight;
    }

    private TranscriptScrollLayout UpdateScrollPosition(float total, float width, Rect outRect)
    {
        var layout = new TranscriptScrollLayout
        {
            ViewRect = new Rect(0f, 0f, width, Math.Max(total, outRect.height))
        };
        layout.MaxScroll = layout.ViewRect.height - outRect.height;
        if (_scrollToBottom || _scroll.y >= _lastMaxScroll - StickToBottomSlack)
        {
            _scroll.y = layout.MaxScroll;
            _scrollToBottom = false;
            _seenVersion = _linesTranscriptVersion;
        }
        _lastMaxScroll = layout.MaxScroll;
        layout.ShowJump = _scroll.y < layout.MaxScroll - StickToBottomSlack;
        layout.JumpRect = new Rect(outRect.center.x - JumpButtonWidth / 2f, outRect.yMax - JumpButtonHeight - 6f, JumpButtonWidth, JumpButtonHeight);
        layout.MouseOnJump = layout.ShowJump && Mouse.IsOver(layout.JumpRect);
        return layout;
    }

    private void DrawTranscriptLine(
        TranscriptLine line,
        float y,
        float lineWidth,
        float visibleWidth,
        bool mouseOnJump,
        ref TranscriptEntry clickedTool,
        ref TranscriptEntry clickedGroup)
    {
        var lineRect = new Rect(0f, y, lineWidth, line.Height);
        switch (line.Kind)
        {
            case LineKind.Screenshot:
                DrawScreenshotLine(lineRect, line);
                break;
            case LineKind.PlayerBubble:
                DrawPlayerBubbleLine(lineRect, line, y, visibleWidth);
                break;
            case LineKind.ToolGroupHeader:
                DrawToolGroupHeaderLine(lineRect, line, mouseOnJump, ref clickedGroup);
                break;
            case LineKind.Table:
                DrawTableLine(lineRect, line, mouseOnJump);
                break;
            case LineKind.ToolHeader:
                DrawToolHeaderLine(lineRect, line, mouseOnJump, ref clickedTool);
                break;
            default:
                DrawDefaultLine(lineRect, line);
                break;
        }
    }

    private void DrawScreenshotLine(Rect lineRect, TranscriptLine line)
    {
        DrawScreenshotPreview(lineRect, line.ImagePath);
    }

    private static void DrawPlayerBubbleLine(Rect lineRect, TranscriptLine line, float y, float visibleWidth)
    {
        float bubbleRight = Mathf.Min(lineRect.xMax, visibleWidth);
        var bubble = new Rect(Mathf.Max(0f, bubbleRight - line.BubbleWidth), y, line.BubbleWidth, line.Height);
        GUI.Box(bubble, GUIContent.none, GetBubbleStyle());
        DrawSelectableLine(bubble.ContractedBy(BubblePadX, BubblePadY), line.Text, line.PlainText);
    }

    private void DrawToolGroupHeaderLine(Rect lineRect, TranscriptLine line, bool mouseOnJump, ref TranscriptEntry clickedGroup)
    {
        if (!mouseOnJump)
        {
            if (Mouse.IsOver(lineRect))
            {
                Widgets.DrawHighlight(lineRect);
                if (_toolGroupToggleTip == null) _toolGroupToggleTip = "RimAgent.ToolGroup.ToggleTip".Translate();
                TooltipHandler.TipRegion(lineRect, _toolGroupToggleTip);
            }
            if (Widgets.ButtonInvisible(lineRect)) clickedGroup = line.GroupFirst;
        }
        Widgets.Label(lineRect, line.Text);
    }

    private static void DrawTableLine(Rect lineRect, TranscriptLine line, bool mouseOnJump)
    {
        if (!mouseOnJump) CopyToolHeaderOnRightClick(lineRect, line.PlainText);
        line.Table.Draw(lineRect);
    }

    private void DrawToolHeaderLine(Rect lineRect, TranscriptLine line, bool mouseOnJump, ref TranscriptEntry clickedTool)
    {
        if (!mouseOnJump)
        {
            CopyToolHeaderOnRightClick(lineRect, line.PlainText);
            if (Mouse.IsOver(lineRect))
            {
                Widgets.DrawHighlight(lineRect);
                if (_toolToggleTip == null) _toolToggleTip = "RimAgent.Tool.ToggleTip".Translate();
                TooltipHandler.TipRegion(lineRect, _toolToggleTip);
            }
            if (Widgets.ButtonInvisible(lineRect)) clickedTool = line.Entry;
        }
        Widgets.Label(lineRect, line.Text);
    }

    private static void DrawDefaultLine(Rect lineRect, TranscriptLine line)
    {
        DrawSelectableLine(lineRect, line.Text, line.PlainText);
    }

    private void DrawJumpButton(Rect jumpRect)
    {
        bool unseen = _linesTranscriptVersion != _seenVersion;
        string jumpLabel = _jumpLabel.Get(unseen, u => (u ? "RimAgent.Scroll.NewMessages" : "RimAgent.Scroll.Latest").Translate().ToString());
        Color jumpColor = unseen ? NoticeColor : TitleColor;
        if (DrawUnifiedButton(jumpRect, jumpLabel, jumpColor, jumpColor, true, GameFont.Tiny)) _scrollToBottom = true;
    }

    /// <summary>寬度是否已經停在同一個值超過 <see cref="ReflowDelayMs"/>（用 TickCount：BCL 在測試與遊戲內都能 JIT）。</summary>
    private bool WidthSettled(float width)
    {
        int now = Environment.TickCount;
        if (Mathf.Abs(width - _settlingWidth) > 0.001f)
        {
            _settlingWidth = width;
            _settlingSince = now;
            return false;
        }
        return unchecked(now - _settlingSince) >= ReflowDelayMs;
    }

    private static GUIStyle _bubbleStyle;

    /// <summary>以九宮格貼圖保留圓角，避免介面材質不支援 GUI.DrawTexture 的圓角參數。</summary>
    private static GUIStyle GetBubbleStyle()
    {
        if (_bubbleStyle != null) return _bubbleStyle;
        int size = BubbleRadius * 2 + 1;
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
            float dx = Mathf.Max(0f, Mathf.Abs(x - BubbleRadius) - 0.5f);
            float dy = Mathf.Max(0f, Mathf.Abs(y - BubbleRadius) - 0.5f);
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float coverage = Mathf.Clamp01(BubbleRadius + 0.5f - distance);
            float border = Mathf.Clamp01(distance - BubbleRadius + 1.5f);
            Color color = Color.Lerp(BubbleColor, BubbleBorderColor, border);
            color.a *= coverage;
            pixels[y * size + x] = color;
        }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        _bubbleStyle = new GUIStyle { border = new RectOffset(BubbleRadius, BubbleRadius, BubbleRadius, BubbleRadius) };
        _bubbleStyle.normal.background = texture;
        return _bubbleStyle;
    }

    /// <summary>框選層樣式快取（按字體）：平時文字透明、只看到底下的彩色 Label；取得焦點才顯示純文字，複製不到顏色標籤。</summary>
    private static readonly Dictionary<GameFont, GUIStyle> SelectableStyles = [];

    /// <summary>
    /// Labels 不能框選，TextArea 會把顏色標籤一起複製；所以彩色 Label 照常顯示，
    /// 上面再疊一層同字體同排版、文字平時透明的 TextArea 專門給滑鼠拖選＋Ctrl+C。
    /// 底色沿用 Label（透明），捲動、行高、換行都與量測時一致。
    /// Unity 原生呼叫集中在這一個方法（RuntimeCompatibilityTests 逐一登記這類方法）。
    /// </summary>
    private static void DrawSelectableLine(Rect rect, string richText, string plainText)
    {
        Widgets.Label(rect, richText);
        // 唯讀框選層不需要文字游標：焦點在它身上時 Unity 會畫一條閃爍豎線（如氣泡尾端的游標），
        // 把全域游標色暫時設透明即可隱藏；選取反白不受影響，畫完立刻還原，後面畫的輸入框游標正常。
        Color oldCursor = GUI.skin.settings.cursorColor;
        GUI.skin.settings.cursorColor = Color.clear;
        GUI.TextArea(rect, plainText ?? string.Empty, GetSelectableStyle());
        GUI.skin.settings.cursorColor = oldCursor;
    }

    private static GUIStyle GetSelectableStyle()
    {
        GameFont font = Text.Font;
        if (!SelectableStyles.TryGetValue(font, out GUIStyle style) || style == null)
        {
            style = new GUIStyle(Text.CurFontStyle)
            {
                richText = false,
                wordWrap = true
            };
            Color plain = new Color(0.90f, 0.93f, 0.96f);
            // GUIStyleState 是參考型別，收進陣列迴圈賦值仍會寫回樣式本體。
            foreach (GUIStyleState state in new[] { style.normal, style.hover, style.onNormal, style.onHover })
            {
                state.background = null;
                state.textColor = Color.clear;
            }
            foreach (GUIStyleState state in new[] { style.active, style.focused, style.onActive, style.onFocused })
            {
                state.background = null;
                state.textColor = plain;
            }
            SelectableStyles[font] = style;
        }
        return style;
    }

    /// <summary>
    /// 工具摘要列沒有框選層（左鍵要留給展開／收合）：右鍵直接複製該列純文字。
    /// Unity 原生呼叫集中在這一個方法（RuntimeCompatibilityTests 逐一登記這類方法）。
    /// </summary>
    private static void CopyToolHeaderOnRightClick(Rect rect, string plainText)
    {
        if (Event.current.type != EventType.MouseDown || Event.current.button != 1) return;
        if (!Mouse.IsOver(rect)) return;
        GUIUtility.systemCopyBuffer = plainText ?? string.Empty;
        Event.current.Use();
    }
}
