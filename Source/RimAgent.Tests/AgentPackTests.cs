using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests
{
    [TestFixture]
    public class AgentPackTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "RimAgentPack_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (Exception)
            {
            }
        }

        [Test]
        public void MissingFolderLoadsEmptyPack()
        {
            AgentPack pack = AgentPackLoader.Load(Path.Combine(_root, "nope"));

            Assert.That(pack.Conduct, Is.EqualTo(string.Empty));
            Assert.That(pack.Skills, Is.Empty);
        }

        [Test]
        public void LoadsConductAndSkills()
        {
            File.WriteAllText(Path.Combine(_root, "Briefing.md"), "  You are RimAgent.  ");
            File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "  Play tall, not wide.  ");
            File.WriteAllText(Path.Combine(_root, "Conduct.md"), "  Be brief.  ");
            File.WriteAllText(Path.Combine(_root, "AskMode.md"), "  Ask note.  ");
            File.WriteAllText(Path.Combine(_root, "RiskClassifier.md"), "  Judge risk.  ");
            string dir = Path.Combine(_root, "Skills", "raid-defense");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: raid-defense\ndescription: fight raids\n---\n# Drill\nDraft all.");

            AgentPack pack = AgentPackLoader.Load(_root);

            Assert.That(pack.Preamble, Is.EqualTo("You are RimAgent."));
            Assert.That(pack.PlayerNotes, Is.EqualTo("Play tall, not wide."));
            Assert.That(pack.Conduct, Is.EqualTo("Be brief."));
            Assert.That(pack.AskModeNote, Is.EqualTo("Ask note."));
            Assert.That(pack.ClassifierPrompt, Is.EqualTo("Judge risk."));
            AgentSkill skill = pack.Skills.Single();
            Assert.That(skill.Name, Is.EqualTo("raid-defense"));
            Assert.That(skill.Description, Is.EqualTo("fight raids"));
            Assert.That(skill.Body, Does.Contain("Draft all."));
        }

        [Test]
        public void MissingPromptFilesLoadAsEmpty()
        {
            AgentPack pack = AgentPackLoader.Load(_root);

            Assert.That(pack.Preamble, Is.Empty);
            Assert.That(pack.PlayerNotes, Is.Empty);
            Assert.That(pack.Conduct, Is.Empty);
            Assert.That(pack.AskModeNote, Is.Empty);
            Assert.That(pack.ClassifierPrompt, Is.Empty);
            Assert.That(pack.Autonomous.Tasks, Is.Empty);
            Assert.That(pack.Autonomous.GoalsHeader, Is.Empty);
            Assert.That(pack.Autonomous.GoalsFooter, Is.Empty);
        }

        [Test]
        public void ParseSectionsDropsIntroAndKeepsTasks()
        {
            IDictionary<string, string> sections = AgentPackLoader.ParseSections(
                "Intro for humans only.\n## Raid\nHostiles.\n\nStay safe.\n## Raid\nOverride.\n## GoalsHeader\nGoals:");

            Assert.That(sections["Raid"], Is.EqualTo("Override."), "同名重複以後者為準");
            Assert.That(sections["GoalsHeader"], Is.EqualTo("Goals:"));
            Assert.That(sections, Has.Count.EqualTo(2), "引言不成章節");
        }

        [Test]
        public void LoadsAutonomousInEnglishOnly()
        {
            File.WriteAllText(Path.Combine(_root, "Autonomous.en.md"), "## Raid\nHostiles.\n## GoalsHeader\nGoals:\n## GoalsFooter\nSpend steps.");

            AgentPack pack = AgentPackLoader.Load(_root);

            Assert.That(pack.Autonomous.Tasks["Raid"], Is.EqualTo("Hostiles."));
            Assert.That(pack.Autonomous.GoalsHeader, Is.EqualTo("Goals:"));
            Assert.That(pack.Autonomous.GoalsFooter, Is.EqualTo("Spend steps."));
        }

        /// <summary>
        /// 出貨的 md 一個不能少、不能空：搬遷後唯一的提示詞真相來源就是這些檔案。
        /// </summary>
        [Test]
        public void ShippedPromptFilesAreComplete()
        {
            AgentPack pack = AgentPackLoader.Load(FindAgentPack());

            Assert.That(pack.Preamble, Does.Contain("You are RimAgent"));
            Assert.That(pack.PlayerNotes, Is.Empty, "AGENTS.md 留給玩家自己設計，出貨必須是空的");
            Assert.That(pack.Conduct, Does.Contain("Minimal change"));
            Assert.That(pack.AskModeNote, Does.Contain("Ask mode"));
            Assert.That(pack.ClassifierPrompt, Does.Contain("\"risk\""));
            string[] triggers = { "Raid", "RaidEnded", "EmergencyMedical", "Fire", "MentalBreak", "FoodShortage", "Interval" };
            foreach (string trigger in triggers)
            {
                Assert.That(pack.Autonomous.Tasks.ContainsKey(trigger), Is.True, trigger + " 缺章節");
                Assert.That(pack.Autonomous.Tasks[trigger], Is.Not.Empty, trigger + " 空章節");
            }
            Assert.That(pack.Autonomous.GoalsHeader, Is.Not.Empty, "缺 GoalsHeader");
            Assert.That(pack.Autonomous.GoalsFooter, Is.Not.Empty, "缺 GoalsFooter");
        }

        [Test]
        public void SkillWithoutFrontmatterFallsBackToFolderName()
        {
            string dir = Path.Combine(_root, "Skills", "winter-prep");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), "# Stockpile food\n\nStore 30 days.");

            AgentPack pack = AgentPackLoader.Load(_root);

            AgentSkill skill = pack.Skills.Single();
            Assert.That(skill.Name, Is.EqualTo("winter-prep"));
            Assert.That(skill.Description, Does.Contain("Stockpile food"));
        }

        [Test]
        public void LongSkillBodyIsTruncated()
        {
            string dir = Path.Combine(_root, "Skills", "big");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), new string('x', AgentPackLoader.MaxSkillChars + 10));

            AgentPack pack = AgentPackLoader.Load(_root);

            Assert.That(pack.Skills.Single().Body, Does.Contain("[skill truncated]"));
        }

        [Test]
        public void ComposeOrdersLayersAndSkipsEmpties()
        {
            var skills = new[]
            {
                new AgentSkill { Name = "raid-defense", Description = "fight", Body = "Draft all." }
            };

            string text = AgentPromptComposer.Compose("CORE", "Tall, not wide.", "Be brief.", skills);

            Assert.That(text, Does.StartWith("CORE"));
            Assert.That(text.IndexOf("Tall, not wide.", StringComparison.Ordinal), Is.LessThan(text.IndexOf("Be brief.", StringComparison.Ordinal)));
            Assert.That(text.IndexOf("Be brief.", StringComparison.Ordinal), Is.LessThan(text.IndexOf("raid-defense", StringComparison.Ordinal)));
            Assert.That(text, Does.Contain("Draft all."));
        }

        [Test]
        public void ComposeWithAllEmptyLayersReturnsCoreOnly()
        {
            string text = AgentPromptComposer.Compose("CORE", "  ", null, null);

            Assert.That(text, Is.EqualTo("CORE"));
        }

        /// <summary>沿測試執行目錄往上找到出貨的 AgentPack（仿 ReadmeToolTableTests.FindReadme）。</summary>
        private static string FindAgentPack()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "AgentPack", "AGENTS.md");
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            }
            Assert.Fail("找不到 repo 的 AgentPack 資料夾");
            return null;
        }
    }
}
