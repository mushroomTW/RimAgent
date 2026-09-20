using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>
    /// README 的工具表由登錄表生成，改了工具沒更新 README 這裡會擋下，並把正確的區塊印在失敗訊息裡供貼上。
    /// </summary>
    [TestFixture]
    public class ReadmeToolTableTests
    {
        private const string BeginMarker = "<!-- tool-table:begin -->";
        private const string EndMarker = "<!-- tool-table:end -->";

        [Test]
        public void ReadmeToolTableMatchesRegistry()
        {
            string readme = File.ReadAllText(FindReadme());
            string expected = RenderTable(new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript()));

            int begin = readme.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = readme.IndexOf(EndMarker, StringComparison.Ordinal);
            Assert.That(begin, Is.GreaterThanOrEqualTo(0), "README.md 缺少 " + BeginMarker);
            Assert.That(end, Is.GreaterThan(begin), "README.md 缺少 " + EndMarker);

            string actual = readme.Substring(begin + BeginMarker.Length, end - begin - BeginMarker.Length).Trim().Replace("\r\n", "\n");
            Assert.That(actual, Is.EqualTo(expected), "README.md 的工具表與登錄表不一致，請把標記之間換成：\n\n" + expected + "\n");
        }

        [Test]
        public void ReadmeToolCountMatchesRegistry()
        {
            string readme = File.ReadAllText(FindReadme());
            int count = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript()).Descriptors.Count;
            Assert.That(readme, Does.Contain(count + " tools (AIFunctionFactory)"), "README.md 架構圖裡的工具數要是 " + count);
        }

        /// <summary>依類別（enum 順序）再依名稱排，反射的方法順序不必穩定。</summary>
        internal static string RenderTable(AgentToolRegistry registry)
        {
            var sb = new StringBuilder();
            sb.Append("| Tool | Category | Risk |\n| --- | --- | --- |");
            foreach (AgentToolDescriptor tool in registry.Descriptors.OrderBy(t => t.Category).ThenBy(t => t.Name, StringComparer.Ordinal))
            {
                sb.Append("\n| `").Append(tool.Name).Append("` | ").Append(tool.Category).Append(" | ").Append(tool.Risk).Append(" |");
            }
            return sb.ToString();
        }

        private static string FindReadme()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "README.md");
                if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "About", "About.xml"))) return candidate;
            }
            throw new FileNotFoundException("找不到 mod 根目錄的 README.md");
        }
    }
}
