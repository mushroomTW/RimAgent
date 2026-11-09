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
}
