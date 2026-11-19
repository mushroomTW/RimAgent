using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Tools;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimAgent.UI;

#pragma warning disable S101 // RimWorld Window 類別命名慣例
public partial class Window_Agent
{
    /// <summary>執行中提示：依 Agent 此刻實際在做的事顯示；尚未得知時只說執行中。</summary>
    private static string ActivityText(AgentActivity activity)
    {
        if (activity == null) return "RimAgent.Working".Translate().ToString();
        switch (activity.Kind)
        {
            case AgentActivityKind.Tool:
                return "RimAgent.Activity.Tool".Translate(activity.ToolName).ToString();
            case AgentActivityKind.Checking:
                return "RimAgent.Activity.Checking".Translate(activity.ToolName).ToString();
            case AgentActivityKind.Compacting:
                return "RimAgent.Activity.Compacting".Translate().ToString();
            default:
                return "RimAgent.Activity.Thinking".Translate().ToString();
        }
    }

    private static int WorkingPhase()
    {
        // TickCount 會正負迴繞，先取正數再算格子。
        return ((System.Environment.TickCount & int.MaxValue) / 500) % 3;
    }

    /// <summary>執行中動畫：. → .. → ... 循環，每 0.5 秒換一格。</summary>
    private static string WorkingDots(int phase)
    {
        switch (phase)
        {
            case 1: return "..";
            case 2: return "...";
            default: return ".";
        }
    }

    private void RebuildLines(IReadOnlyList<TranscriptEntry> entries, float width)
    {
        _lines.Clear();
        _linesTotal = 0f;
        _groupFirst = null;
        _groupHeaderIndex = -1;
        _combatNoted.Clear();
        PruneToolExpanded(entries);
        AppendLines(entries, 0, width);
    }

    /// <summary>丟掉已經不在逐字稿裡（/clear、裁掉舊紀錄、換存檔）的展開狀態，免得一直抓著舊紀錄。</summary>
    private void PruneToolExpanded(IReadOnlyList<TranscriptEntry> entries)
    {
        if (_toolExpanded.Count == 0) return;
        var live = new HashSet<TranscriptEntry>(entries);
        List<TranscriptEntry> stale = [.. _toolExpanded.Keys.Where(entry => !live.Contains(entry))];
        foreach (TranscriptEntry entry in stale) _toolExpanded.Remove(entry);
        foreach (TranscriptEntry entry in _groupExpanded.Keys.Where(entry => !live.Contains(entry)).ToList()) _groupExpanded.Remove(entry);
    }

    private void AppendLines(IReadOnlyList<TranscriptEntry> entries, int from, float width)
    {
        for (int i = Math.Max(0, from); i < entries.Count; i++)
        {
            TranscriptEntry entry = entries[i];
            // 每句 User 紀錄開啟新回合（插話屬於進行中的回合）；第一行上面不畫分隔線。
            bool turnStart = entry.Kind == TranscriptKind.User && !entry.Interjection && _lines.Count > 0;
            bool collapsed = false;
            if (entry.Kind == TranscriptKind.Tool)
            {
                collapsed = !TrackToolGroup(entry, i > 0 && entries[i - 1].Kind == TranscriptKind.Tool, width);
            }
            else
            {
                _groupFirst = null;
            }
            _linesTotal += BuildEntryLines(entry, width, turnStart, _lines, collapsed);
        }
        _linesEntryCount = entries.Count;
    }

    private string _toolGroupToggleTip;

    /// <summary>
    /// 把這筆工具紀錄記進連續工具群組並更新（或新建）群組摘要列，回傳該組目前是否展開。
    /// 摘要列在組內第一筆工具時加入，之後每多一筆就改寫它的數字。
    /// </summary>
    private bool TrackToolGroup(TranscriptEntry entry, bool continuesPrevious, float width)
    {
        if (!continuesPrevious || _groupFirst == null)
        {
            _groupFirst = entry;
            _groupCount = 0;
            _groupFailed = 0;
            _groupPending = false;
            _groupHeaderIndex = -1;
        }
        _groupCount++;
        if (entry.Failed) _groupFailed++;
        if (entry.Result.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal)) _groupPending = true;

        bool expanded = IsGroupExpanded(_groupFirst);
        string text = GroupHeaderText(_groupCount, _groupFailed, expanded);
        var header = new TranscriptLine
        {
            Kind = LineKind.ToolGroupHeader,
            Text = text,
            PlainText = text.StripTags(),
            GroupFirst = _groupFirst,
            Height = MeasureRichHeight(text, width)
        };
        if (_groupHeaderIndex < 0)
        {
            _groupHeaderIndex = _lines.Count;
            _lines.Add(header);
            _linesTotal += header.Height + EntryGap;
        }
        else
        {
            _linesTotal += header.Height - _lines[_groupHeaderIndex].Height;
            _lines[_groupHeaderIndex] = header;
        }
        return expanded;
    }

    /// <summary>群組預設收合；組內有等待確認的預覽時預設展開（玩家要看得到確認的是什麼）；玩家親手設定過的照玩家的。</summary>
    private bool IsGroupExpanded(TranscriptEntry first)
    {
        if (_groupExpanded.TryGetValue(first, out bool expanded)) return expanded;
        return ReferenceEquals(first, _groupFirst) && _groupPending;
    }

    private static string GroupHeaderText(int count, int failed, bool expanded)
    {
        string arrow = ColorTagPrefix + SubTextHex + ">" + (expanded ? "▾" : "▸") + ColorTagCloseSpace;
        string summary = ColorTagPrefix + TextHex + ">" + "RimAgent.ToolGroup.Summary".Translate(count) + ColorTagClose;
        if (failed > 0)
        {
            summary += " " + ColorTagPrefix + EmergencyHex + ">" + "RimAgent.ToolGroup.Failed".Translate(failed) + ColorTagClose;
        }
        return arrow + summary;
    }

    private static readonly GUIContent MeasureContent = new();

    /// <summary>Text.CalcHeight 會先去掉標籤，量不到放大的標題與變寬的粗體，回覆尾端因此被裁切；這裡連標籤一起量。</summary>
    private static float MeasureRichHeight(string richText, float width)
    {
        MeasureContent.text = richText;
        return Text.CurFontStyle.CalcHeight(MeasureContent, width);
    }

    /// <summary>把一筆紀錄排成若干行加到 <paramref name="into"/>，回傳佔用的總高度（含間距）。</summary>
    private float BuildEntryLines(TranscriptEntry entry, float width, bool turnStart, List<TranscriptLine> into, bool collapsedGroup = false)
    {
        float total = 0f;
        foreach ((string text, LineKind kind) in Render(entry, IsToolExpanded(entry), _combatNoted))
        {
            // 群組收合時只留戰鬥提醒，其餘工具行藏在摘要列後面。
            if (collapsedGroup && kind != LineKind.CombatNote) continue;
            var line = new TranscriptLine { Kind = kind, Text = text, Entry = entry, TurnStart = turnStart };
            line.PlainText = text.StripTags();
            if (kind == LineKind.Table)
            {
                LayoutTable(ref line, width);
            }
            else if (kind == LineKind.PlayerBubble)
            {
                // 短句貼齊字寬，長句到上限就換行。CalcSize 不換行，量到的是最長一行的寬度。
                float maxInner = width * BubbleMaxRatio - BubblePadX * 2f;
                float inner = Mathf.Min(Mathf.Ceil(Text.CalcSize(text).x) + 1f, maxInner);
                line.BubbleWidth = inner + BubblePadX * 2f;
                line.Height = Text.CalcHeight(text, inner) + BubblePadY * 2f;
            }
            else
            {
                line.Height = MeasureRichHeight(text, width);
            }
            into.Add(line);
            total += line.Height + EntryGap + (turnStart ? TurnGap : 0f);
            turnStart = false;
        }
        string screenshotPath = collapsedGroup ? null : TranscriptScreenshot.TryGetPath(entry, GenFilePaths.ScreenshotFolderPath);
        if (screenshotPath != null)
        {
            float imageHeight = Mathf.Clamp(width * 0.5625f, 120f, 320f);
            into.Add(new TranscriptLine { Kind = LineKind.Screenshot, Height = imageHeight, ImagePath = screenshotPath, Entry = entry });
            total += imageHeight + EntryGap;
        }
        return total;
    }

    /// <summary>只重排一筆紀錄的行（展開／收合工具時），不必整份逐字稿重算 CalcHeight 與 markdown。</summary>
    private void RelayoutEntry(TranscriptEntry entry)
    {
        int start = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (ReferenceEquals(_lines[i].Entry, entry)) { start = i; break; }
        }
        if (start < 0) return;

        int end = start;
        float oldHeight = 0f;
        bool hadCombatNote = false;
        while (end < _lines.Count && ReferenceEquals(_lines[end].Entry, entry))
        {
            TranscriptLine old = _lines[end];
            oldHeight += old.Height + EntryGap + (old.TurnStart ? TurnGap : 0f);
            hadCombatNote |= old.Kind == LineKind.CombatNote;
            end++;
        }
        // 這一筆原本就是該句指令顯示戰鬥提醒的那筆：先讓出去，重排時才會再出現。
        if (hadCombatNote) _combatNoted.Remove(entry.InstructionId);

        var fresh = new List<TranscriptLine>();
        float newHeight = BuildEntryLines(entry, _linesWidth, _lines[start].TurnStart, fresh);
        _lines.RemoveRange(start, end - start);
        _lines.InsertRange(start, fresh);
        _linesTotal += newHeight - oldHeight;
    }

    /// <summary>工具紀錄預設收合，只有等待確認的預覽預設展開（玩家要看得到確認的是什麼）；玩家親手設定過的照玩家的。</summary>
    private bool IsToolExpanded(TranscriptEntry entry)
    {
        if (entry.Kind != TranscriptKind.Tool) return false;
        if (_toolExpanded.TryGetValue(entry, out bool expanded)) return expanded;
        return entry.Result.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 雙色分離渲染條目：採用 Unity Rich Text 標籤，達成「高亮彩色前綴標籤 + 柔和淺白內文」的圖片設計層次。
    /// </summary>
    private static IEnumerable<(string Text, LineKind Kind)> Render(TranscriptEntry entry, bool toolExpanded, HashSet<int> combatNoted)
    {
        switch (entry.Kind)
        {
            case TranscriptKind.User:
                if (entry.GoalRun)
                {
                    string goalTag = ColorTagPrefix + NoticeHex + ">" + "RimAgent.Tag.Goal".Translate() + ColorTagCloseSpace;
                    yield return (goalTag + ColorTagPrefix + TextHex + ">" + EscapeRichText(entry.Text) + ColorTagClose, LineKind.Text);
                }
                else if (entry.Autonomous)
                {
                    string autoTag = ColorTagPrefix + AutoActionHex + ">" + "RimAgent.Tag.AutoAction".Translate() + ColorTagCloseSpace;
                    string body = ColorTagPrefix + TextHex + ">" + EscapeRichText(entry.Text) + ColorTagClose;
                    yield return (autoTag + body, LineKind.Text);
                }
                else
                {
                    // 靠右的氣泡本身就標明是玩家說的，不再加標籤。
                    yield return (ColorTagPrefix + TextHex + ">" + EscapeRichText(entry.Text) + ColorTagClose, LineKind.PlayerBubble);
                }
                break;

            case TranscriptKind.Assistant:
                foreach (var line in RenderAssistant(entry)) yield return line;
                break;

            case TranscriptKind.Tool:
                foreach (var line in RenderTool(entry, toolExpanded, combatNoted)) yield return line;
                break;

            case TranscriptKind.Error:
                string errTag = ColorTagPrefix + EmergencyHex + ">" + "RimAgent.Tag.EmergencyStop".Translate() + ColorTagCloseSpace;
                string errBody = ColorTagPrefix + EmergencyHex + ">" + EscapeRichText(DescribeError(entry)) + ColorTagClose;
                yield return (errTag + errBody, LineKind.Text);
                break;

            default:
                // Notice 條目：檢驗是否為暫停、告警或提醒
                bool isPauseOrAlert = ContainsAny(entry.Text, "暫停", "暂停", "pause", "告警", "警告", "warn", "alert");
                string noticeTag = isPauseOrAlert
                    ? ColorTagPrefix + PauseHex + ">" + "RimAgent.Tag.AutoPause".Translate().ToString() + ColorTagCloseSpace
                    : ColorTagPrefix + NoticeHex + ">" + "RimAgent.Tag.Notice".Translate().ToString() + ColorTagCloseSpace;
                string noticeBody = ColorTagPrefix + TextHex + ">" + EscapeRichText(entry.Text) + ColorTagClose;
                yield return (noticeTag + noticeBody, LineKind.Text);
                break;
        }
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < keywords.Length; i++)
        {
            if (text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<(string Text, LineKind Kind)> RenderAssistant(TranscriptEntry entry)
    {
        string agentTag = ColorTagPrefix + AutoActionHex + ">" + "RimAgent.Tag.Agent".Translate() + ColorTagCloseSpace;
        bool tagged = false;
        foreach (RimLLM_Framework.Mod.RimLLMMarkdownSegment segment in RimLLM_Framework.Mod.RimLLMMarkdownTable.Split(ReplyMarkdown.EscapeAngleBrackets(entry.Text)))
        {
            if (segment.IsTable)
            {
                // 表格不能帶前綴標籤；回覆以表格開頭時，標籤自成一行。
                if (!tagged) yield return (agentTag, LineKind.Text);
                tagged = true;
                yield return (segment.Markdown, LineKind.Table);
                continue;
            }
            string reply = RenderReply(segment.Markdown);
            if (reply.Length == 0) continue;
            yield return ((tagged ? string.Empty : agentTag) + ColorTagPrefix + TextHex + ">" + reply + ColorTagClose, LineKind.Text);
            tagged = true;
        }
        if (!tagged)
        {
            yield return (agentTag + ColorTagPrefix + TextHex + ">" + "RimAgent.EmptyReply".Translate() + ColorTagClose, LineKind.Text);
        }
        if (entry.ToolsStripped)
        {
            yield return (ColorTagPrefix + NoticeHex + ">" + "RimAgent.ToolsStripped".Translate() + ColorTagClose, LineKind.Text);
        }
    }

    /// <summary>表格交給框架排版；格子套上內文顏色，純文字另存供右鍵複製。</summary>
    private static void LayoutTable(ref TranscriptLine line, float width)
    {
        var rows = RimLLM_Framework.Mod.RimLLMMarkdownTable.Split(line.Text)[0].Rows;
        line.Table = RimLLM_Framework.Mod.RimLLMMarkdownTable.Layout(rows, width, RenderCell);
        line.Height = line.Table.Height;
        line.PlainText = line.Table.PlainText;
    }

    /// <summary>摘要列（▸／▾、工具名、風險判定；失敗時工具名轉紅）一定顯示，參數與結果只在展開時顯示。</summary>
    private static IEnumerable<(string Text, LineKind Kind)> RenderTool(TranscriptEntry entry, bool expanded, HashSet<int> combatNoted)
    {
        string arrow = ColorTagPrefix + SubTextHex + ">" + (expanded ? "▾" : "▸") + ColorTagCloseSpace;
        string tag = ColorTagPrefix + AutoActionHex + ">" + "RimAgent.Tag.Tool".Translate() + ColorTagCloseSpace;
        // 子代理的工具呼叫標上名稱，玩家才分得出是誰做的。
        if (!string.IsNullOrEmpty(entry.Agent))
        {
            tag += ColorTagPrefix + SubTextHex + ">[" + EscapeRichText(entry.Agent) + "]" + ColorTagCloseSpace;
        }
        string call = arrow + tag + ColorTagPrefix + (entry.Failed ? EmergencyHex : TextHex) + ">" + EscapeRichText(entry.ToolName) + ColorTagClose;
        if (entry.RiskCheck.Length > 0)
        {
            call += " " + ColorTagPrefix + (entry.Failed ? EmergencyHex : NoticeHex) + ">["
                + RiskLabel(entry.RiskCheck) + "]" + ColorTagClose;
        }
        yield return (call, LineKind.ToolHeader);
        if (expanded)
        {
            if (!string.IsNullOrEmpty(entry.Arguments))
            {
                yield return (ColorTagPrefix + SubTextHex + ">" + "RimAgent.Tool.Arguments".Translate()
                    + " " + EscapeRichText(entry.Arguments) + ColorTagClose, LineKind.Text);
            }

            string resultStr = entry.Result.Length > MaxResultChars ? entry.Result[..MaxResultChars] + "…" : entry.Result;
            string resColorHex = entry.Failed ? EmergencyHex : SubTextHex;
            yield return (ColorTagPrefix + resColorHex + ">" + "RimAgent.Tool.Result".Translate()
                + " " + EscapeRichText(resultStr) + ColorTagClose, LineKind.Text);
        }

        if (entry.ToolCategory == ToolCategory.Combat && combatNoted.Add(entry.InstructionId))
        {
            string combatNote = ColorTagPrefix + NoticeHex + ">" + "RimAgent.CombatLatencyNote".Translate() + ColorTagClose;
            yield return (combatNote, LineKind.CombatNote);
        }
    }

    private static string RiskLabel(string risk)
    {
        switch (risk)
        {
            case "low": return "RimAgent.Risk.Low".Translate();
            case "high": return "RimAgent.Risk.High".Translate();
            case "unrequested": return "RimAgent.Risk.Unrequested".Translate();
            case "unavailable": return "RimAgent.Risk.Unavailable".Translate();
            default: return EscapeRichText(risk);
        }
    }

    private static string EscapeRichText(string value)
    {
        return (value ?? string.Empty).Replace('<', '＜').Replace('>', '＞');
    }

    private static string DescribeError(TranscriptEntry entry)
    {
        switch (entry.ErrorKind)
        {
            case AgentErrorKind.Cancelled: return "RimAgent.Error.Cancelled".Translate();
            case AgentErrorKind.Configuration: return "RimAgent.Error.Configuration".Translate(entry.Text);
            case AgentErrorKind.Transient: return "RimAgent.Error.Transient".Translate(entry.Text);
            case AgentErrorKind.ContextTooLong: return "RimAgent.Error.ContextTooLong".Translate();
            default: return "RimAgent.Error.Unknown".Translate(entry.Text);
        }
    }

    private static bool? _markdownAvailable;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string RenderReply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (_markdownAvailable == false) return text;
        try
        {
            // 先把 $...$／$$...$$ 數學轉成易讀純文字：框架的 Markdig 管線沒有數學擴充。
            string rich = RimLLM_Framework.Mod.RimLLMMarkdown.ToRichText(LatexMath.ConvertMath(text));
            _markdownAvailable = true;
            return rich ?? text;
        }
        catch (Exception ex) when (ex is MissingMethodException || ex is TypeLoadException)
        {
            _markdownAvailable = false;
            return text;
        }
        catch (Exception)
        {
            return text;
        }
    }

    /// <summary>表格格子交給框架的格子轉換（保留「射擊 12**」這類熱情星號），再套上內文顏色。</summary>
    private static string RenderCell(string cell)
    {
        string math;
        try
        {
            math = LatexMath.ConvertMath(cell);
        }
        catch (RegexMatchTimeoutException)
        {
            // 病態輸入讓數學轉換逾時：這格就照原文顯示，不讓例外冒到繪製迴圈。
            math = cell;
        }
        return ColorTagPrefix + TextHex + ">" + RimLLM_Framework.Mod.RimLLMMarkdown.CellToRichText(math) + ColorTagClose;
    }

    /// <summary>記住上一次的輸入值與組好的字串；值沒變就直接回傳，不重新翻譯與格式化。</summary>
    private sealed class TextCache<TKey>
    {
        private bool _hasValue;
        private TKey _key;
        private string _text;

        public string Get(TKey key, Func<TKey, string> build)
        {
            if (!_hasValue || !EqualityComparer<TKey>.Default.Equals(_key, key))
            {
                _key = key;
                _text = build(key);
                _hasValue = true;
            }
            return _text;
        }
    }
}
