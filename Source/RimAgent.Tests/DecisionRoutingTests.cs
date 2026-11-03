using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;
using RimLLM_Framework;
using Verse;

namespace RimAgent.Tests;

[TestFixture]
public class DecisionRoutingTests
{
    private FakeGameWorld _world;
    private ScriptedChatClient _client;
    private ToolPermissions _permissions;
    private FakeDecisionService _fakeDecision;
    private RimAgentSettings _settings;
    private RimAgentSettings _originalSettings;

    [SetUp]
    public void SetUp()
    {
        typeof(Prefs).GetField("data", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, new PrefsData());
        RegisterTypeInGenTypes(typeof(RimAgentSettings));
        RegisterTypeInGenTypes(typeof(DecisionRouteCategory));
        _world = new FakeGameWorld();
        _client = new ScriptedChatClient();
        _permissions = ToolPermissions.For(ToolCategory.Observe, ToolCategory.Work);
        _fakeDecision = new FakeDecisionService();
        _settings = new RimAgentSettings
        {
            decisionFallbackModel = "fallback-model",
            decisionFallbackReasoning = ReasoningLevel.Low,
            decisionRoutingMinConfidence = 0.6f,
            decisionCategories =
            [
                new DecisionRouteCategory
                {
                    id = "observe",
                    name = "查詢解釋",
                    description = "Read colony state and explain.",
                    model = "small-model",
                    reasoning = ReasoningLevel.Off
                },
                new DecisionRouteCategory
                {
                    id = "routine",
                    name = "日常維護",
                    description = "Routine maintenance and work priorities.",
                    model = string.Empty, // 預設留空交給備援鏈
                    reasoning = ReasoningLevel.Auto
                },
                new DecisionRouteCategory
                {
                    id = "build",
                    name = "建造生產",
                    description = "Construct, deconstruct, and bills.",
                    model = "claude-3-7-sonnet",
                    reasoning = ReasoningLevel.High
                },
                new DecisionRouteCategory
                {
                    id = "risky",
                    name = "高風險規劃",
                    description = "Combat, trade, medical, surgery.",
                    model = "deepseek-r1",
                    reasoning = ReasoningLevel.High
                }
            ]
        };

        _originalSettings = RimAgentMod.Settings;
        RimAgentMod.Settings = _settings;
    }

    [TearDown]
    public void TearDown()
    {
        RimAgentMod.Settings = _originalSettings;
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Scribe.ForceStop();
        }
    }

    [Test]
    public async Task CategoryMatchedRoutesToConfiguredModelAndReasoning()
    {
        _fakeDecision.ReplyRoute("build", 0.85);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("build a wall around our crops", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.False);
        Assert.That(route.CategoryId, Is.EqualTo("build"));
        Assert.That(route.Model, Is.EqualTo("claude-3-7-sonnet"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.High));
        Assert.That(route.Confidence, Is.EqualTo(0.85));
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ConfidenceBelowThresholdFallsBackToConfiguredFallback()
    {
        _fakeDecision.ReplyRoute("build", 0.45); // threshold is 0.60
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("maybe do some construction", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Model, Is.EqualTo("fallback-model"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("below threshold"));
    }

    [Test]
    public async Task CategoryWithFollowAgentModelUsesFallbackInsteadOfSentinel()
    {
        // 「跟隨 Agent」在分流模式下指向分流器自己，送給框架會是無效的模型名。
        _settings.decisionCategories[0].model = RimAgentSettings.FollowAgentModel;
        _fakeDecision.ReplyRoute("observe", 0.9);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("how many colonists are alive?", _settings.decisionCategories);

        Assert.That(route.CategoryId, Is.EqualTo("observe"));
        Assert.That(route.Model, Is.EqualTo("fallback-model"));
        Assert.That(route.Model, Is.Not.EqualTo(RimAgentSettings.FollowAgentModel));
    }

    [Test]
    public async Task FallbackModelSetToFollowAgentResolvesToEmptyForFrameworkChain()
    {
        // 備援模型選「跟隨 Agent」時，分流器本身就是當前模型 → 交給框架備援鏈。
        _settings.decisionFallbackModel = RimAgentSettings.FollowAgentModel;
        _fakeDecision.ReplyRoute("build", 0.2);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("maybe build something", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.Model, Is.Empty);
    }

    [Test]
    public async Task DecisionServiceFailureFallsBackGracefully()
    {
        _fakeDecision.Throw(new InvalidOperationException("API timeout"));
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("build a wooden door", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Model, Is.EqualTo("fallback-model"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("Decision model evaluation failed"));
    }

    [Test]
    public async Task NullDecisionServiceFallsBackGracefully()
    {
        var router = new DecisionModelRouter(decisionService: null, serviceFactory: null, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("examine power grid", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Model, Is.EqualTo("fallback-model"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("unavailable"));
    }

    [Test]
    public async Task NoCategoriesConfiguredFallsBackWithoutCallingDecisionService()
    {
        _settings.decisionCategories.Clear();
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("observe the colony", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Model, Is.EqualTo("fallback-model"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("No categories configured"));
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(0));
    }

    [Test]
    public async Task EmptyModelInRouteDoesNotSetModelId()
    {
        _settings.decisionFallbackModel = string.Empty;
        _fakeDecision.ReplyRoute("routine", 0.95);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);
        var session = new ColonyAgentSession(
            _client,
            _world,
            () => _permissions,
            maxSteps: 8,
            router: router)
        {
            PreferredModel = RimAgentSettings.AutoRoutingModel
        };

        RimLLMChatOptions options = await session.BuildRunOptionsAsync("set cleaning priority");

        Assert.That(options.ModelId, Is.Null, "分流結果空模型不應指定 options.ModelId，交由框架備援鏈");
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ManualModelSelectionNeverInvokesDecisionModel()
    {
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);
        _client.Reply("Done cleaning.");
        var session = new ColonyAgentSession(
            _client,
            _world,
            () => _permissions,
            maxSteps: 8,
            router: router)
        {
            PreferredModel = "manual-gpt-4o"
        };

        string reply = await session.SendAsync("clean the barracks");

        Assert.That(reply, Is.EqualTo("Done cleaning."));
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(0), "手選模型時決策模型完全不應被呼叫");

        IReadOnlyList<TranscriptEntry> transcript = session.Transcript.Snapshot();
        TranscriptEntry assistant = transcript.Last(e => e.Kind == TranscriptKind.Assistant);
        Assert.That(assistant.RoutedModel, Is.Empty);
    }

    [Test]
    public async Task AutoRoutingFillsTranscriptRoutedModelOnSuccess()
    {
        _fakeDecision.ReplyRoute("build", 0.92);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);
        _client.Reply("Building wall started.");
        var session = new ColonyAgentSession(
            _client,
            _world,
            () => _permissions,
            maxSteps: 8,
            router: router)
        {
            PreferredModel = RimAgentSettings.AutoRoutingModel
        };

        string reply = await session.SendAsync("build a sandstone wall");

        Assert.That(reply, Is.EqualTo("Building wall started."));
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(1));

        IReadOnlyList<TranscriptEntry> transcript = session.Transcript.Snapshot();
        TranscriptEntry assistant = transcript.Last(e => e.Kind == TranscriptKind.Assistant);
        Assert.That(assistant.RoutedModel, Is.EqualTo("claude-3-7-sonnet"));
    }

    [Test]
    public void SettingsExposeDataRoundTripPreservesCustomCategoriesAndRoutingFields()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "RimAgentSettings_RoundTrip_" + Guid.NewGuid() + ".xml");
        try
        {
            var original = new RimAgentSettings
            {
                decisionFallbackModel = "roundtrip-fallback",
                decisionFallbackReasoning = ReasoningLevel.High,
                decisionRoutingMinConfidence = 0.78f,
                decisionCategories =
                [
                    new DecisionRouteCategory
                    {
                        id = "scouting",
                        name = "斥候偵察",
                        description = "Scout the external world and neighbors.",
                        model = "gemini-flash",
                        reasoning = ReasoningLevel.Medium
                    },
                    new DecisionRouteCategory
                    {
                        id = "risky",
                        name = "危險動作",
                        description = "Surgery and risky maneuvers.",
                        model = "o3-mini",
                        reasoning = ReasoningLevel.High
                    }
                ]
            };

            Scribe.saver.InitSaving(tempFile, "SettingsBlock");
            try
            {
                ModSettings s = original;
                Scribe_Deep.Look(ref s, "ModSettings");
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }

            RimAgentSettings loaded = null;
            Scribe.loader.InitLoading(tempFile);
            try
            {
                Scribe_Deep.Look(ref loaded, "ModSettings");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.decisionFallbackModel, Is.EqualTo("roundtrip-fallback"));
            Assert.That(loaded.decisionFallbackReasoning, Is.EqualTo(ReasoningLevel.High));
            Assert.That(loaded.decisionRoutingMinConfidence, Is.EqualTo(0.78f).Within(0.001f));
            Assert.That(loaded.decisionCategories, Has.Count.EqualTo(2));

            DecisionRouteCategory cat0 = loaded.decisionCategories[0];
            Assert.That(cat0.id, Is.EqualTo("scouting"));
            Assert.That(cat0.name, Is.EqualTo("斥候偵察"));
            Assert.That(cat0.description, Is.EqualTo("Scout the external world and neighbors."));
            Assert.That(cat0.model, Is.EqualTo("gemini-flash"));
            Assert.That(cat0.reasoning, Is.EqualTo(ReasoningLevel.Medium));

            DecisionRouteCategory cat1 = loaded.decisionCategories[1];
            Assert.That(cat1.id, Is.EqualTo("risky"));
            Assert.That(cat1.name, Is.EqualTo("危險動作"));
            Assert.That(cat1.description, Is.EqualTo("Surgery and risky maneuvers."));
            Assert.That(cat1.model, Is.EqualTo("o3-mini"));
            Assert.That(cat1.reasoning, Is.EqualTo(ReasoningLevel.High));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public void SettingsExposeDataMissingXmlRestoresDefaultCategories()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "RimAgentSettings_Missing_" + Guid.NewGuid() + ".xml");
        try
        {
            File.WriteAllText(tempFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock>\n  <ModSettings Class=\"RimAgent.RimAgentSettings\">\n    <mode>Confirm</mode>\n  </ModSettings>\n</SettingsBlock>");

            RimAgentSettings loaded = null;
            Scribe.loader.InitLoading(tempFile);
            try
            {
                Scribe_Deep.Look(ref loaded, "ModSettings");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.decisionCategories, Is.Not.Null);
            Assert.That(loaded.decisionCategories, Has.Count.EqualTo(4));
            Assert.That(loaded.decisionCategories.Select(c => c.id), Is.EqualTo(new[] { "observe", "routine", "build", "risky" }));
            Assert.That(loaded.decisionFallbackModel, Is.EqualTo(string.Empty));
            Assert.That(loaded.decisionFallbackReasoning, Is.EqualTo(ReasoningLevel.Auto));
            Assert.That(loaded.decisionRoutingMinConfidence, Is.EqualTo(0.6f).Within(0.001f));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public void SettingsExposeDataClampsConfidence()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "RimAgentSettings_Clamp_" + Guid.NewGuid() + ".xml");
        try
        {
            File.WriteAllText(tempFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock>\n  <ModSettings Class=\"RimAgent.RimAgentSettings\">\n    <decisionRoutingMinConfidence>1.85</decisionRoutingMinConfidence>\n  </ModSettings>\n</SettingsBlock>");

            RimAgentSettings loaded = null;
            Scribe.loader.InitLoading(tempFile);
            try
            {
                Scribe_Deep.Look(ref loaded, "ModSettings");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.decisionRoutingMinConfidence, Is.EqualTo(1.0f).Within(0.001f));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public void DescribeLimitsHistoryAndTruncatesMessageLengths()
    {
        var longUser = new string('A', 800);
        var longReply = new string('B', 800);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Old turn 1"),
            new(ChatRole.Assistant, "Old reply 1"),
            new(ChatRole.User, "Old turn 2"),
            new(ChatRole.Assistant, "Old reply 2"),
            new(ChatRole.User, longUser),
            new(ChatRole.Assistant, longReply),
            new(ChatRole.User, "Turn 4 user"),
            new(ChatRole.Assistant, "Turn 4 assistant"),
            new(ChatRole.User, "Turn 5 user"),
            new(ChatRole.Assistant, "Turn 5 assistant")
        };

        string state = DecisionModelRouter.Describe("current new instruction", messages);

        Assert.That(state, Does.Contain("current new instruction"));
        Assert.That(state, Does.Not.Contain("Old turn 1"), "應只保留最近 3 輪歷史");
        Assert.That(state, Does.Not.Contain("Old turn 2"));
        Assert.That(state, Does.Contain(new string('A', 600)));
        Assert.That(state, Does.Not.Contain(new string('A', 601)), "User 訊息應截斷在 600 字");
        Assert.That(state, Does.Contain(new string('B', 300) + "\n[...]\n" + new string('B', 300)), "Assistant 訊息應取頭 300 尾 300");
    }

    [Test]
    public async Task DecisionRouterSnapshotsCategoriesBeforeAwaitingTheModel()
    {
        _fakeDecision.YieldBeforeResult = true;
        _fakeDecision.ReplyRoute("build", 0.92);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);
        List<DecisionRouteCategory> live = _settings.decisionCategories;

        Task<DecisionRoute> pending = router.ResolveAsync("build a wall", live);
        live.Clear(); // 模擬玩家在模型回應前清空分類
        DecisionRoute route = await pending;

        Assert.That(route.CategoryId, Is.EqualTo("build"), "await 前已送出的分類快照不應被後續設定變更影響");
        Assert.That(route.Model, Is.EqualTo("claude-3-7-sonnet"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.High));
    }

    [Test]
    public void AuxiliarySlotsFollowTheCurrentRoutedModel()
    {
        _settings.agentModel = RimAgentSettings.AutoRoutingModel;
        _settings.classifierModel = RimAgentSettings.FollowAgentModel;
        _settings.compactModel = RimAgentSettings.FollowAgentModel;
        _settings.subagentModel = RimAgentSettings.FollowAgentModel;
        Assert.That(_settings.ResolveClassifierModel("provider:routed"), Is.EqualTo("provider:routed"));
        Assert.That(_settings.ResolveModelSlot(_settings.compactModel, "provider:routed"), Is.EqualTo("provider:routed"));
        Assert.That(_settings.ResolveSubagentModel("provider:routed"), Is.EqualTo("provider:routed"));
        Assert.That(_settings.ResolveModelSlot("provider:explicit", "provider:routed"), Is.EqualTo("provider:explicit"));
    }

    [Test]
    public async Task FollowingModelUsesActualFallbackAndResetsWhenSelectionChanges()
    {
        _client.ReplyWithModel("done", "provider:actual-fallback");
        var session = new ColonyAgentSession(_client, _world, () => _permissions, 8)
        {
            PreferredModel = "provider:preferred"
        };
        await session.SendAsync("hello");
        _settings.agentModel = session.PreferredModel;
        Assert.That(_settings.ResolveModelSlot(RimAgentSettings.FollowAgentModel, session.CurrentAgentModel),
            Is.EqualTo("provider:actual-fallback"));

        session.PreferredModel = "provider:new";
        _settings.agentModel = session.PreferredModel;
        Assert.That(_settings.ResolveModelSlot(RimAgentSettings.FollowAgentModel, session.CurrentAgentModel),
            Is.EqualTo("provider:new"));
    }

    [Test]
    public void ExplicitDecisionProviderIsAvailableEvenWhenFrameworkDefaultIsDisabled()
    {
        var disabled = new DecisionAvailability { Enabled = false };
        Assert.That(DecisionServiceFactory.CanUseDecisionModel(disabled, "provider"), Is.True);
        Assert.That(DecisionServiceFactory.CanUseDecisionModel(disabled, ""), Is.False);
        Assert.That(DecisionServiceFactory.CanUseDecisionModel(null, "provider"), Is.False);
    }

    [Test]
    public async Task ContextWindowUsesTheRoutedModelOfTheCurrentInstruction()
    {
        _fakeDecision.ReplyRoute("build", 0.92);
        var router = new DecisionModelRouter(_fakeDecision, settingsProvider: () => _settings);
        string lookedUpModel = null;
        var session = new ColonyAgentSession(
            _client,
            _world,
            () => _permissions,
            maxSteps: 8,
            contextWindowLookup: model =>
            {
                lookedUpModel = model;
                return 321000;
            },
            router: router)
        {
            PreferredModel = RimAgentSettings.AutoRoutingModel
        };

        await session.BuildRunOptionsAsync("build a sandstone wall");
        ColonyAgentSession.ContextStatus status = session.GetContextStatus();

        Assert.That(lookedUpModel, Is.EqualTo("claude-3-7-sonnet"), "上下文視窗要查本輪分流後的模型，不是 Agent 的哨兵值");
        Assert.That(status.ContextWindow, Is.EqualTo(321000));
    }

    [Test]
    public void CustomCategoryIdStaysUniqueAfterDeletingAMiddleCategory()
    {
        var categories = new List<DecisionRouteCategory>
        {
            new("custom_2", "two", "description"),
            new("custom_4", "four", "description")
        };

        string first = RimAgentMod.NextCustomCategoryId(categories);
        categories.Add(new DecisionRouteCategory(first, "new", "description"));
        string second = RimAgentMod.NextCustomCategoryId(categories);
        categories.Add(new DecisionRouteCategory(second, "new", "description"));
        string third = RimAgentMod.NextCustomCategoryId(categories);

        Assert.That(new[] { first, second, third }, Is.EqualTo(new[] { "custom_1", "custom_3", "custom_5" }));
        Assert.That(categories.Select(c => c.id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Is.EqualTo(categories.Count));
    }

    private static void RegisterTypeInGenTypes(Type type)
    {
        var cacheField = typeof(GenTypes).GetField("typeCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var cache = cacheField?.GetValue(null) as System.Collections.IDictionary;
        if (cache == null)
        {
            typeof(GenTypes).GetMethod("ClearCache", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            cache = cacheField?.GetValue(null) as System.Collections.IDictionary;
        }

        Type keyType = typeof(GenTypes).GetNestedType("TypeCacheKey", System.Reflection.BindingFlags.NonPublic);
        if (cache != null && keyType != null)
        {
            object keyFullName = Activator.CreateInstance(keyType, type.FullName, null);
            cache[keyFullName] = type;
            object keyName = Activator.CreateInstance(keyType, type.Name, null);
            cache[keyName] = type;
            if (!string.IsNullOrEmpty(type.Namespace))
            {
                object keyWithNs = Activator.CreateInstance(keyType, type.Name, type.Namespace);
                cache[keyWithNs] = type;
            }
        }
    }
}
