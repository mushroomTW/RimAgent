using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Tests
{
    /// <summary>模型呼叫優化：分類器句內快取、查詢結果快取、分任務 reasoning、自主與玩家共用同一模型、技能按需讀取。</summary>
    [TestFixture]
    public class ModelCallOptimizationTests
    {
        private FakeGameWorld _world;
        private ScriptedChatClient _client;
        private ToolPermissions _permissions;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
            _client = new ScriptedChatClient();
            _permissions = ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work);
        }

        [Test]
        public async Task SameQuestionTwiceCallsClassifierModelOnce()
        {
            var classifierClient = new ScriptedChatClient().Reply("{\"risk\":\"low\",\"authorized\":true}");
            var classifier = new RiskClassifier(classifierClient, systemPrompt: () => "prompt");
            var question = new RiskQuestion
            {
                Instruction = "set Ali's doctor to 1",
                ToolName = "set_work_priorities",
                Arguments = "assignments=Ali:doctor=1",
                Preview = "would set Ali's doctor priority 3 -> 1."
            };

            RiskVerdict first = await classifier.ClassifyAsync(question, CancellationToken.None);
            RiskVerdict second = await classifier.ClassifyAsync(question, CancellationToken.None);

            Assert.That(first.ShouldReject, Is.False);
            Assert.That(second.ShouldReject, Is.False);
            Assert.That(classifierClient.CallCount, Is.EqualTo(1), "同樣的問題只燒一次模型");
        }

        [Test]
        public async Task FailedVerdictsAreNotCached()
        {
            var classifierClient = new ScriptedChatClient()
                .Throw(new TimeoutException("down"))
                .Reply("{\"risk\":\"low\",\"authorized\":true}");
            var classifier = new RiskClassifier(classifierClient, systemPrompt: () => "prompt");
            var question = new RiskQuestion { Instruction = "x", ToolName = "t", Arguments = "a", Preview = "p" };

            RiskVerdict first = await classifier.ClassifyAsync(question, CancellationToken.None);
            RiskVerdict second = await classifier.ClassifyAsync(question, CancellationToken.None);

            Assert.That(first.Failed, Is.True);
            Assert.That(second.Failed, Is.False);
            Assert.That(classifierClient.CallCount, Is.EqualTo(2), "失敗不進快取，下一次重問模型");
        }

        [Test]
        public async Task RetriedCommandInOneInstructionClassifiesOnce()
        {
            var agentClient = new ScriptedChatClient();
            var classifierClient = new ScriptedChatClient();
            agentClient.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
                       .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
                       .Reply("Done.");
            classifierClient.Reply("{\"risk\": \"low\", \"authorized\": true, \"reason\": \"safe\"}");
            var session = new ColonyAgentSession(agentClient, _world, () => _permissions, 8,
                () => AgentMode.Auto, null, null, classifierClient, classifierPrompt: () => "prompt");

            await session.SendAsync("set Ali's doctor to 1");

            Assert.That(classifierClient.CallCount, Is.EqualTo(1), "同一句重試相同命令不重問分類器");
            Assert.That(_world.Calls.Count, Is.EqualTo(2), "命令本身照樣執行兩次");
        }

        [Test]
        public async Task ClassifierCacheDoesNotCrossInstructions()
        {
            var agentClient = new ScriptedChatClient();
            var classifierClient = new ScriptedChatClient();
            agentClient.CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("one")
                       .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" }).Reply("two");
            classifierClient.Reply("{\"risk\": \"low\", \"authorized\": true}")
                            .Reply("{\"risk\": \"low\", \"authorized\": true}");
            var session = new ColonyAgentSession(agentClient, _world, () => _permissions, 8,
                () => AgentMode.Auto, null, null, classifierClient, classifierPrompt: () => "prompt");

            await session.SendAsync("set Ali's doctor to 1");
            await session.SendAsync("set Ali's doctor to 1");

            Assert.That(classifierClient.CallCount, Is.EqualTo(2), "換一句指令就重問");
        }

        [Test]
        public async Task RepeatedQueryHitsWorldOnceButLogsTwice()
        {
            _client.CallTool("list_colonists", new { })
                   .CallTool("list_colonists", new { })
                   .Reply("there they are.");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);

            await session.SendAsync("who is here? who is here?");

            Assert.That(_world.ColonistQueryCount, Is.EqualTo(1), "同樣的查詢只碰一次世界");
            IReadOnlyList<TranscriptEntry> tools = session.Transcript.Snapshot()
                .Where(e => e.Kind == TranscriptKind.Tool).ToList();
            Assert.That(tools.Count, Is.EqualTo(2), "行動紀錄照記兩次");
            Assert.That(tools[0].Result, Is.EqualTo(tools[1].Result));
            Assert.That(tools.All(t => !t.Failed), Is.True);
        }

        [Test]
        public async Task FailedQueryIsNotCached()
        {
            _client.CallTool("get_colonist_wellbeing", new { colonist = "Nobody" })
                   .CallTool("get_colonist_wellbeing", new { colonist = "Nobody" })
                   .Reply("unknown.");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);

            await session.SendAsync("how is Nobody? how is Nobody?");

            IReadOnlyList<TranscriptEntry> tools = session.Transcript.Snapshot()
                .Where(e => e.Kind == TranscriptKind.Tool).ToList();
            Assert.That(tools.All(t => t.Failed), Is.True, "錯誤不進快取，第二次是真的又查了一次");
        }

        [Test]
        public async Task BatchedQueriesShareOneFollowUpRoundTrip()
        {
            _client.CallTools(("list_colonists", new { }), ("get_colony_overview", new { }))
                   .Reply("overview done.");
            ColonyAgentSession session = new ColonyAgentSession(_client, _world, () => _permissions, 8);

            string reply = await session.SendAsync("status report");

            Assert.That(reply, Is.EqualTo("overview done."));
            Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool), Is.EqualTo(2));
            Assert.That(_client.CallCount, Is.EqualTo(2), "一批查詢只要一次跟進呼叫");
        }

        [Test]
        public void ReasoningOverrideAppliesToOneRunOnly()
        {
            var session = new ColonyAgentSession(_client, _world, () => _permissions, 8);
            session.Reasoning = ReasoningLevel.High;

            RimLLMChatOptions overridden = session.BuildRunOptions(ReasoningLevel.Low);
            RimLLMChatOptions preset = session.BuildRunOptions();

            Assert.That(overridden.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.Low));
            Assert.That(preset.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.High));
            Assert.That(session.Reasoning, Is.EqualTo(ReasoningLevel.High), "覆寫只影響當次，不改會話設定");
        }

        [Test]
        public void SkillIndexListsNamesWithoutBodies()
        {
            var skills = new[]
            {
                new AgentSkill { Name = "raid-defense", Description = "fight", Body = "Draft everyone and hold the line." }
            };

            string text = AgentPromptComposer.ComposeWithSkillIndex("CORE", null, null, skills);

            Assert.That(text, Does.Contain("raid-defense").And.Contain("fight").And.Contain("read_skill"));
            Assert.That(text, Does.Not.Contain("Draft everyone"));
        }

        [Test]
        public void RegistryOffersReadSkillOnlyWhenSkillsProvided()
        {
            var withSkills = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null, null,
                skills: () => new[] { new AgentSkill { Name = "s", Description = "d", Body = "b" } });
            var withoutSkills = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());

            Assert.That(withSkills.Descriptors.Any(d => d.Name == "read_skill"), Is.True);
            Assert.That(withoutSkills.Descriptors.Any(d => d.Name == "read_skill"), Is.False);
        }

        [Test]
        public async Task ReadSkillReturnsBodyAndRejectsUnknown()
        {
            var skills = new List<AgentSkill>
            {
                new AgentSkill { Name = "Raid-Defense", Description = "fight", Body = "Draft everyone." }
            };
            _client.CallTool("read_skill", new { skill = "raid-defense" })
                   .CallTool("read_skill", new { skill = "nope" })
                   .Reply("noted.");
            var session = new ColonyAgentSession(_client, _world, () => _permissions, 8,
                skillsProvider: () => skills);

            await session.SendAsync("check skills");

            IReadOnlyList<TranscriptEntry> tools = session.Transcript.Snapshot()
                .Where(e => e.Kind == TranscriptKind.Tool).ToList();
            Assert.That(tools[0].Result, Does.Contain("Draft everyone."));
            Assert.That(tools[0].Failed, Is.False);
            Assert.That(tools[1].Failed, Is.True);
            Assert.That(tools[1].Result, Does.Contain("Raid-Defense"));
        }

        [Test]
        public void EmptySkillListStillOffersReadSkill()
        {
            var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null, null,
                skills: () => new AgentSkill[0]);

            Assert.That(registry.Descriptors.Any(d => d.Name == "read_skill"), Is.True,
                "正式環境永遠帶技能提供者，工具數恆為 60（含零技能時）");
        }

        [Test]
        public async Task BlacklistedCommandSkipsClassifierInAuto()
        {
            _permissions.Set(ToolCategory.Trade, true);
            _world.Traders.Add(new TraderInfo
            {
                Kind = TraderInfo.OrbitalKind, Id = "PassingShip_1", Name = "Starweaver", Faction = "Outlanders", Silver = 1200,
                Goods = new[] { new TradeGoodsInfo { DefName = "ComponentSpacer", Label = "spacer component", Count = 8, MarketValue = 55 } }
            });
            var agentClient = new ScriptedChatClient();
            var classifierClient = new ScriptedChatClient();
            agentClient.CallTool("execute_trade", new { trader = "PassingShip_1", buys = "spacer component x4", sells = "", negotiator = "Ali" })
                       .Reply("rejected.");
            var session = new ColonyAgentSession(agentClient, _world, () => _permissions, 8,
                () => AgentMode.Auto, null, null, classifierClient, classifierPrompt: () => "prompt");

            await session.SendAsync("buy 4 spacer components");

            Assert.That(classifierClient.CallCount, Is.EqualTo(0), "黑名單不燒分類器模型");
            Assert.That(_world.Calls, Is.Empty);
            Assert.That(session.HasPendingConfirmation, Is.False);
            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.RiskCheck, Is.EqualTo("high"));
            Assert.That(tool.Result, Does.StartWith(ToolText.ErrorPrefix).And.Contain("blacklist"));
        }

        [Test]
        public async Task QueryCacheInvalidatedAfterCommand()
        {
            _client.CallTool("list_colonists", new { })
                   .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
                   .CallTool("list_colonists", new { })
                   .Reply("done.");
            var session = new ColonyAgentSession(_client, _world, () => _permissions, 8,
                () => AgentMode.Autonomous);

            await session.SendAsync("report then set then report");

            Assert.That(_world.ColonistQueryCount, Is.EqualTo(3),
                "命令執行後快取作廢：兩次查詢各碰一次世界，加上命令解析的一次");
            Assert.That(session.Transcript.Snapshot()
                .Count(e => e.Kind == TranscriptKind.Tool && e.ToolName == "list_colonists"), Is.EqualTo(2));
        }

        /// <summary>自主任務不另選模型：與玩家指令走同一個 Agent 模型。</summary>
        [Test]
        public async Task AutonomousRunUsesAgentModelLikePlayerRun()
        {
            _client.Reply("巡檢完成。").Reply("巡檢完成。");
            var session = new ColonyAgentSession(_client, _world, () => _permissions, 8)
            {
                PreferredModel = "flagship:model"
            };

            await session.SendAsync("例行巡檢");
            await session.SendAsync("例行巡檢", autonomous: true);

            Assert.That(_client.ReceivedOptions, Has.Count.EqualTo(2));
            Assert.That(_client.ReceivedOptions[0].ModelId, Is.EqualTo("flagship:model"));
            Assert.That(_client.ReceivedOptions[1].ModelId, Is.EqualTo("flagship:model"),
                "自主旗標不換模型");
        }
    }
}
