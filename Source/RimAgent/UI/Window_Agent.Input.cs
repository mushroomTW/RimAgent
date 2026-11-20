#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
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
    private void DrawBottomBar(Rect rect, ColonyAgentSession session)
    {
        if (session.HasPendingConfirmation && !session.IsBusy)
        {
            DrawConfirmBar(rect, session);
            return;
        }

        bool canSend = !session.IsBusy && _capabilityWarning == null;
        var inputRect = new Rect(rect.x, rect.y, rect.width - ButtonWidth - 6f, rect.height);
        var buttonRect = new Rect(rect.xMax - ButtonWidth, rect.y, ButtonWidth, rect.height);

        bool enterPressed = !HandleHistoryKeys() && !HandleSlashKeys(session) && !HandleSkillKeys() &&
                            Event.current.type == EventType.KeyDown &&
                            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

        HandlePasteImage(session);
        DrawInputField(inputRect, session.IsBusy);
        if (DrawSendOrStop(session, buttonRect, canSend, enterPressed) && enterPressed)
        {
            Event.current.Use();
        }
    }

    private bool DrawSendOrStop(ColonyAgentSession session, Rect buttonRect, bool canSend, bool enterPressed)
    {
        bool isSlash = _input.Length > 0 && _input[0] == '/';
        if (session.IsBusy)
        {
            if (DrawUnifiedButton(buttonRect, "RimAgent.Stop".Translate(), EmergencyColor, EmergencyColor, true))
            {
                StopRun();
            }
            // 忙碌時按鈕留給停止；Enter 送出斜線指令或插話（Send 依忙碌狀態分流）。
            if (!enterPressed || _input.Trim().Length == 0) return false;
            Send(session);
            return true;
        }

        bool enabled = canSend || isSlash;
        bool clicked = DrawUnifiedButton(buttonRect, "RimAgent.Send".Translate(), ToolActionColor, ToolActionColor, enabled);
        if (!(clicked || enterPressed) || !enabled) return false;
        Send(session);
        return true;
    }

    private void StopRun()
    {
        CancelRunCts();
        RimAgentGameComponent component = RimAgentGameComponent.Instance;
        component?.StopAutonomousRun();
        if (component?.IsGoalRunning == true) component.PauseGoal();
    }

    private static GUIStyle _bareTextFieldStyle;

    /// <summary>沿用遊戲輸入框的字型與游標，但拿掉方形底圖，改由外面畫的圓角框當背景。</summary>
    private static GUIStyle GetBareTextFieldStyle()
    {
        if (_bareTextFieldStyle != null) return _bareTextFieldStyle;
        GameFont old = Text.Font;
        Text.Font = GameFont.Small;
        _bareTextFieldStyle = new GUIStyle(Text.CurTextFieldStyle);
        Text.Font = old;
        foreach (GUIStyleState state in new[]
                 {
                     _bareTextFieldStyle.normal, _bareTextFieldStyle.hover, _bareTextFieldStyle.focused, _bareTextFieldStyle.active,
                     _bareTextFieldStyle.onNormal, _bareTextFieldStyle.onHover, _bareTextFieldStyle.onFocused, _bareTextFieldStyle.onActive
                 })
        {
            state.background = null;
        }
        return _bareTextFieldStyle;
    }

    /// <summary>
    /// Ctrl+V：目前模型可看圖（見 <see cref="ColonyAgentSession.ImageInputEnabled"/>）且剪貼簿只有圖片時，把圖片附到下一句指令。
    /// 剪貼簿有文字時不攔截，輸入框照常貼上文字。
    /// </summary>
    private void HandlePasteImage(ColonyAgentSession session)
    {
        Event e = Event.current;
        if (e.type != EventType.KeyDown || e.keyCode != KeyCode.V || !(e.control || e.command)) return;
        if (session?.ImageInputEnabled != true) return;
        byte[] jpeg = ClipboardImage.TryRead();
        if (jpeg.Length == 0) return;
        e.Use();
        if (_pastedImages.Count >= MaxPastedImages)
        {
            Notice(session, "RimAgent.ImageLimit".Translate(MaxPastedImages));
            return;
        }
        var thumbnail = new Texture2D(2, 2);
        thumbnail.LoadImage(jpeg);
        _pastedImages.Add(new PastedImage { Jpeg = jpeg, Thumbnail = thumbnail });
    }

    private void DrawPastedImages(Rect rect, float thumbSize)
    {
        for (int i = 0; i < _pastedImages.Count; i++)
        {
            var thumbRect = new Rect(rect.x + i * (thumbSize + 3f), rect.y, thumbSize, thumbSize);
            GUI.DrawTexture(thumbRect, _pastedImages[i].Thumbnail, ScaleMode.ScaleToFit);
            TooltipHandler.TipRegion(thumbRect, "RimAgent.PastedImageTip".Translate());
            if (!Widgets.ButtonInvisible(thumbRect)) continue;
            UnityEngine.Object.Destroy(_pastedImages[i].Thumbnail);
            _pastedImages.RemoveAt(i);
            break;
        }
    }

    /// <summary>取出貼上的圖片並清空縮圖；沒有圖片時回傳空陣列。</summary>
    private List<byte[]> TakePastedImages()
    {
        if (_pastedImages.Count == 0) return [];
        List<byte[]> images = _pastedImages.Select(p => p.Jpeg).ToList();
        foreach (PastedImage image in _pastedImages) UnityEngine.Object.Destroy(image.Thumbnail);
        _pastedImages.Clear();
        return images;
    }

    private void DrawInputField(Rect inputRect, bool busy)
    {
        DrawRoundedBox(inputRect, ControlBgColor, ControlBorderColor);

        GUI.SetNextControlName(InputControlName);
        var innerInputRect = inputRect.ContractedBy(4f, 2f);
        if (_pastedImages.Count > 0)
        {
            // 貼上的圖片縮圖排在輸入框右側，文字區讓出空間。
            float thumbSize = inputRect.height - 6f;
            float thumbsWidth = _pastedImages.Count * (thumbSize + 3f);
            innerInputRect.width -= thumbsWidth;
            DrawPastedImages(new Rect(innerInputRect.xMax + 2f, inputRect.y + 3f, thumbsWidth, thumbSize), thumbSize);
        }
        string before = _input;
        _input = GUI.TextField(innerInputRect, _input ?? string.Empty, GetBareTextFieldStyle());
        // 玩家自己改了內容就不算在瀏覽歷史：↓ 不會拿舊草稿蓋掉新字，打 / 也讓方向鍵回到斜線選單。
        if (_input != before) _historyIndex = -1;

        if (_input.Length == 0 && GUI.GetNameOfFocusedControl() != InputControlName)
        {
            GUI.color = new Color(0.5f, 0.55f, 0.6f);
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;
            string placeholder = (busy ? "RimAgent.PlaceholderBusy" : "RimAgent.Placeholder").Translate();
            Widgets.Label(innerInputRect, placeholder.Truncate(innerInputRect.width));
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            GUI.color = Color.white;
        }
    }

    /// <summary>
    /// 輸入框有焦點時 ↑／↓ 瀏覽送出過的輸入，↓ 走到底還原原本的草稿。
    /// 斜線選單開著時讓給選單；但已經在瀏覽歷史時（叫回的是斜線指令也一樣）繼續瀏覽。
    /// </summary>
    private bool HandleHistoryKeys()
    {
        if (Event.current.type != EventType.KeyDown || InputHistory.Count == 0) return false;
        KeyCode key = Event.current.keyCode;
        if (key != KeyCode.UpArrow && key != KeyCode.DownArrow) return false;
        if (GUI.GetNameOfFocusedControl() != InputControlName) return false;
        if (_historyIndex < 0 && (SlashCommands.Match(_input).Count > 0 || SkillMatches().Count > 0)) return false;

        if (key == KeyCode.UpArrow)
        {
            NavigateHistoryUp();
        }
        else if (_historyIndex >= 0)
        {
            NavigateHistoryDown();
        }
        Event.current.Use();
        return true;
    }

    private void NavigateHistoryUp()
    {
        if (_historyIndex < 0)
        {
            _historyDraft = _input;
            _historyIndex = InputHistory.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        SetInput(InputHistory[_historyIndex]);
    }

    private void NavigateHistoryDown()
    {
        _historyIndex++;
        if (_historyIndex >= InputHistory.Count)
        {
            _historyIndex = -1;
            SetInput(_historyDraft);
        }
        else
        {
            SetInput(InputHistory[_historyIndex]);
        }
    }

    private static void RememberInput(string text)
    {
        if (InputHistory.Count > 0 && InputHistory[InputHistory.Count - 1] == text) return;
        InputHistory.Add(text);
        if (InputHistory.Count > MaxInputHistory) InputHistory.RemoveAt(0);
    }

    private bool HandleSlashKeys(ColonyAgentSession session)
    {
        if (Event.current.type != EventType.KeyDown) return false;
        List<SlashCommand> matches = SlashCommands.Match(_input);
        if (matches.Count == 0) return false;
        _slashIndex = Mathf.Clamp(_slashIndex, 0, matches.Count - 1);
        switch (Event.current.keyCode)
        {
            case KeyCode.UpArrow:
                _slashIndex = (_slashIndex + matches.Count - 1) % matches.Count;
                break;
            case KeyCode.DownArrow:
                _slashIndex = (_slashIndex + 1) % matches.Count;
                break;
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                if (string.Equals(_input, "/" + matches[_slashIndex].Name, StringComparison.OrdinalIgnoreCase)) return false;
                AcceptSlash(session, matches[_slashIndex]);
                break;
            case KeyCode.Tab:
                AcceptSlash(session, matches[_slashIndex]);
                break;
            default:
                return false;
        }
        Event.current.Use();
        return true;
    }

    /// <summary>輸入尾端正在打 $ 時符合的技能名稱。</summary>
    private List<string> SkillMatches()
    {
        IReadOnlyList<AgentSkill> skills = RimAgentGameComponent.Instance?.Skills;
        if (skills == null || skills.Count == 0) return [];
        return SlashCommands.MatchSkills(_input, skills.Where(s => s != null).Select(s => s.Name));
    }

    private bool HandleSkillKeys()
    {
        if (Event.current.type != EventType.KeyDown) return false;
        List<string> matches = SkillMatches();
        if (matches.Count == 0) return false;
        _skillIndex = Mathf.Clamp(_skillIndex, 0, matches.Count - 1);
        switch (Event.current.keyCode)
        {
            case KeyCode.UpArrow:
                _skillIndex = (_skillIndex + matches.Count - 1) % matches.Count;
                break;
            case KeyCode.DownArrow:
                _skillIndex = (_skillIndex + 1) % matches.Count;
                break;
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
            case KeyCode.Tab:
                AcceptSkill(matches[_skillIndex]);
                break;
            default:
                return false;
        }
        Event.current.Use();
        return true;
    }

    private void AcceptSkill(string name)
    {
        SlashCommands.TryGetSkillToken(_input, out _, out int start);
        SetInput(_input[..start] + "$" + name + " ");
    }

    private void DrawSkillPopup(Rect bottom)
    {
        List<string> matches = SkillMatches();
        if (matches.Count == 0) return;
        _skillIndex = Mathf.Clamp(_skillIndex, 0, matches.Count - 1);

        const int maxRows = 8;
        int shown = Mathf.Min(matches.Count, maxRows);
        float height = shown * SlashRowHeight + 8f;
        var popup = new Rect(bottom.x, bottom.y - height - 4f, Mathf.Min(SlashPopupWidth, bottom.width), height);
        DrawRoundedBox(popup, BgColor, BorderColor, WindowRadius);

        IReadOnlyList<AgentSkill> skills = RimAgentGameComponent.Instance?.Skills ?? [];
        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        // 選中項超出可見列數時，視窗跟著捲動。
        int first = Mathf.Clamp(_skillIndex - maxRows + 1, 0, matches.Count - shown);
        for (int i = 0; i < shown; i++)
        {
            string name = matches[first + i];
            var row = new Rect(popup.x + 4f, popup.y + 4f + i * SlashRowHeight, popup.width - 8f, SlashRowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            if (first + i == _skillIndex) Widgets.DrawHighlightSelected(row);

            string label = "$" + name;
            float nameWidth = Text.CalcSize(label).x;
            GUI.color = ToolActionColor;
            Widgets.Label(new Rect(row.x + 6f, row.y, nameWidth, row.height), label);

            AgentSkill skill = skills.FirstOrDefault(s => s != null && string.Equals((s.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase));
            var descRect = new Rect(row.x + 6f + nameWidth + 10f, row.y, row.width - nameWidth - 22f, row.height);
            GUI.color = ToolTextColor;
            Widgets.Label(descRect, (skill?.Description ?? string.Empty).Truncate(descRect.width));
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(row)) AcceptSkill(name);
        }
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;
    }

    private void AcceptSlash(ColonyAgentSession session, SlashCommand command)
    {
        if (command.TakesArgs)
        {
            SetInput("/" + command.Name + " ");
            return;
        }
        _input = string.Empty;
        RunSlash(session, command, string.Empty);
    }

    private void SetInput(string text)
    {
        _input = text;
        if (GUI.GetNameOfFocusedControl() != InputControlName) return;
        var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
        editor.text = text;
        editor.MoveTextEnd();
    }

    private void DrawSlashPopup(Rect bottom, ColonyAgentSession session)
    {
        List<SlashCommand> matches = SlashCommands.Match(_input);
        if (matches.Count == 0) return;
        _slashIndex = Mathf.Clamp(_slashIndex, 0, matches.Count - 1);

        float height = matches.Count * SlashRowHeight + 8f;
        var popup = new Rect(bottom.x, bottom.y - height - 4f, Mathf.Min(SlashPopupWidth, bottom.width), height);

        DrawRoundedBox(popup, BgColor, BorderColor, WindowRadius);

        Text.Anchor = TextAnchor.MiddleLeft;
        Text.WordWrap = false;
        for (int i = 0; i < matches.Count; i++)
        {
            SlashCommand command = matches[i];
            var row = new Rect(popup.x + 4f, popup.y + 4f + i * SlashRowHeight, popup.width - 8f, SlashRowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            if (i == _slashIndex) Widgets.DrawHighlightSelected(row);

            string name = "/" + command.Name;
            float nameWidth = Text.CalcSize(name).x;
            GUI.color = ToolActionColor;
            Widgets.Label(new Rect(row.x + 6f, row.y, nameWidth, row.height), name);

            var descRect = new Rect(row.x + 6f + nameWidth + 10f, row.y, row.width - nameWidth - 22f, row.height);
            GUI.color = ToolTextColor;
            Widgets.Label(descRect, command.DescriptionKey.Translate().ToString().Truncate(descRect.width));
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(row)) AcceptSlash(session, command);
        }
        Text.WordWrap = true;
        Text.Anchor = TextAnchor.UpperLeft;
    }
}
