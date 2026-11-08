using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace RimAgent.UI;

/// <summary>模型回覆送進 Markdown 轉換前的角括號跳脫，以及把頂層表格切出來逐格繪製。</summary>
internal static class ReplyMarkdown
{
    private static readonly Regex NonQuoteMarker = new(
        @"(?<!^[ \t]*(?:>[ \t]*)*)>",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly MarkdownPipeline TablePipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <summary>行首的 &gt; 是引用標記，要留給 Markdown 解析；其餘角括號改成全形，避免被當成 rich text 標籤。</summary>
    public static string EscapeAngleBrackets(string reply)
    {
        return NonQuoteMarker.Replace((reply ?? string.Empty).Replace('<', '＜'), "＞");
    }

    /// <summary>回覆的一段：<see cref="Rows"/> 為 null 時是一般 Markdown 文字，否則是表格的原始儲存格（第一列為表頭）。</summary>
    public readonly struct Segment
    {
        public readonly string Markdown;
        public readonly List<string[]> Rows;

        public Segment(string markdown, List<string[]> rows)
        {
            Markdown = markdown;
            Rows = rows;
        }
    }

    /// <summary>
    /// 只切出最外層的表格：引用或清單裡的表格仍交給框架攤平。
    /// 以 4 空格或 tab 縮排開頭的「表格」在 GFM 是程式碼，同樣留給框架。
    /// </summary>
    public static List<Segment> SplitTables(string markdown)
    {
        var segments = new List<Segment>();
        string source = markdown ?? string.Empty;
        int cursor = 0;
        foreach (Block block in Markdown.Parse(source, TablePipeline))
        {
            if (block is not Table table || StartsIndented(source, table.Span.Start)) continue;
            int start = LineStart(source, table.Span.Start);
            AddText(segments, source.Substring(cursor, start - cursor));
            segments.Add(new Segment(source.Substring(start, table.Span.End + 1 - start), Rows(table, source)));
            cursor = Math.Min(source.Length, table.Span.End + 1);
        }
        AddText(segments, source.Substring(cursor));
        return segments;
    }

    private static void AddText(List<Segment> segments, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) segments.Add(new Segment(text.Trim('\n', '\r'), null));
    }

    private static List<string[]> Rows(Table table, string source)
    {
        var rows = new List<string[]>();
        foreach (Block rowBlock in table)
        {
            if (rowBlock is not TableRow row) continue;
            var cells = new List<string>();
            foreach (Block cellBlock in row)
            {
                // 取原始切片而非 inline 樹；跳脫的直線要自己還原。
                cells.Add(source.Substring(cellBlock.Span.Start, cellBlock.Span.Length).Trim().Replace("\\|", "|"));
            }
            if (cells.Count > 0) rows.Add([.. cells]);
        }
        return rows;
    }

    private static int LineStart(string source, int index)
    {
        int newline = index > 0 ? source.LastIndexOf('\n', index - 1) : -1;
        return newline + 1;
    }

    private static bool StartsIndented(string source, int index)
    {
        if (index < source.Length && source[index] == '\t') return true;
        int spaces = 0;
        for (int k = LineStart(source, index); k < index && source[k] == ' '; k++) spaces++;
        return spaces >= 4;
    }
}
