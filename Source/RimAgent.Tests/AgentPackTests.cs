using System;
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
            File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "  Be brief.  ");
            string dir = Path.Combine(_root, "Skills", "raid-defense");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: raid-defense\ndescription: fight raids\n---\n# Drill\nDraft all.");

            AgentPack pack = AgentPackLoader.Load(_root);

            Assert.That(pack.Conduct, Is.EqualTo("Be brief."));
            AgentSkill skill = pack.Skills.Single();
            Assert.That(skill.Name, Is.EqualTo("raid-defense"));
            Assert.That(skill.Description, Is.EqualTo("fight raids"));
            Assert.That(skill.Body, Does.Contain("Draft all."));
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

            string text = AgentPromptComposer.Compose("CORE", "Be brief.", skills);

            Assert.That(text, Does.StartWith("CORE"));
            Assert.That(text.IndexOf("Be brief.", StringComparison.Ordinal), Is.LessThan(text.IndexOf("raid-defense", StringComparison.Ordinal)));
            Assert.That(text, Does.Contain("Draft all."));
        }

        [Test]
        public void ComposeWithAllEmptyLayersReturnsCoreOnly()
        {
            string text = AgentPromptComposer.Compose("CORE", "  ", null);

            Assert.That(text, Is.EqualTo("CORE"));
        }
    }
}
