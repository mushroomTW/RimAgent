using NUnit.Framework;
using RimAgent.UI;

namespace RimAgent.Tests
{
    [TestFixture]
    public class LatexMathTests
    {
        [Test]
        public void InlineMathWithCommandBecomesPlainText()
        {
            Assert.That(
                LatexMath.ConvertMath("平均情況為 $O(n \\log n)$，最差為 $O(n^2)$。"),
                Is.EqualTo("平均情況為 O(n log n)，最差為 O(n²)。"));
        }

        [Test]
        public void DisplayMathBecomesPlainText()
        {
            Assert.That(
                LatexMath.ConvertMath("$$\nE = mc^2\n$$"),
                Is.EqualTo("\nE = mc²\n"));
        }

        [Test]
        public void FracSqrtAndGreekConvert()
        {
            Assert.That(LatexMath.ConvertMath("$\\frac{a}{b}$"), Is.EqualTo("(a)/(b)"));
            Assert.That(LatexMath.ConvertMath("$\\sqrt{x} + \\alpha$"), Is.EqualTo("√(x) + α"));
            Assert.That(LatexMath.ConvertMath("$x_{i} + y_{2}$"), Is.EqualTo("x_i + y₂"));
            Assert.That(LatexMath.ConvertMath("$a \\times b \\leq c$"), Is.EqualTo("a × b ≤ c"));
        }

        [Test]
        public void UnknownCommandKeepsNameWithoutBackslash()
        {
            Assert.That(LatexMath.ConvertMath("$\\foo + 1$"), Is.EqualTo("foo + 1"));
        }

        [Test]
        public void CodeRegionsAreUntouched()
        {
            Assert.That(
                LatexMath.ConvertMath("```\n$x^2$\n```"),
                Is.EqualTo("```\n$x^2$\n```"));
            Assert.That(
                LatexMath.ConvertMath("呼叫 `$x$` 即可"),
                Is.EqualTo("呼叫 `$x$` 即可"));
        }

        [Test]
        public void LoneOrUnmatchedDollarIsUntouched()
        {
            Assert.That(LatexMath.ConvertMath("只要 $100 就好"), Is.EqualTo("只要 $100 就好"));
            Assert.That(LatexMath.ConvertMath("$100 和 $200"), Is.EqualTo("$100 和 $200"));
            Assert.That(LatexMath.ConvertMath("沒有錢號的文字"), Is.EqualTo("沒有錢號的文字"));
        }

        [Test]
        public void NullOrEmptyPassesThrough()
        {
            Assert.That(LatexMath.ConvertMath(null), Is.Null);
            Assert.That(LatexMath.ConvertMath(string.Empty), Is.EqualTo(string.Empty));
        }

        /// <summary>
        /// 顯示／複製一致性：Assistant 行的顯示文字即 ToRichText(ConvertMath(...))，
        /// PlainText 只是再去顏色標籤；顯示側沒有 $ 殘留，複製側就不可能有。
        /// </summary>
        [Test]
        public void RenderedRichTextContainsNoRawMath()
        {
            string rendered = RimLLM_Framework.Mod.RimLLMMarkdown.ToRichText(
                LatexMath.ConvertMath("平均情況為 $O(n \\log n)$，最差為 $O(n^2)$。"));
            Assert.That(rendered, Does.Contain("O(n log n)"));
            Assert.That(rendered, Does.Contain("O(n²)"));
            Assert.That(rendered, Does.Not.Contain("$"));
            Assert.That(rendered, Does.Not.Contain("\\log"));
        }
    }
}
