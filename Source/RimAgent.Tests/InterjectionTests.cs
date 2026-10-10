using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>玩家在 agent 執行中插話：附在下一個工具結果送達；沒趕上的在本輪結束後接著當新指令跑。</summary>
    [TestFixture]
    public class InterjectionTests
    {
        private FakeGameWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
            _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        }

        private ColonyAgentSession NewSession(ScriptedChatClient client)
        {
            return new ColonyAgentSession(client, _world, () => ToolPermissions.All, 8, () => AgentMode.Autonomous);
        }

        [Test]
        public void InterjectionIsRefusedWhenIdle()
        {
            var session = NewSession(new ScriptedChatClient().Reply("ok"));

            Assert.That(session.Interject("hello"), Is.False);
            Assert.That(session.Transcript.Snapshot(), Is.Empty);
        }

        [Test]
        public async Task InterjectionReachesTheModelWithTheNextToolResult()
        {
            ColonyAgentSession session = null;
            bool accepted = false;
            var client = new ScriptedChatClient()
                .CallTool("get_colony_overview", null).Do(() => accepted = session.Interject("Prioritize the raid instead."))
                .Reply("Switched to the raid.");
            session = NewSession(client);

            string reply = await session.SendAsync("plan the next build");

            Assert.That(accepted, Is.True);
            Assert.That(reply, Is.EqualTo("Switched to the raid."));
            Assert.That(client.CallCount, Is.EqualTo(2), "插話已送達，不必另起一句");
            string toolResult = client.ReceivedMessages[1].Last().Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
            Assert.That(toolResult, Does.Contain("Prioritize the raid instead."));
            TranscriptEntry[] users = session.Transcript.Snapshot().Where(e => e.Kind == TranscriptKind.User).ToArray();
            Assert.That(users.Select(u => u.Text), Is.EqualTo(new[] { "plan the next build", "Prioritize the raid instead." }));
            TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
            Assert.That(tool.Result, Does.Not.Contain("Prioritize the raid"), "行動紀錄只顯示工具本身的結果");
            Assert.That(session.Transcript.CurrentInstructionText, Is.EqualTo("plan the next build\nPrioritize the raid instead."),
                "分類器看到原指令加上插話，不是只有插話");
        }

        [Test]
        public async Task LateInterjectionDoesNotDiscardPendingConfirmations()
        {
            ColonyAgentSession session = null;
            var client = new ScriptedChatClient()
                .CallTool("place_blueprint", new { building = "wall", shape = "single", x = 1, z = 1, x2 = 1, z2 = 1, rotation = 0, stuff = "" })
                .Reply("Previewed.").Do(() => session.Interject("use granite"));
            session = new ColonyAgentSession(client, _world, () => ToolPermissions.All, 8, () => AgentMode.Confirm);

            await session.SendAsync("build a wall");

            Assert.That(session.HasPendingConfirmation, Is.True, "待確認的命令保留");
            Assert.That(client.CallCount, Is.EqualTo(2), "不自動送出插話");
            Assert.That(session.Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("waiting for confirmation")), Is.True);
        }

        [Test]
        public async Task LateInterjectionRunsAsTheNextInstructionWithoutChangingTheReply()
        {
            ColonyAgentSession session = null;
            var client = new ScriptedChatClient()
                .Reply("First answer.").Do(() => session.Interject("Also check the food."))
                .Reply("Food is fine.");
            session = NewSession(client);

            string reply = await session.SendAsync("how is the colony?");

            Assert.That(reply, Is.EqualTo("First answer."), "回傳值仍是原指令的回覆");
            Assert.That(client.CallCount, Is.EqualTo(2));
            Assert.That(client.ReceivedMessages[1].Last(m => m.Role == ChatRole.User).Text, Is.EqualTo("Also check the food."));
            Assert.That(session.Transcript.Snapshot().Count(e => e.Kind == TranscriptKind.User && e.Text == "Also check the food."),
                Is.EqualTo(1), "插話只顯示一次");
            Assert.That(session.Transcript.Snapshot().Last(e => e.Kind == TranscriptKind.Assistant).Text, Is.EqualTo("Food is fine."));
            Assert.That(session.IsBusy, Is.False);
            Assert.That(session.Interject("late"), Is.False, "本輪結束後不再收插話");
        }

        [Test]
        public async Task StoppedRunDropsQueuedInterjections()
        {
            ColonyAgentSession session = null;
            var cts = new CancellationTokenSource();
            var client = new ScriptedChatClient()
                .Reply("unused").Do(() =>
                {
                    session.Interject("never mind");
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                });
            session = NewSession(client);

            await session.SendAsync("build a wall", cts.Token);

            Assert.That(client.CallCount, Is.EqualTo(1), "按停止後不會再送出插話");
            Assert.That(session.Transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Notice && e.Text.Contains("not delivered")), Is.True);
        }

        [Test]
        public async Task SubagentToolsDoNotConsumeTheInterjection()
        {
            ColonyAgentSession session = null;
            var subagent = new AgentSubagent { Name = "scout", Description = "looks around", Categories = new[] { ToolCategory.Observe }, ReadOnly = true, Body = "Look." };
            var client = new ScriptedChatClient()
                .CallTool("delegate_task", new { agent = "scout", tasks = new[] { "look" } })
                // 子代理的第一步：先模擬玩家插話，再叫工具。
                .CallTool("get_colony_overview", null).Do(() => session.Interject("Hurry up."))
                .Reply("Scout report.")
                .Reply("Done.");
            session = new ColonyAgentSession(client, _world, () => ToolPermissions.All, 8, () => AgentMode.Autonomous,
                subagentsProvider: () => new[] { subagent });

            await session.SendAsync("send the scout");

            string subagentToolResult = client.ReceivedMessages[2].Last().Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
            Assert.That(subagentToolResult, Does.Not.Contain("Hurry up."));
            Assert.That(client.ReceivedMessages.Last().Last(m => m.Role == ChatRole.User).Text, Is.EqualTo("Hurry up."),
                "主 Agent 在委派後沒有再呼叫工具，插話在本輪結束後接著送出");
        }
    }
}
