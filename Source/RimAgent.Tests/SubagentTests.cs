using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;

namespace RimAgent.Tests;

/// <summary>子代理委派（delegate_task）與模型槽（子代理、分類器、/compact 摘要）。</summary>
[TestFixture]
public class SubagentTests
{
    private FakeGameWorld _world;

    private static readonly AgentSubagent Scout = new()
    {
        Name = "scout",
        Description = "look around",
        Categories = new[] { ToolCategory.Observe, ToolCategory.Work },
        ReadOnly = true,
        Body = "SCOUT BODY"
    };

    private static readonly AgentSubagent Steward = new()
    {
        Name = "steward",
        Description = "assign work",
        Categories = new[] { ToolCategory.Observe, ToolCategory.Work },
        ReadOnly = false,
        Reasoning = ReasoningLevel.High,
        Body = "STEWARD BODY"
    };

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
    }

    private ColonyAgentSession NewSession(IChatClient client, ToolPermissions permissions, AgentMode mode,
        IReadOnlyList<AgentSubagent> subagents, ModelRouting routing = null, IChatClient classifierClient = null)
    {
        return new ColonyAgentSession(client, _world, () => permissions, 8, () => mode,
            classifierClient: classifierClient, classifierPrompt: () => "prompt",
            subagentsProvider: () => subagents, routing: routing);
    }

    [Test]
    public void ParseSubagentReadsFrontmatter()
    {
        AgentSubagent parsed = AgentPackLoader.ParseSubagent("folder",
            "---\nname: Builder\ndescription: builds things\ncategories: Build, zone, Agents, Nonsense, 3\nread_only: true\nreasoning: low\n---\nBuild well.");

        Assert.That(parsed.Name, Is.EqualTo("builder"), "名稱轉小寫當鍵");
        Assert.That(parsed.Description, Is.EqualTo("builds things"));
        Assert.That(parsed.Categories, Is.EqualTo([ToolCategory.Build, ToolCategory.Zone]),
            "不分大小寫；Agents、寫錯的名稱與數字都剔除");
        Assert.That(parsed.ReadOnly, Is.True);
        Assert.That(parsed.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(parsed.Body, Is.EqualTo("Build well."));
    }

    [Test]
    public void ParseSubagentWithoutFrontmatterDefaultsToObserve()
    {
        AgentSubagent parsed = AgentPackLoader.ParseSubagent("Helper", "# Helper\nDo the thing.");

        Assert.That(parsed.Name, Is.EqualTo("helper"), "沒有 frontmatter 用資料夾名");
        Assert.That(parsed.Categories, Is.EqualTo([ToolCategory.Observe]));
        Assert.That(parsed.ReadOnly, Is.False);
        Assert.That(parsed.Reasoning, Is.Null, "沒寫就用設定頁的子代理預設");
        Assert.That(parsed.Description, Is.EqualTo("Helper"));
    }

    [Test]
    public void LoaderReadsAgentsFolderAndSkipsDuplicates()
    {
        string root = Path.Combine(Path.GetTempPath(), "RimAgentSubagents_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Agents", "a-scout"));
            Directory.CreateDirectory(Path.Combine(root, "Agents", "b-copy"));
            Directory.CreateDirectory(Path.Combine(root, "Agents", "c-empty"));
            File.WriteAllText(Path.Combine(root, "Agents", "a-scout", "AGENT.md"), "---\nname: scout\n---\nLook.");
            File.WriteAllText(Path.Combine(root, "Agents", "b-copy", "AGENT.md"), "---\nname: Scout\n---\nDuplicate.");

            AgentPack pack = AgentPackLoader.Load(root);

            Assert.That(pack.Subagents.Select(s => s.Name), Is.EqualTo(["scout"]), "同名只留第一個，沒有 AGENT.md 的資料夾略過");
            Assert.That(pack.Subagents[0].Body, Is.EqualTo("Look."));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void SubagentPromptCarriesExpansionsBeforeBody()
    {
        var subagent = new AgentSubagent { Name = "scout", Body = "BODY" };

        string prompt = SubagentTools.BuildPrompt(subagent, "Game expansions active: Biotech.");

        Assert.That(prompt, Does.EndWith("Game expansions active: Biotech.\n\nBODY"));
        Assert.That(SubagentTools.BuildPrompt(subagent), Does.Not.Contain("Game expansions"));
    }

    [Test]
    public void ShippedScoutIsReadOnly()
    {
        AgentPack pack = AgentPackLoader.Load(Path.Combine(FindRepositoryRoot(), "AgentPack"));

        AgentSubagent scout = pack.Subagents.Single(s => s.Name == "scout");
        Assert.That(scout.ReadOnly, Is.True);
        Assert.That(scout.Categories, Does.Not.Contain(ToolCategory.Mcp), "偵察不碰外部 MCP 程式");
        Assert.That(scout.Body, Is.Not.Empty);
    }

    [Test]
    public void DelegateToolIsOfferedOnlyWithPermissionAndSubagents()
    {
        var client = new ScriptedChatClient().Reply("ok");
        var withAgents = ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents);

        RimLLMChatOptions offered = NewSession(client, withAgents, AgentMode.Confirm, new[] { Scout }).BuildRunOptions();
        RimLLMChatOptions noPermission = NewSession(client, ToolPermissions.For(ToolCategory.Observe), AgentMode.Confirm, new[] { Scout }).BuildRunOptions();
        RimLLMChatOptions noSubagents = NewSession(client, withAgents, AgentMode.Confirm, Array.Empty<AgentSubagent>()).BuildRunOptions();

        Assert.That(offered.Tools.Select(t => t.Name), Does.Contain(SubagentTools.ToolName));
        Assert.That(offered.Instructions, Does.Contain("- scout (read-only; several tasks run in parallel): look around"));
        Assert.That(noPermission.Tools.Select(t => t.Name), Does.Not.Contain(SubagentTools.ToolName));
        Assert.That(noPermission.Instructions ?? string.Empty, Does.Not.Contain("Subagents"));
        Assert.That(noSubagents.Tools.Select(t => t.Name), Does.Not.Contain(SubagentTools.ToolName));
    }

    [Test]
    public void SubagentToolsAreScopedToCategoriesPermissionsAndReadOnly()
    {
        var registry = new AgentToolRegistry(_world, new AgentTranscript());
        var permissions = ToolPermissions.For(ToolCategory.Observe, ToolCategory.Build);

        List<AgentToolDescriptor> byName = [.. registry.Descriptors];
        IList<AITool> readOnly = registry.SelectForSubagent(permissions, AgentMode.Confirm, new[] { ToolCategory.Observe, ToolCategory.Work }, true);
        IList<AITool> full = registry.SelectForSubagent(permissions, AgentMode.Confirm, new[] { ToolCategory.Observe }, false);

        Assert.That(readOnly, Is.Not.Empty);
        foreach (AITool tool in readOnly)
        {
            AgentToolDescriptor d = byName.Single(x => x.Name == tool.Name);
            Assert.That(d.Category, Is.EqualTo(ToolCategory.Observe), "Work 沒被玩家允許、Build 沒被子代理宣告");
            Assert.That(d.Risk, Is.EqualTo(ToolRisk.Query), tool.Name);
        }
        Assert.That(full.Select(t => t.Name), Does.Contain("set_research"), "非唯讀子代理拿得到該類別的命令");
        Assert.That(readOnly.Count(t => t.Name == ToolOutputTools.ToolName), Is.EqualTo(1),
            "主 Agent 與子代理共用一個 read_tool_output，不能重複公告");
    }

    [Test]
    public async Task SubagentToolOutputStoreIsIsolatedFromTheMainAgent()
    {
        for (int i = 0; i < 200; i++)
        {
            _world.Things.Add(FakeGameWorld.MakeThing(1000 + i, "MineableSteel",
                "compacted steel deposit with a deliberately long label for this test", ThingKind.Mineable, i % 50, i / 50));
        }
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "scout", tasks = new[] { "Find the steel." } })
            .CallTool("find_things", new { kind = "mineable", name = "", x1 = -1, z1 = -1, x2 = -1, z2 = -1, limit = 200 })
            .CallTool(ToolOutputTools.ToolName, new { id = "out_1", offset = 0, find = "(25,1)" })
            .Reply("Found the steel.")
            .CallTool(ToolOutputTools.ToolName, new { id = "out_1", offset = 0, find = "" })
            .Reply("Main agent cannot read the subagent output.");
        ColonyAgentSession session = NewSession(client,
            ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents), AgentMode.Confirm, new[] { Scout });

        await session.SendAsync("survey the steel");

        TranscriptEntry[] reads = [.. session.Transcript.Snapshot().Where(e => e.ToolName == ToolOutputTools.ToolName)];
        Assert.That(reads, Has.Length.EqualTo(2));
        Assert.That(reads[0].Failed, Is.False, "子代理應讀到自己的 out_1");
        Assert.That(reads[0].Result, Does.Contain("(25,1)"));
        Assert.That(reads[1].Failed, Is.True, "主 Agent 不應讀到子代理的 out_1");
    }

    [Test]
    public async Task DelegateRunsSubagentInFreshContextWithItsModelAndLabelsItsTools()
    {
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "scout", tasks = new[] { "Who lives here?" } })
            .CallTool("list_colonists", null)
            .Reply("Only Alice.")
            .Reply("Alice is alone.");
        var routing = new ModelRouting
        {
            SubagentModel = name => name == "scout" ? "p:cheap" : null,
            SubagentReasoning = () => ReasoningLevel.Low
        };
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Scout }, routing);
        session.PreferredModel = "p:main";

        string reply = await session.SendAsync("who is in the colony?");

        Assert.That(reply, Is.EqualTo("Alice is alone."));
        IList<ChatMessage> childFirst = client.ReceivedMessages[1];
        Assert.That(childFirst.Count, Is.EqualTo(2), "子代理只看得到自己的提示詞與任務，看不到主對話");
        Assert.That(childFirst[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(childFirst[0].Text, Does.Contain("SCOUT BODY"));
        Assert.That(childFirst[1].Text, Is.EqualTo("Who lives here?"));
        ChatOptions childOptions = client.ReceivedOptions[1];
        Assert.That(childOptions.ModelId, Is.EqualTo("p:cheap"), "子代理走自己的模型槽");
        Assert.That(childOptions.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.Low));
        Assert.That(childOptions.Tools.Select(t => t.Name), Does.Not.Contain(SubagentTools.ToolName), "只委派一層");
        Assert.That(client.ReceivedOptions[0].ModelId, Is.EqualTo("p:main"), "主 Agent 仍用自己的模型");

        IReadOnlyList<TranscriptEntry> entries = session.Transcript.Snapshot();
        TranscriptEntry delegated = entries.Single(e => e.ToolName == SubagentTools.ToolName);
        TranscriptEntry listed = entries.Single(e => e.ToolName == "list_colonists");
        Assert.That(delegated.Result, Is.EqualTo("[scout] Only Alice."));
        Assert.That(delegated.ToolCategory, Is.EqualTo(ToolCategory.Agents));
        Assert.That(listed.Agent, Is.EqualTo("scout"), "子代理的工具紀錄要標上名稱");
        Assert.That(delegated.Agent, Is.Empty);
        Assert.That(entries.ToList().IndexOf(delegated), Is.LessThan(entries.ToList().IndexOf(listed)), "委派紀錄排在子代理的工具之前");
        Assert.That(session.History.Any(m => m.Text.Contains("Who lives here?") && m.Role == ChatRole.System), Is.False);
    }

    [Test]
    public async Task SubagentStoppedAtTheStepLimitSaysSoInsteadOfReturningRawToolCalls()
    {
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "scout", tasks = new[] { "Look everywhere." } });
        for (int i = 0; i < SubagentTools.MaxSteps; i++) client.CallTool("list_colonists", null);
        client.Reply("<tool_call> <function=list_colonists> </function> </tool_call>")
              .Reply("The scout ran out of steps.");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Scout });

        await session.SendAsync("survey the colony");

        TranscriptEntry delegated = session.Transcript.Snapshot().Single(e => e.ToolName == SubagentTools.ToolName);
        Assert.That(delegated.Result, Does.Not.Contain("<tool_call>"));
        Assert.That(delegated.Result, Does.Contain("stopped at the subagent step limit"));
    }

    [Test]
    public async Task SubagentReasoningFromAgentFileOverridesSlot()
    {
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "steward", tasks = new[] { "check" } })
            .Reply("checked")
            .Reply("done");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Steward }, new ModelRouting { SubagentReasoning = () => ReasoningLevel.Off });

        await session.SendAsync("go");

        Assert.That(client.ReceivedOptions[1].Reasoning?.Effort, Is.EqualTo(ReasoningEffort.High));
    }

    [Test]
    public async Task SubagentCommandsStillGoThroughTheConfirmationGate()
    {
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "steward", tasks = new[] { "Make Ali a doctor." } })
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .Reply("Held for confirmation.")
            .Reply("Waiting for you to confirm.");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Steward });

        await session.SendAsync("make Ali a doctor");

        Assert.That(session.HasPendingConfirmation, Is.True, "委派不是後門：確認模式照擋");
        Assert.That(_world.Calls.Where(c => c.StartsWith("SetWorkPriority", StringComparison.Ordinal)), Is.Empty);
        await session.ConfirmAsync();
        Assert.That(_world.Calls.Where(c => c.StartsWith("SetWorkPriority", StringComparison.Ordinal)), Is.Not.Empty, "按下確認執行子代理捕捉的原呼叫");
    }

    [Test]
    public async Task ReadOnlyTasksRunInParallel()
    {
        var client = new ParallelProbeClient(expectedChildren: 2);
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Scout });

        await session.SendAsync("look twice");

        TranscriptEntry delegated = session.Transcript.Snapshot().Single(e => e.ToolName == SubagentTools.ToolName);
        Assert.That(delegated.Result, Does.Contain("[scout task 1] peers-seen").And.Contain("[scout task 2] peers-seen"),
            "兩個唯讀任務同時在飛，才看得到彼此");
    }

    [Test]
    public async Task NonReadOnlyTasksRunOneAfterAnother()
    {
        var client = new ParallelProbeClient(expectedChildren: 2, agent: "steward");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Steward });

        await session.SendAsync("look twice");

        TranscriptEntry delegated = session.Transcript.Snapshot().Single(e => e.ToolName == SubagentTools.ToolName);
        Assert.That(delegated.Result, Does.Contain("[steward task 1] alone").And.Contain("[steward task 2] peers-seen"),
            "依序執行：第一個任務跑完才輪到第二個");
    }

    [Test]
    public async Task UnknownSubagentAndTooManyTasksAreRejected()
    {
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "ghost", tasks = new[] { "x" } })
            .CallTool(SubagentTools.ToolName, new { agent = "scout", tasks = new[] { "1", "2", "3", "4", "5" } })
            .Reply("gave up");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Confirm, new[] { Scout });

        await session.SendAsync("go");

        List<TranscriptEntry> calls = [.. session.Transcript.Snapshot().Where(e => e.ToolName == SubagentTools.ToolName)];
        Assert.That(calls[0].Failed, Is.True);
        Assert.That(calls[0].Result, Does.Contain("unknown subagent 'ghost'").And.Contain("scout"));
        Assert.That(calls[1].Result, Does.Contain("at most 4 tasks"));
        Assert.That(client.CallCount, Is.EqualTo(3), "被拒絕的委派不起子代理的模型呼叫");
    }

    [Test]
    public async Task ClassifierUsesItsReasoningSlot()
    {
        var agentClient = new ScriptedChatClient()
            .CallTool("set_work_priorities", new { assignments = "Ali:doctor=1" })
            .Reply("Done.");
        var classifierClient = new ScriptedChatClient().Reply("{\"risk\": \"low\", \"authorized\": true}");
        var session = new ColonyAgentSession(agentClient, _world, () => ToolPermissions.For(ToolCategory.Work), 8,
            () => AgentMode.Auto, classifierClient: classifierClient, classifierModel: () => "p:judge",
            classifierPrompt: () => "prompt", routing: new ModelRouting { ClassifierReasoning = () => ReasoningLevel.Off });

        await session.SendAsync("set Ali's doctor to 1");

        ChatOptions options = classifierClient.ReceivedOptions.Single();
        Assert.That(options.ModelId, Is.EqualTo("p:judge"));
        Assert.That(new RimLLMChatOptions { AdditionalProperties = options.AdditionalProperties }.DisableReasoning, Is.True);
    }

    [Test]
    public async Task CompactUsesItsModelSlot()
    {
        var client = new ScriptedChatClient();
        for (int i = 0; i < 6; i++) client.Reply("ok " + i);
        client.Reply("Summary of the colony so far.");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe), AgentMode.Confirm,
            Array.Empty<AgentSubagent>(), new ModelRouting { CompactModel = () => "p:summary", CompactReasoning = () => ReasoningLevel.Low });
        session.PreferredModel = "p:main";
        for (int i = 0; i < 6; i++) await session.SendAsync("instruction " + i);

        string result = await session.CompactAsync();

        Assert.That(result, Does.Contain("Context compacted"));
        ChatOptions summary = client.ReceivedOptions.Last();
        Assert.That(summary.ModelId, Is.EqualTo("p:summary"));
        Assert.That(summary.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.Low));
    }

    [Test]
    public void SubagentModelResolvesOverrideDefaultAndFollow()
    {
        var settings = new RimAgentSettings { agentModel = "openai:gpt-5" };

        Assert.That(settings.subagentModel, Is.EqualTo(RimAgentSettings.FollowAgentModel), "預設跟隨 Agent");
        Assert.That(settings.classifierModel, Is.EqualTo(RimAgentSettings.FollowAgentModel));
        Assert.That(settings.compactModel, Is.EqualTo(RimAgentSettings.FollowAgentModel));
        Assert.That(settings.ResolveSubagentModel("scout"), Is.EqualTo("openai:gpt-5"));

        settings.subagentModel = "google:flash";
        Assert.That(settings.ResolveSubagentModel("scout"), Is.EqualTo("google:flash"), "沒有個別設定用子代理預設");

        settings.subagentModels["scout"] = string.Empty;
        Assert.That(settings.ResolveSubagentModel("scout"), Is.EqualTo(string.Empty), "個別設成 Fallback");
        settings.subagentModels["scout"] = RimAgentSettings.FollowAgentModel;
        Assert.That(settings.ResolveSubagentModel("scout"), Is.EqualTo("openai:gpt-5"));
        Assert.That(settings.ResolveSubagentModel("other"), Is.EqualTo("google:flash"));
    }

    [TestCase("yes", true)]
    [TestCase("On", true)]
    [TestCase("1", true)]
    [TestCase("true", true)]
    [TestCase("no", false)]
    [TestCase("maybe", false)]
    public void ReadOnlyAcceptsYamlTruthyValues(string value, bool expected)
    {
        AgentSubagent parsed = AgentPackLoader.ParseSubagent("x", "---\nread_only: " + value + "\n---\nBody.");

        Assert.That(parsed.ReadOnly, Is.EqualTo(expected));
    }

    [Test]
    public void SubagentWithoutAllowedToolsIsNotOffered()
    {
        var builder = new AgentSubagent { Name = "builder", Categories = new[] { ToolCategory.Build }, Body = "B" };
        ColonyAgentSession session = NewSession(new ScriptedChatClient().Reply("ok"),
            ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents), AgentMode.Confirm, new[] { builder, Scout });

        RimLLMChatOptions options = session.BuildRunOptions();

        Assert.That(options.Instructions, Does.Contain("- scout"));
        Assert.That(options.Instructions, Does.Not.Contain("- builder"), "拿不到任何工具的子代理不列給模型");
    }

    [Test]
    public async Task ExhaustedAutonomousBudgetDoesNotStartSubagents()
    {
        var client = new ScriptedChatClient()
            .CallTool(SubagentTools.ToolName, new { agent = "scout", tasks = new[] { "a", "b" } })
            .Reply("done");
        ColonyAgentSession session = NewSession(client, ToolPermissions.For(ToolCategory.Observe, ToolCategory.Agents),
            AgentMode.Autonomous, new[] { Scout });

        await session.SendAsync("patrol", autonomous: true, maxToolSteps: 0);

        TranscriptEntry delegated = session.Transcript.Snapshot().Single(e => e.ToolName == SubagentTools.ToolName);
        Assert.That(delegated.Result, Does.Contain("budget exhausted"));
        Assert.That(client.ReceivedMessages.Any(m => m.Count > 0 && m[0].Role == ChatRole.System
            && m[0].Text.StartsWith("You are \"scout\"", StringComparison.Ordinal)), Is.False, "額度用完不起子代理的模型呼叫");
    }

    [Test]
    public void ExplicitReasoningRaisesTinyOutputCaps()
    {
        var high = new ChatOptions { MaxOutputTokens = 150 };
        var auto = new ChatOptions { MaxOutputTokens = 150 };
        var large = new ChatOptions { MaxOutputTokens = 4000 };

        ModelSlots.ApplyReasoning(high, ReasoningLevel.High);
        ModelSlots.ApplyReasoning(auto, ReasoningLevel.Auto);
        ModelSlots.ApplyReasoning(large, ReasoningLevel.Low);

        Assert.That(high.MaxOutputTokens, Is.EqualTo(ModelSlots.ReasoningOutputFloor), "分類器開思考時不能只剩 150 token");
        Assert.That(auto.MaxOutputTokens, Is.EqualTo(150), "沒開思考就維持原上限");
        Assert.That(large.MaxOutputTokens, Is.EqualTo(4000));
    }

    [Test]
    public void TranscriptSaveKeepsSubagentLabel()
    {
        var transcript = new AgentTranscript();
        transcript.Add(new TranscriptEntry { Kind = TranscriptKind.Tool, ToolName = "list_colonists", Agent = "scout" });

        TranscriptEntry restored = TranscriptEntryData.FromTranscript(transcript).Single().ToEntry();

        Assert.That(restored.Agent, Is.EqualTo("scout"));
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "About", "About.xml"))
                && Directory.Exists(Path.Combine(dir.FullName, "AgentPack"))) return dir.FullName;
        }
        Assert.Ignore("找不到 mod 根目錄（測試輸出不在 mod 資料夾內）");
        return null;
    }

    /// <summary>
    /// 主 Agent 第一次呼叫就委派兩個任務，第二次回文字；子代理的呼叫會等到另一個子代理也在飛（最多一秒），
    /// 回報 peers-seen 或 alone，用來分辨並行與依序。
    /// </summary>
    private sealed class ParallelProbeClient : IChatClient
    {
        private readonly int _expectedChildren;
        private readonly string _agent;
        private readonly TaskCompletionSource<bool> _allStarted = new();
        private int _mainCalls;
        private int _children;

        public ParallelProbeClient(int expectedChildren, string agent = "scout")
        {
            _expectedChildren = expectedChildren;
            _agent = agent;
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
        {
            List<ChatMessage> list = [.. messages];
            bool child = list.Count > 0 && list[0].Role == ChatRole.System && list[0].Text.StartsWith("You are \"" + _agent + "\"", StringComparison.Ordinal);
            if (!child)
            {
                if (Interlocked.Increment(ref _mainCalls) == 1)
                {
                    var args = new Dictionary<string, object> { ["agent"] = _agent, ["tasks"] = new[] { "a", "b" } };
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("call_1", SubagentTools.ToolName, args)]))
                    { FinishReason = ChatFinishReason.ToolCalls };
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
            }
            if (Interlocked.Increment(ref _children) >= _expectedChildren) _allStarted.TrySetResult(true);
            Task finished = await Task.WhenAny(_allStarted.Task, Task.Delay(1000)).ConfigureAwait(false);
            string report = finished == _allStarted.Task ? "peers-seen" : "alone";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, report));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object GetService(Type serviceType, object serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
