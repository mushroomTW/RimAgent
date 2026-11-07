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
            decisionFallbackReasoning = ReasoningLevel.Low,
            decisionRoutingMinConfidence = 0.6f,
            decisionCategories =
            [
                new DecisionRouteCategory
                {
                    id = "observe",
                    name = "查詢解釋",
                    description = "Read colony state and explain.",
                    reasoning = ReasoningLevel.Off
                },
                new DecisionRouteCategory
                {
                    id = "routine",
                    name = "日常維護",
                    description = "Routine maintenance and work priorities.",
                    reasoning = ReasoningLevel.Auto
                },
                new DecisionRouteCategory
                {
                    id = "build",
                    name = "建造生產",
                    description = "Construct, deconstruct, and bills.",
                    reasoning = ReasoningLevel.High
                },
                new DecisionRouteCategory
                {
                    id = "risky",
                    name = "高風險規劃",
                    description = "Combat, trade, medical, surgery.",
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
    public async Task CategoryMatchedRoutesToConfiguredReasoning()
    {
        _fakeDecision.ReplyRoute("build", 0.85);
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("build a wall around our crops", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.False);
        Assert.That(route.CategoryId, Is.EqualTo("build"));
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.High));
        Assert.That(route.Confidence, Is.EqualTo(0.85));
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ConfidenceBelowThresholdFallsBackToConfiguredFallback()
    {
        _fakeDecision.ReplyRoute("build", 0.45); // threshold is 0.60
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("maybe do some construction", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("below threshold"));
    }

    [Test]
    public async Task DecisionServiceFailureFallsBackGracefully()
    {
        _fakeDecision.Throw(new InvalidOperationException("API timeout"));
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("build a wooden door", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("Decision model evaluation failed"));
    }

    [Test]
    public async Task NullDecisionServiceFallsBackGracefully()
    {
        var router = new DecisionReasoningRouter(decisionService: null, serviceFactory: null, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("examine power grid", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("unavailable"));
    }

    [Test]
    public async Task NoCategoriesConfiguredFallsBackWithoutCallingDecisionService()
    {
        _settings.decisionCategories.Clear();
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);

        DecisionRoute route = await router.ResolveAsync("observe the colony", _settings.decisionCategories);

        Assert.That(route.IsFallback, Is.True);
        Assert.That(route.CategoryId, Is.Empty);
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.Low));
        Assert.That(route.Reason, Does.Contain("No categories configured"));
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(0));
    }

    [Test]
    public async Task RoutingKeepsPreferredModelAndAppliesRoutedReasoning()
    {
        _fakeDecision.ReplyRoute("build", 0.95);
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);
        var session = new ColonyAgentSession(
            _client,
            _world,
            () => _permissions,
            maxSteps: 8,
            router: router)
        {
            PreferredModel = "provider:manual",
            Reasoning = ReasoningLevel.Low,
            ReasoningRouting = true
        };

        RimLLMChatOptions options = await session.BuildRunOptionsAsync("build a sandstone wall");

        Assert.That(options.ModelId, Is.EqualTo("provider:manual"), "分流不得更換模型");
        Assert.That(options.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.High), "思考強度要用分流類別的設定");
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(1));
    }

    [Test]
    public async Task RoutingOffNeverInvokesDecisionModel()
    {
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);
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
        Assert.That(_fakeDecision.EvaluateCount, Is.EqualTo(0), "未開啟思考強度分流時決策模型完全不應被呼叫");
    }

    [Test]
    public void SettingsExposeDataRoundTripPreservesCustomCategoriesAndRoutingFields()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "RimAgentSettings_RoundTrip_" + Guid.NewGuid() + ".xml");
        try
        {
            var original = new RimAgentSettings
            {
                reasoningRouting = true,
                decisionFallbackReasoning = ReasoningLevel.High,
                decisionRoutingMinConfidence = 0.78f,
                decisionCategories =
                [
                    new DecisionRouteCategory
                    {
                        id = "scouting",
                        name = "斥候偵察",
                        description = "Scout the external world and neighbors.",
                        reasoning = ReasoningLevel.Medium
                    },
                    new DecisionRouteCategory
                    {
                        id = "risky",
                        name = "危險動作",
                        description = "Surgery and risky maneuvers.",
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
            Assert.That(loaded.reasoningRouting, Is.True);
            Assert.That(loaded.decisionFallbackReasoning, Is.EqualTo(ReasoningLevel.High));
            Assert.That(loaded.decisionRoutingMinConfidence, Is.EqualTo(0.78f).Within(0.001f));
            Assert.That(loaded.decisionCategories, Has.Count.EqualTo(2));

            DecisionRouteCategory cat0 = loaded.decisionCategories[0];
            Assert.That(cat0.id, Is.EqualTo("scouting"));
            Assert.That(cat0.name, Is.EqualTo("斥候偵察"));
            Assert.That(cat0.description, Is.EqualTo("Scout the external world and neighbors."));
            Assert.That(cat0.reasoning, Is.EqualTo(ReasoningLevel.Medium));

            DecisionRouteCategory cat1 = loaded.decisionCategories[1];
            Assert.That(cat1.id, Is.EqualTo("risky"));
            Assert.That(cat1.name, Is.EqualTo("危險動作"));
            Assert.That(cat1.description, Is.EqualTo("Surgery and risky maneuvers."));
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
            Assert.That(loaded.reasoningRouting, Is.False);
            Assert.That(loaded.decisionFallbackReasoning, Is.EqualTo(ReasoningLevel.Auto));
            Assert.That(loaded.decisionRoutingMinConfidence, Is.EqualTo(0.6f).Within(0.001f));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public void LegacyAutoRoutingModelMigratesToReasoningRouting()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "RimAgentSettings_Legacy_" + Guid.NewGuid() + ".xml");
        try
        {
            File.WriteAllText(tempFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock>\n  <ModSettings Class=\"RimAgent.RimAgentSettings\">\n    <agentModel>=auto</agentModel>\n    <decisionFallbackModel>old-fallback</decisionFallbackModel>\n  </ModSettings>\n</SettingsBlock>");

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
            Assert.That(loaded.agentModel, Is.Empty, "舊的 =auto 哨兵要改回預設模型");
            Assert.That(loaded.reasoningRouting, Is.True, "舊的自動分流要轉成思考強度分流");
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

        string state = DecisionReasoningRouter.Describe("current new instruction", messages);

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
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);
        List<DecisionRouteCategory> live = _settings.decisionCategories;

        Task<DecisionRoute> pending = router.ResolveAsync("build a wall", live);
        live.Clear(); // 模擬玩家在模型回應前清空分類
        DecisionRoute route = await pending;

        Assert.That(route.CategoryId, Is.EqualTo("build"), "await 前已送出的分類快照不應被後續設定變更影響");
        Assert.That(route.Reasoning, Is.EqualTo(ReasoningLevel.High));
    }

    [Test]
    public void AuxiliarySlotsFollowTheCurrentAgentModel()
    {
        _settings.agentModel = "provider:agent";
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
    public async Task ContextWindowUsesPreferredModelWhileRoutingReasoning()
    {
        _fakeDecision.ReplyRoute("build", 0.92);
        var router = new DecisionReasoningRouter(_fakeDecision, settingsProvider: () => _settings);
        string lookedUpModel = null;
        var session = new ColonyAgentSession(
            _client,
            _world,
            () => _permissions,
            maxSteps: 8,
            contextWindowLookup: model =>
            {
                if (model == "provider:manual") lookedUpModel = model;
                return 321000;
            },
            router: router)
        {
            PreferredModel = "provider:manual",
            ReasoningRouting = true
        };

        await session.BuildRunOptionsAsync("build a sandstone wall");
        ColonyAgentSession.ContextStatus status = session.GetContextStatus();

        Assert.That(lookedUpModel, Is.EqualTo("provider:manual"), "思考強度分流不換模型，上下文視窗要查玩家選的模型");
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
