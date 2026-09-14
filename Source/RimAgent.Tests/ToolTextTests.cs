using NUnit.Framework;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class ToolTextTests
    {
        [TestCase("**done**", "done")]
        [TestCase("Set __Ali__ free", "Set Ali free")]
        [TestCase("*done*", "done")]
        [TestCase("_done_", "done")]
        [TestCase("Ali *受傷* 了", "Ali 受傷 了")]
        [TestCase("use _this_ now", "use this now")]
        [TestCase("moved to `(12,34)`", "moved to (12,34)")]
        [TestCase("~~old~~ new", "old new")]
        [TestCase("[Electricity](ResearchProjectDef)", "Electricity")]
        [TestCase("## Summary\n**Ali**: ok", "Summary\nAli: ok")]
        public void StripMarkdownRemovesPairedMarkers(string input, string expected)
        {
            Assert.That(ToolText.StripMarkdown(input), Is.EqualTo(expected));
        }

        [TestCase("Plant_TreeOak")]
        [TestCase("shooting 8**")]
        [TestCase("3 * 4 = 12")]
        [TestCase("2*3*4")]
        [TestCase("a_b")]
        [TestCase("Plant_TreeOak and Gun_AutoPistol")]
        public void StripMarkdownKeepsUnpairedMarkers(string input)
        {
            Assert.That(ToolText.StripMarkdown(input), Is.EqualTo(input));
        }

        [Test]
        public void StripMarkdownHandlesNullAndEmpty()
        {
            Assert.That(ToolText.StripMarkdown(null), Is.EqualTo(string.Empty));
            Assert.That(ToolText.StripMarkdown(string.Empty), Is.EqualTo(string.Empty));
        }
    }
}
