#nullable disable

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
    private static string ActivityText(AgentActivity activity)
    {
        if (activity == null) return "RimAgent.Working".Translate().ToString();
        return activity.Kind switch
        {
            AgentActivityKind.Tool => "RimAgent.Activity.Tool".Translate(activity.ToolName).ToString(),
            AgentActivityKind.Checking => "RimAgent.Activity.Checking".Translate(activity.ToolName).ToString(),
            AgentActivityKind.Compacting => "RimAgent.Activity.Compacting".Translate().ToString(),
            _ => "RimAgent.Activity.Thinking".Translate().ToString(),
        };
    }

    private static int WorkingPhase()
    {
        // TickCount 會正負迴繞，先取正數再算格子。
        return ((System.Environment.TickCount & int.MaxValue) / 500) % 3;
    }

    private static string WorkingDots(int phase)
    {
        return phase switch
        {
            1 => "..",
            2 => "...",
            _ => ".",
        };
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
                collapsed = !TrackToolGroup(entry, PreviousIsTool(entries, i), width);
            }
            else if (entry.Kind == TranscriptKind.Reasoning && _groupFirst != null)
            {
                // 推理模型每步都有思考：夾在工具之間的思考算進同一個工具群組，群組收合時一起藏起來，
                // 不然每一步都會把「已執行 N 個工具」切成一組。
                collapsed = !IsGroupExpanded(_groupFirst);
            }
            else
            {
                _groupFirst = null;
            }
            _linesTotal += BuildEntryLines(entry, width, turnStart, _lines, collapsed);
        }
        _linesEntryCount = entries.Count;
    }

    internal static bool PreviousIsTool(IReadOnlyList<TranscriptEntry> entries, int index)
    {
        for (int j = index - 1; j >= 0; j--)
        {
            if (entries[j].Kind == TranscriptKind.Reasoning) continue;
            return entries[j].Kind == TranscriptKind.Tool;
        }
        return false;
    }

    private string _toolGroupToggleTip;

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

    private bool IsToolExpanded(TranscriptEntry entry)
    {
        if (entry.Kind == TranscriptKind.Reasoning) return _toolExpanded.TryGetValue(entry, out bool open) && open;
        if (entry.Kind != TranscriptKind.Tool) return false;
        if (_toolExpanded.TryGetValue(entry, out bool expanded)) return expanded;
        return entry.Result.StartsWith(ToolText.ConfirmationPrefix, StringComparison.Ordinal);
    }

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

            case TranscriptKind.Reasoning:
                foreach (var line in RenderReasoning(entry, toolExpanded)) yield return line;
                break;

            case TranscriptKind.Error:
                string errTag = ColorTagPrefix + EmergencyHex + ">" + "RimAgent.Tag.EmergencyStop".Translate() + ColorTagCloseSpace;
                string errBody = ColorTagPrefix + EmergencyHex + ">" + EscapeRichText(DescribeError(entry)) + ColorTagClose;
                yield return (errTag + errBody, LineKind.Text);
                break;

            default:
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

    private static void LayoutTable(ref TranscriptLine line, float width)
    {
        var rows = RimLLM_Framework.Mod.RimLLMMarkdownTable.Split(line.Text)[0].Rows;
        line.Table = RimLLM_Framework.Mod.RimLLMMarkdownTable.Layout(rows, width, RenderCell);
        line.Height = line.Table.Height;
        line.PlainText = line.Table.PlainText;
    }

    private static IEnumerable<(string Text, LineKind Kind)> RenderTool(TranscriptEntry entry, bool expanded, HashSet<int> combatNoted)
    {
        string arrow = ColorTagPrefix + SubTextHex + ">" + (expanded ? "▾" : "▸") + ColorTagCloseSpace;
        string tag = ColorTagPrefix + AutoActionHex + ">" + "RimAgent.Tag.Tool".Translate() + ColorTagCloseSpace;
        // 子代理的工具呼叫標上名稱，玩家才分得出是誰做的。
        if (!string.IsNullOrEmpty(entry.Agent))
        {
            tag += ColorTagPrefix + SubTextHex + ">[" + EscapeRichText(entry.Agent) + "]" + ColorTagCloseSpace;
        }
        string call = arrow + tag + ColorTagPrefix + (entry.Failed ? EmergencyHex : ToolCategoryHex(entry.ToolCategory)) + ">" + EscapeRichText(entry.ToolName) + ColorTagClose;
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

    private static IEnumerable<(string Text, LineKind Kind)> RenderReasoning(TranscriptEntry entry, bool expanded)
    {
        string arrow = ColorTagPrefix + SubTextHex + ">" + (expanded ? "▾" : "▸") + ColorTagCloseSpace;
        string header = arrow + ColorTagPrefix + ReasoningHex + ">" + "RimAgent.Tag.Reasoning".Translate() + ColorTagClose;
        if (!string.IsNullOrEmpty(entry.Agent))
        {
            header += " " + ColorTagPrefix + SubTextHex + ">[" + EscapeRichText(entry.Agent) + "]" + ColorTagClose;
        }
        yield return (header, LineKind.ToolHeader);
        if (expanded)
        {
            yield return (ColorTagPrefix + SubTextHex + ">" + EscapeRichText(entry.Text) + ColorTagClose, LineKind.Text);
        }
    }

    internal static string ToolCategoryHex(ToolCategory category)
    {
        return category switch
        {
            ToolCategory.Observe or ToolCategory.Memory or ToolCategory.Web or ToolCategory.Mcp => "9CC8EE",
            ToolCategory.Work or ToolCategory.Zone or ToolCategory.Production => "8FD694",
            ToolCategory.Build => "E8C170",
            ToolCategory.Combat => "FF9A6B",
            ToolCategory.Medical => "F29BC0",
            ToolCategory.Animal or ToolCategory.Prisoner => "B5D98A",
            ToolCategory.Trade => "D7B8FF",
            ToolCategory.Time or ToolCategory.Save => "C9CFF5",
            ToolCategory.Agents => "73CCFF",
            _ => TextHex,
        };
    }

    private static string RiskLabel(string risk)
    {
        return risk switch
        {
            "low" => (string)"RimAgent.Risk.Low".Translate(),
            "high" => (string)"RimAgent.Risk.High".Translate(),
            "unrequested" => (string)"RimAgent.Risk.Unrequested".Translate(),
            "unavailable" => (string)"RimAgent.Risk.Unavailable".Translate(),
            _ => EscapeRichText(risk),
        };
    }

    private static string EscapeRichText(string value)
    {
        return (value ?? string.Empty).Replace('<', '＜').Replace('>', '＞');
    }

    private static string DescribeError(TranscriptEntry entry)
    {
        return entry.ErrorKind switch
        {
            AgentErrorKind.Cancelled => (string)"RimAgent.Error.Cancelled".Translate(),
            AgentErrorKind.Configuration => (string)"RimAgent.Error.Configuration".Translate(entry.Text),
            AgentErrorKind.Transient => (string)"RimAgent.Error.Transient".Translate(entry.Text),
            AgentErrorKind.ContextTooLong => (string)"RimAgent.Error.ContextTooLong".Translate(),
            _ => (string)"RimAgent.Error.Unknown".Translate(entry.Text),
        };
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
