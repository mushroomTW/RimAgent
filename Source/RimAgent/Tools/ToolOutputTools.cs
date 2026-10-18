using System;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace RimAgent.Tools;

/// <summary>
/// 取回被截斷的工具結果：依位置分段讀，或搜尋含某段文字的行。
/// 每段比結果上限小，讀回來的內容本身不會再被截斷成新的代號。
/// </summary>
internal sealed class ToolOutputTools
{
    public const string ToolName = "read_tool_output";

    /// <summary>一次讀回的字元數；留空間給標頭與可能附加的遊戲錯誤。</summary>
    public const int SliceChars = 6000;

    private readonly ToolOutputStore _store;

    public ToolOutputTools(ToolOutputStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    [AgentTool(ToolName, ToolCategory.Observe, ToolRisk.Query)]
    [Description("Reads a capped tool result saved under an id such as out_3: a slice from a character offset, or with find, only the lines containing that text.")]
    public string ReadToolOutput(
        [Description("Id from the omission note, e.g. \"out_3\".")] string id,
        [Description("Character offset to start from; the note gives where the omitted part begins.")] int offset,
        [Description("Optional text; returns matching lines with their offsets instead of a slice.")] string find)
    {
        if (!_store.TryGet(id, out string text))
        {
            return ToolText.Error("no saved output \"" + id + "\". Saved outputs last only for this conversation and only the latest " +
                ToolOutputStore.Capacity.ToString(CultureInfo.InvariantCulture) + " are kept; run the original tool again.");
        }
        return string.IsNullOrEmpty(find) ? Slice(id, text, offset) : Find(id, text, find);
    }

    private static string Slice(string id, string text, int offset)
    {
        int start = ToolOutputLimit.AlignToCharBoundary(text, Math.Max(0, Math.Min(offset, text.Length)));
        int end = ToolOutputLimit.AlignToCharBoundary(text, Math.Min(text.Length, start + SliceChars));
        string header = id + " characters " + start.ToString(CultureInfo.InvariantCulture) + "-" +
            end.ToString(CultureInfo.InvariantCulture) + " of " + text.Length.ToString(CultureInfo.InvariantCulture) + ":\n";
        string footer = end < text.Length
            ? "\n[continue with offset " + end.ToString(CultureInfo.InvariantCulture) + "]"
            : "\n[end of output]";
        return header + text.Substring(start, end - start) + footer;
    }

    private static string Find(string id, string text, string find)
    {
        var sb = new StringBuilder();
        int matches = 0, shown = 0, lineStart = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            string line = text.Substring(lineStart, lineEnd - lineStart).TrimEnd(['\r']);
            if (line.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matches++;
                string entry = "@" + lineStart.ToString(CultureInfo.InvariantCulture) + ": " + line + "\n";
                if (sb.Length + entry.Length <= SliceChars)
                {
                    sb.Append(entry);
                    shown++;
                }
            }
            lineStart = lineEnd + 1;
        }
        if (matches == 0) return "No lines in " + id + " contain \"" + find + "\".";
        string header = matches.ToString(CultureInfo.InvariantCulture) + " line(s) in " + id + " contain \"" + find + "\"" +
            (shown < matches ? "; showing the first " + shown.ToString(CultureInfo.InvariantCulture) : string.Empty) + ":\n";
        return header + sb.ToString().TrimEnd(['\n']);
    }
}
