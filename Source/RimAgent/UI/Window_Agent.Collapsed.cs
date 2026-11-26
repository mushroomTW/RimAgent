#nullable disable

using System.Collections.Generic;
using System.IO;
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

public partial class Window_Agent
{
    private void Collapse(ColonyAgentSession session)
    {
        RimAgentMod.Settings?.UpdateBounds(windowRect);
        Find.WindowStack.WindowOfType<ContextPanel>()?.Close(doCloseSound: false);
        _collapsed = true;
        resizeable = false;
        _iconPressed = false;
        _collapsedVersion = session.Transcript.Version;
        _bubbleState = -1;
        _bubbleText = null;
        _bubbleMeasuredText = null;
        _bubbleWidth = 0f;
        _bubbleOnLeft = true;
        // 頭像放在視窗右上角；展開時反過來以頭像位置推回視窗，兩者一起移動。
        windowRect = new Rect(Mathf.Round(windowRect.xMax - AvatarSize - 8f), Mathf.Round(windowRect.y), AvatarSize, AvatarSize);
        _avatarPos = windowRect.position;
        _laidOutPos = windowRect.position;
        UpdateCollapsedIcon();
    }

    private void Expand()
    {
        _collapsed = false;
        resizeable = true;
        _iconPressed = false;
        Vector2 size = InitialSize;
        float x = _avatarPos.x + AvatarSize + 8f - size.x;
        float y = _avatarPos.y;
        windowRect = new Rect(
            Mathf.Clamp(x, 0f, Mathf.Max(0f, Verse.UI.screenWidth - size.x)),
            Mathf.Clamp(y, 0f, Mathf.Max(0f, Verse.UI.screenHeight - size.y)),
            size.x, size.y);
        RimAgentMod.Settings?.UpdateBounds(windowRect);
    }

    private void UpdateCollapsedIcon()
    {
        // 頭像位置只跟著拖曳的位移走；不從視窗寬度反推，否則每幀 Rounded() 的誤差會累積成漂移。
        Vector2 dragged = windowRect.position - _laidOutPos;
        if (dragged != Vector2.zero) _avatarPos += dragged;
        float avatarX = Mathf.Round(Mathf.Clamp(_avatarPos.x, 0f, Verse.UI.screenWidth - AvatarSize));
        float avatarY = Mathf.Round(Mathf.Clamp(_avatarPos.y, 0f, Verse.UI.screenHeight - AvatarSize));
        _avatarPos = new Vector2(avatarX, avatarY);

        UpdateBubbleText();
        float extra = _bubbleText != null && _bubbleWidth > 0f ? Mathf.Ceil(_bubbleWidth) + AvatarBubbleGap : 0f;
        _bubbleOnLeft = avatarX >= extra;
        windowRect = _bubbleOnLeft
            ? new Rect(avatarX - extra, avatarY, extra + AvatarSize, AvatarSize)
            : new Rect(avatarX, avatarY, AvatarSize + extra, AvatarSize);
        _laidOutPos = windowRect.position;

        if (!_iconPressed || !Input.GetMouseButtonUp(0)) return;
        _iconPressed = false;
        if (Vector2.Distance(_avatarPos, _iconPressPos) <= IconClickSlack) Expand();
    }

    /// <summary>泡泡只在有話要說時出現：等待確認 > 執行中的動作 > 有新訊息。</summary>
    private void UpdateBubbleText()
    {
        ColonyAgentSession session = RimAgentGameComponent.Instance?.Session;
        if (session == null)
        {
            _bubbleText = null;
            return;
        }
        AgentActivity activity = session.Activity.Current;
        int state;
        if (session.HasPendingConfirmation) state = 1;
        else if (session.IsBusy) state = 2;
        else if (session.Transcript.Version != _collapsedVersion) state = 3;
        else state = 0;
        if (state == _bubbleState && (state != 2 || activity == _bubbleActivity)) return;
        _bubbleState = state;
        _bubbleActivity = activity;
        _bubbleText = state switch
        {
            1 => "RimAgent.Window.AvatarPending".Translate().ToString(),
            2 => ActivityText(activity),
            3 => "RimAgent.Window.AvatarUnread".Translate().ToString(),
            _ => null
        };
    }

    private static Texture2D DiscTex
    {
        get
        {
            if (_discTex != null) return _discTex;
            const int size = 128;
            const float radius = size / 2f;
            _discTex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - radius;
                    float dy = y + 0.5f - radius;
                    float coverage = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, coverage);
                }
            }
            _discTex.SetPixels(pixels);
            _discTex.Apply();
            return _discTex;
        }
    }

    private void DrawCollapsedIcon(Rect inRect)
    {
        ColonyAgentSession session = RimAgentGameComponent.Instance?.Session;
        bool busy = session?.IsBusy == true;
        bool pending = session?.HasPendingConfirmation == true;
        bool unread = session != null && session.Transcript.Version != _collapsedVersion;
        float time = Time.realtimeSinceStartup;
        float pulse = Mathf.PingPong(time * 2f, 1f);

        float bob = Mathf.Sin(time * (busy ? 5f : 2f)) * 2.5f;
        var avatarSlot = new Rect(_bubbleOnLeft ? inRect.xMax - AvatarSize : inRect.x, inRect.y, AvatarSize, AvatarSize);
        // 內縮要容得下外光暈 4px 加浮動 2.5px，否則上下會被視窗邊界截掉。
        Rect avatar = new Rect(avatarSlot.x, avatarSlot.y + bob, AvatarSize, AvatarSize).ContractedBy(7f);
        bool hover = Mouse.IsOver(avatarSlot);
        Color ring = ResolveRingColor(pending, busy, pulse);

        DrawRingGlow(avatar, ring, busy, pending, hover, pulse);
        GUI.color = ring;
        GUI.DrawTexture(avatar, DiscTex);
        GUI.color = new Color(0.06f, 0.09f, 0.12f, 0.96f);
        GUI.DrawTexture(avatar.ContractedBy(2.5f), DiscTex);
        GUI.color = hover ? Color.white : new Color(1f, 1f, 1f, 0.88f);
        GUI.DrawTexture(avatar.ContractedBy(8f), IconTex);
        DrawUnreadDot(avatar, unread);
        GUI.color = Color.white;
        TooltipHandler.TipRegion(avatarSlot, "RimAgent.Window.ExpandTip".Translate());

        // 泡泡寬度只能在 OnGUI 量；量好留給 WindowUpdate 排版（GUI.Window 會覆蓋這裡改的 windowRect）。
        Text.Font = GameFont.Small;
        UpdateBubbleWidth();
        DrawBubble(inRect);

        // 不吃掉事件：交給視窗本身的 DragWindow 處理拖曳，放開時在 WindowUpdate 判斷是不是點擊。
        if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && Mouse.IsOver(inRect))
        {
            _iconPressed = true;
            _iconPressPos = _avatarPos;
        }
    }

    private static Color ResolveRingColor(bool pending, bool busy, float pulse)
    {
        if (pending) return PauseColor;
        if (busy) return Color.Lerp(BorderColor, ToolActionColor, pulse);
        return BorderColor;
    }

    private static void DrawRingGlow(Rect avatar, Color ring, bool busy, bool pending, bool hover, float pulse)
    {
        if (busy || pending || hover)
        {
            GUI.color = new Color(ring.r, ring.g, ring.b, busy || pending ? 0.18f + 0.22f * pulse : 0.22f);
            GUI.DrawTexture(avatar.ExpandedBy(4f), DiscTex);
        }
    }

    private static void DrawUnreadDot(Rect avatar, bool unread)
    {
        if (!unread) return;
        var dot = new Rect(avatar.xMax - 15f, avatar.y + 1f, 13f, 13f);
        GUI.color = new Color(0.06f, 0.09f, 0.12f, 1f);
        GUI.DrawTexture(dot.ExpandedBy(1.5f), DiscTex);
        GUI.color = NoticeColor;
        GUI.DrawTexture(dot, DiscTex);
    }

    private void DrawBubble(Rect inRect)
    {
        if (_bubbleText == null || inRect.width <= AvatarSize + AvatarBubbleGap) return;
        var bubble = new Rect(
            _bubbleOnLeft ? inRect.x : inRect.x + AvatarSize + AvatarBubbleGap,
            inRect.y + (AvatarSize - AvatarBubbleHeight) / 2f,
            inRect.width - AvatarSize - AvatarBubbleGap, AvatarBubbleHeight);
        if (Event.current.type == EventType.Repaint) GetBubbleStyle().Draw(bubble, isHover: false, isActive: false, on: false, hasKeyboardFocus: false);
        Text.Anchor = TextAnchor.MiddleLeft;
        GUI.color = new Color(0.90f, 0.93f, 0.96f);
        Rect labelRect = bubble.ContractedBy(12f, 0f);
        Widgets.Label(labelRect, _bubbleText.Truncate(labelRect.width));
        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
    }

    private void UpdateBubbleWidth()
    {
        if (_bubbleText == _bubbleMeasuredText) return;
        _bubbleMeasuredText = _bubbleText;
        _bubbleWidth = _bubbleText == null ? 0f : Mathf.Min(Text.CalcSize(_bubbleText).x + 24f, AvatarBubbleMaxWidth);
    }
}
