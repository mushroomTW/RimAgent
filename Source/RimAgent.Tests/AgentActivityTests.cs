using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>執行中提示反映 Agent 實際在做的事：思考、執行工具、分類器檢查、壓縮；結束後不殘留。</summary>
    [TestFixture]
    public class AgentActivityTests
    {
        private FakeGameWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        }

        [Test]
        public void InterleavedActivitiesEndIndependently()
        {
            var tracker = new AgentActivityTracker();

            System.IDisposable a = tracker.Begin(AgentActivityKind.Tool, "a");
            System.IDisposable b = tracker.Begin(AgentActivityKind.Tool, "b");
            a.Dispose();
            Assert.That(tracker.Current.ToolName, Is.EqualTo("b"));
            b.Dispose();

            Assert.That(tracker.Current, Is.Null, "並行的活動交錯結束後不殘留");
        }

        [Test]
        public async Task ToolActivityIsVisibleWhileItRunsAndEndsWithIt()
        {
            var tracker = new AgentActivityTracker();
            AgentActivity during = null;
            AIFunction inner = AIFunctionFactory.Create(() => { during = tracker.Current; return "ok"; }, "probe");
            var tool = new LoggingAIFunction(inner, new AgentTranscript(), _world, ToolCategory.Observe, ToolRisk.Query, null, activity: tracker);

            await tool.InvokeAsync(new AIFunctionArguments());

            Assert.That(during?.Kind, Is.EqualTo(AgentActivityKind.Tool));
            Assert.That(during?.ToolName, Is.EqualTo("probe"));
            Assert.That(tracker.Current, Is.Null, "沒有後續模型呼叫也不留著工具名稱");
        }

        [Test]
        public async Task ActivityIsThinkingWhileTheModelRunsAndClearedAfterTheRun()
        {
            ColonyAgentSession session = null;
            AgentActivity first = null;
            var client = new ScriptedChatClient()
                .CallTool("get_colony_overview", null).Do(() => first = session.Activity.Current)
                .Reply("Done.");
            session = new ColonyAgentSession(client, _world, () => ToolPermissions.All, 8, () => AgentMode.Autonomous);

            await session.SendAsync("overview");

            Assert.That(first?.Kind, Is.EqualTo(AgentActivityKind.Thinking));
            Assert.That(session.Activity.Current, Is.Null);
        }

        [Test]
        public async Task ClassifierCheckShowsCheckingWithTheToolName()
        {
            ColonyAgentSession session = null;
            AgentActivity checking = null;
            var agent = new ScriptedChatClient()
                .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
                .Reply("Done.");
            var classifier = new ScriptedChatClient()
                .Reply("{\"risk\": \"low\", \"authorized\": true, \"reason\": \"ok\"}").Do(() => checking = session.Activity.Current);
            session = new ColonyAgentSession(agent, _world, () => ToolPermissions.All, 8, () => AgentMode.Auto, null, null, classifier,
                classifierPrompt: () => "test classifier prompt");

            await session.SendAsync("make Ali a doctor");

            Assert.That(checking?.Kind, Is.EqualTo(AgentActivityKind.Checking));
            Assert.That(checking?.ToolName, Is.EqualTo("set_work_priorities"));
        }

        [Test]
        public async Task CompactShowsCompacting()
        {
            ColonyAgentSession session = null;
            AgentActivity compacting = null;
            var client = new ScriptedChatClient();
            // 歷史要夠長，策略才會真的呼叫摘要模型。
            for (int i = 0; i < 6; i++) client.Reply("ok " + i);
            client.Reply("Summary.").Do(() => compacting = session.Activity.Current);
            session = new ColonyAgentSession(client, _world, () => ToolPermissions.All, 8, () => AgentMode.Autonomous);

            for (int i = 0; i < 6; i++) await session.SendAsync("instruction " + i);
            await session.CompactAsync();

            Assert.That(compacting?.Kind, Is.EqualTo(AgentActivityKind.Compacting));
            Assert.That(session.Activity.Current, Is.Null);
        }
    }
}
