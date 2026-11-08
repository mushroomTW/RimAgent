using NUnit.Framework;
using RimAgent.UI;
using RimLLM_Framework.Mod;

namespace RimAgent.Tests;

[TestFixture]
public class ReplyMarkdownTests
{
    private static string Render(string reply)
    {
        return RimLLMMarkdown.ToRichText(ReplyMarkdown.EscapeAngleBrackets(reply));
    }

    [Test]
    public void BlockQuoteStillRendersAsQuote()
    {
        Assert.That(Render("> 引用 **粗**"), Is.EqualTo("<color=#9aa0a6>| 引用 <b>粗</b></color>"));
    }

    [Test]
    public void IndentedAndNestedQuoteMarkersAreKept()
    {
        Assert.That(ReplyMarkdown.EscapeAngleBrackets("  > 甲\n> > 乙"), Is.EqualTo("  > 甲\n> > 乙"));
    }

    [Test]
    public void AngleBracketsElsewhereAreEscaped()
    {
        Assert.That(
            ReplyMarkdown.EscapeAngleBrackets("HP > 10，List<int>\n> a->b"),
            Is.EqualTo("HP ＞ 10，List＜int＞\n> a-＞b"));
    }

    [Test]
    public void ModelCannotInjectRichTextTags()
    {
        Assert.That(Render("<b>粗</b> <color=red>紅</color>"), Does.Not.Contain("<b>").And.Not.Contain("<color"));
    }

    [Test]
    public void NullBecomesEmpty()
    {
        Assert.That(ReplyMarkdown.EscapeAngleBrackets(null), Is.EqualTo(string.Empty));
    }

    [Test]
    public void TopLevelTableIsSplitIntoRawCells()
    {
        var segments = ReplyMarkdown.SplitTables("前言\n\n| 殖民者 | 工作 |\n|---|---|\n| **Cole** | 建造、射擊 12** |\n| Toon | a \\| b |\n\n結語");

        Assert.That(segments, Has.Count.EqualTo(3));
        Assert.That(segments[0].Rows, Is.Null);
        Assert.That(segments[0].Markdown, Is.EqualTo("前言"));
        Assert.That(segments[1].Rows, Has.Count.EqualTo(3));
        Assert.That(segments[1].Rows[0], Is.EqualTo(new[] { "殖民者", "工作" }));
        Assert.That(segments[1].Rows[1], Is.EqualTo(new[] { "**Cole**", "建造、射擊 12**" }));
        Assert.That(segments[1].Rows[2], Is.EqualTo(new[] { "Toon", "a | b" }));
        Assert.That(segments[2].Markdown, Is.EqualTo("結語"));
    }

    [Test]
    public void SplitTableMarkdownParsesBackToSameRows()
    {
        var table = ReplyMarkdown.SplitTables("a\n| x | y |\n|---|---|\n| 1 | 2 |")[1];
        Assert.That(ReplyMarkdown.SplitTables(table.Markdown)[0].Rows, Is.EqualTo(table.Rows));
    }

    [Test]
    public void FitColumnsKeepsNarrowColumnsAndShrinksWideOnes()
    {
        Assert.That(Window_Agent.FitColumns([50f, 100f], 300f), Is.EqualTo(new[] { 50f, 100f }));
        Assert.That(Window_Agent.FitColumns([120f, 2000f], 600f), Is.EqualTo(new[] { 120f, 480f }));
        Assert.That(Window_Agent.FitColumns([60f, 900f, 900f], 600f), Is.EqualTo(new[] { 60f, 270f, 270f }));
    }

    [Test]
    public void TablesInsideCodeQuotesOrIndentStayText()
    {
        foreach (string reply in new[]
        {
            "```\n| x | y |\n|---|---|\n| 1 | 2 |\n```",
            "> | x | y |\n> |---|---|\n> | 1 | 2 |",
            "    | x | y |\n    |---|---|\n    | 1 | 2 |",
            "純文字，沒有表格"
        })
        {
            var segments = ReplyMarkdown.SplitTables(reply);
            Assert.That(segments, Has.Count.EqualTo(1), reply);
            Assert.That(segments[0].Rows, Is.Null, reply);
        }
    }
}
