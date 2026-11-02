using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests;

[TestFixture]
public class AgentPackTests
{
    private string _root;
    private string _prompts;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "RimAgentPack_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _prompts = Path.Combine(_root, "Prompts");
        Directory.CreateDirectory(_prompts);
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
        File.WriteAllText(Path.Combine(_prompts, "Briefing.md"), "  You are RimAgent.  ");
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "  Play tall, not wide.  ");
        File.WriteAllText(Path.Combine(_prompts, "Conduct.md"), "  Be brief.  ");
        File.WriteAllText(Path.Combine(_prompts, "AskMode.md"), "  Ask note.  ");
        File.WriteAllText(Path.Combine(_prompts, "RiskClassifier.md"), "  Judge risk.  ");
        File.WriteAllText(Path.Combine(_prompts, "DecisionRisk.md"), "  Judge decision risk.  ");
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
        Assert.That(pack.DecisionRiskCriteria, Is.EqualTo("Judge decision risk."));
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
    }

    [Test]
    public void ParseSectionsKeepsIntroAsPreambleAndSkipsComments()
    {
        string intro;
        IDictionary<string, string> sections = AgentPackLoader.ParseSections(
            "<!-- file note -->\nShared rule.\n## Raid\nHostiles.\n\n<!-- note -->\nStay safe.\n## Raid\nOverride.\n## GoalsHeader\nGoals:",
            out intro);

        Assert.That(sections["Raid"], Is.EqualTo("Override."), "同名重複以後者為準");
        Assert.That(sections["GoalsHeader"], Is.EqualTo("Goals:"));
        Assert.That(sections, Has.Count.EqualTo(2), "引言不成章節");
        Assert.That(intro, Is.EqualTo("Shared rule."), "註解不進提示詞");
    }

    [Test]
    public void ParseSectionsSkipsMultiLineComments()
    {
        string intro;
        IDictionary<string, string> sections = AgentPackLoader.ParseSections(
            "# Title\n<!-- first line\n   second line\n   ## NotASection -->\nShared rule.\n## Raid\n<!-- a\nb -->\nHostiles.",
            out intro);

        Assert.That(intro, Is.EqualTo("# Title\nShared rule."), "跨行註解的後續行也不進提示詞");
        Assert.That(sections, Has.Count.EqualTo(1), "註解裡的 ## 不算章節");
        Assert.That(sections["Raid"], Is.EqualTo("Hostiles."));
    }

    [Test]
    public void UnterminatedCommentOnlyDropsItsOwnLine()
    {
        string intro;
        IDictionary<string, string> sections = AgentPackLoader.ParseSections(
            "Shared rule.\n<!-- forgot to close\n## Raid\nHostiles.",
            out intro);

        Assert.That(intro, Is.EqualTo("Shared rule."));
        Assert.That(sections["Raid"], Is.EqualTo("Hostiles."), "後面的章節不能因為沒收尾的註解而消失");
    }

    /// <summary>
    /// 共用通則放在第一個章節之前，而 Build 必須把它送出去。
    /// 少了這段，寫在檔頭的規則會被靜靜丟掉，模型永遠看不到。
    /// </summary>
    [Test]
    public void SharedPreambleReachesTheModel()
    {
        File.WriteAllText(Path.Combine(_prompts, "Autonomous.en.md"),
            "# Autonomous tasks\n\nSHARED_RULE\n\n## Interval\n\nINTERVAL_BODY\n");

        AgentPack pack = AgentPackLoader.Load(_root);
        string text = AutonomousPrompts.Build(pack.Autonomous, AutonomousTrigger.Interval);

        Assert.That(pack.Autonomous.Preamble, Does.Contain("SHARED_RULE"));
        Assert.That(text, Does.Contain("SHARED_RULE"), "共用通則要跟著每個任務送出去");
        Assert.That(text, Does.Contain("INTERVAL_BODY"));
    }

    [Test]
    public void MissingPreambleStillYieldsTheTaskBody()
    {
        File.WriteAllText(Path.Combine(_prompts, "Autonomous.en.md"), "## Interval\n\nOnly the body.\n");

        AgentPack pack = AgentPackLoader.Load(_root);

        Assert.That(pack.Autonomous.Preamble, Is.Empty);
        Assert.That(AutonomousPrompts.Build(pack.Autonomous, AutonomousTrigger.Interval), Is.EqualTo("Only the body."));
    }

    [Test]
    public void LoadsAutonomousInEnglishOnly()
    {
        File.WriteAllText(Path.Combine(_prompts, "Autonomous.en.md"), "## Raid\nHostiles.");

        AgentPack pack = AgentPackLoader.Load(_root);

        Assert.That(pack.Autonomous.Tasks["Raid"], Is.EqualTo("Hostiles."));
    }

    /// <summary>
    /// 出貨的 md 一個不能少、不能空：搬遷後唯一的提示詞真相來源就是這些檔案。
    /// </summary>
    [Test]
    public void ShippedPromptFilesAreComplete()
    {
        string root = FindAgentPack();
        foreach (string file in new[] { "Briefing.md", "Conduct.md", "AskMode.md", "RiskClassifier.md", "Autonomous.en.md" })
        {
            Assert.That(File.Exists(Path.Combine(root, "Prompts", file)), Is.True, file + " 未放在 Prompts/");
            Assert.That(File.Exists(Path.Combine(root, file)), Is.False, file + " 不應留在 AgentPack/ 根目錄");
        }
        AgentPack pack = AgentPackLoader.Load(root);

        Assert.That(pack.Preamble, Does.Contain("You are RimAgent"));
        Assert.That(pack.PlayerNotes, Is.Empty, "AGENTS.md 留給玩家自己設計，出貨必須是空的");
        Assert.That(pack.Conduct, Does.Contain("For player instructions"));
        Assert.That(pack.AskModeNote, Does.Contain("Ask mode"));
        Assert.That(pack.ClassifierPrompt, Does.Contain("\"risk\""));
        // 從 enum 推導，不寫死清單：新增觸發卻忘了寫章節時，這裡會直接紅掉。
        // （少了章節不會壞掉，只會靜靜退回 Interval —— 正是最難發現的那種 bug。）
        foreach (AutonomousTrigger trigger in (AutonomousTrigger[])Enum.GetValues(typeof(AutonomousTrigger)))
        {
            if (trigger == AutonomousTrigger.None) continue;
            string name = trigger.ToString();
            Assert.That(pack.Autonomous.Tasks.ContainsKey(name), Is.True, name + " 缺章節");
            Assert.That(pack.Autonomous.Tasks[name], Is.Not.Empty, name + " 空章節");
        }
        // 共用任務說明不能空，模型需要知道這是由它自主處理的事件。
        Assert.That(pack.Autonomous.Preamble, Is.Not.Empty, "Autonomous.en.md 缺少共用通則（第一個 ## 之前）");
        Assert.That(AutonomousPrompts.Build(pack.Autonomous, AutonomousTrigger.Interval),
            Does.StartWith(pack.Autonomous.Preamble.Trim()), "共用通則要真的接在任務前面");
    }

    /// <summary>出貨技能超過上限會被靜靜截斷或擠出索引，模型讀到的就不是寫的那份。</summary>
    [Test]
    public void ShippedSkillsFitLimits()
    {
        AgentPack pack = AgentPackLoader.Load(FindAgentPack());

        Assert.That(pack.Skills, Is.Not.Empty);
        foreach (AgentSkill skill in pack.Skills)
        {
            Assert.That(skill.Description, Is.Not.Empty, skill.Name + " 缺描述");
            Assert.That(skill.Body, Does.Not.Contain("[skill truncated]"), skill.Name + " 超過單一技能上限");
        }
        string text = AgentPromptComposer.ComposeWithSkillIndex("CORE", null, null, pack.Skills);
        Assert.That(text, Does.Not.Contain("omitted: over budget"), "技能索引超過總量上限");
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
    public void ComposeWithSkillIndexOrdersLayersAndSkipsEmpties()
    {
        var skills = new[]
        {
            new AgentSkill { Name = "raid-defense", Description = "fight", Body = "Draft all." }
        };

        string text = AgentPromptComposer.ComposeWithSkillIndex("CORE", "Tall, not wide.", "Be brief.", skills);

        Assert.That(text, Does.StartWith("CORE"));
        Assert.That(text.IndexOf("Tall, not wide.", StringComparison.Ordinal), Is.LessThan(text.IndexOf("Be brief.", StringComparison.Ordinal)));
        Assert.That(text.IndexOf("Be brief.", StringComparison.Ordinal), Is.LessThan(text.IndexOf("raid-defense", StringComparison.Ordinal)));
        Assert.That(text, Does.Contain("raid-defense"));
        Assert.That(text, Does.Contain("fight"));
        Assert.That(text, Does.Not.Contain("Draft all."));
    }

    [Test]
    public void ComposeWithSkillIndexPutsExpansionsRightAfterPreamble()
    {
        string text = AgentPromptComposer.ComposeWithSkillIndex("CORE", "Tall, not wide.", null, null, "Game expansions active: Biotech.");

        Assert.That(text, Does.StartWith("CORE\n\nGame expansions active: Biotech."));
        Assert.That(text.IndexOf("Biotech", StringComparison.Ordinal), Is.LessThan(text.IndexOf("Tall, not wide.", StringComparison.Ordinal)));
    }

    [Test]
    public void FormatExpansionsSplitsActiveAndInactiveAndSkipsCore()
    {
        string line = AgentPromptComposer.FormatExpansions(new[]
        {
            ("Core", true, true),
            ("Royalty", false, true),
            ("Anomaly", false, false),
            ("Biotech", false, true)
        });

        Assert.That(line, Is.EqualTo("Game expansions active: Royalty, Biotech. Not active: Anomaly; their content does not exist in this game."));
    }

    [Test]
    public void FormatExpansionsWithNoneActiveSaysBaseGameAndSkipsEmptyInactive()
    {
        Assert.That(AgentPromptComposer.FormatExpansions(new[] { ("Core", true, true), ("Biotech", false, false) }),
            Does.StartWith("Game expansions active: none (base game only)."));
        Assert.That(AgentPromptComposer.FormatExpansions(new[] { ("Biotech", false, true) }), Is.EqualTo("Game expansions active: Biotech."));
    }

    [Test]
    public void ComposeWithSkillIndexWithAllEmptyLayersReturnsCoreOnly()
    {
        string text = AgentPromptComposer.ComposeWithSkillIndex("CORE", "  ", null, null);

        Assert.That(text, Is.EqualTo("CORE"));
    }

    /// <summary>沿測試執行目錄往上找到出貨的 AgentPack。</summary>
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
