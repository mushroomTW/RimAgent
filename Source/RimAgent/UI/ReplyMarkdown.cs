using System;
using System.Text.RegularExpressions;

namespace RimAgent.UI;

/// <summary>模型回覆送進 Markdown 轉換前的角括號跳脫。</summary>
internal static class ReplyMarkdown
{
    private static readonly Regex NonQuoteMarker = new(
        @"(?<!^[ \t]*(?:>[ \t]*)*)>",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>行首的 &gt; 是引用標記，要留給 Markdown 解析；其餘角括號改成全形，避免被當成 rich text 標籤。</summary>
    public static string EscapeAngleBrackets(string reply)
    {
        return NonQuoteMarker.Replace((reply ?? string.Empty).Replace('<', '＜'), "＞");
    }
}
