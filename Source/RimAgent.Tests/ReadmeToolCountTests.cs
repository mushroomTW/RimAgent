using System.IO;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class ReadmeToolCountTests
    {
        [TestCase("README.md", "Tools")]
        [TestCase("README.zh-TW.md", "項工具")]
        public void ToolCountMatchesRegistry(string fileName, string unit)
        {
            // 正式環境一定帶技能提供者（無技能時 read_skill 回「未安裝」），文件數量以此為準。
            int count = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null, null,
                skills: () => new[] { new AgentSkill { Name = "dummy", Description = "dummy", Body = "dummy" } }).Descriptors.Count;
            string readme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), fileName));

            Assert.That(readme, Does.Contain(count + " " + unit), fileName + " 的工具數應為 " + count);
        }

        private static string FindRepositoryRoot()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "About", "About.xml"))
                    && File.Exists(Path.Combine(dir.FullName, "README.md"))) return dir.FullName;
            }
            throw new FileNotFoundException("找不到 mod 根目錄");
        }
    }
}
