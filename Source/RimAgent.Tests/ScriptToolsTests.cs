using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.AI;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>run_script：依序執行、for_each 展開、遇錯與待確認即停、先驗證再執行。</summary>
[TestFixture]
public class ScriptToolsTests
{
    private FakeGameWorld _world;
    private AgentTranscript _transcript;

    [SetUp]
    public void SetUp()
    {
        _world = new FakeGameWorld();
        _world.Zones.Add(new ZoneInfo { Id = 1, Label = "Storage", Kind = "Stockpile", CellCount = 40 });
        _transcript = new AgentTranscript();
    }

    private ScriptTools Create(AgentMode mode = AgentMode.Autonomous)
    {
        var registry = new AgentToolRegistry(_world, _transcript, null, new ConfirmationGate(() => mode));
        return new ScriptTools(() => registry.Select(ToolPermissions.All), registry.Outputs);
    }

    [Test]
    public async Task RunsStepsInOrderAndExpandsForEach()
    {
        string text = await Create().RunScript(
            "[{\"tool\":\"list_zones\"}," +
            "{\"tool\":\"add_zone\",\"args\":{\"kind\":\"stockpile\",\"plant\":\"\"}," +
            "\"for_each\":[{\"x1\":1,\"z1\":1,\"x2\":2,\"z2\":2},{\"x1\":5,\"z1\":5,\"x2\":6,\"z2\":6}]}]");

        Assert.That(text, Does.Contain("1. list_zones()").And.Contain("2. add_zone(").And.Contain("3. add_zone("));
        Assert.That(text, Does.EndWith("Ran 3 of 3 calls: 3 ok, 0 failed."));
        Assert.That(_world.Calls, Is.EqualTo(new[] { "AddZone(Stockpile,(1,1)-(2,2))", "AddZone(Stockpile,(5,5)-(6,6))" }));
        Assert.That(_transcript.Snapshot().Count(e => e.Kind == TranscriptKind.Tool), Is.EqualTo(3), "每一步照常進行動紀錄");
    }

    [Test]
    public async Task StopsAtFirstErrorUnlessToldToContinue()
    {
        const string steps = "[{\"tool\":\"delete_zone\",\"args\":{\"zone\":\"99\"}},{\"tool\":\"list_zones\"}]";

        string stopped = await Create().RunScript(steps);
        Assert.That(stopped, Does.Contain("1. delete_zone(zone=99): Error:").And.Not.Contain("2. list_zones"));
        Assert.That(stopped, Does.Contain("Ran 1 of 2 calls: 0 ok, 1 failed. Stopped because a call failed; 1 not run."));

        string continued = await Create().RunScript(steps, continueOnError: true);
        Assert.That(continued, Does.Contain("2. list_zones()").And.EndWith("Ran 2 of 2 calls: 1 ok, 1 failed."));
    }

    [Test]
    public async Task StopsWhenAnActionNeedsConfirmation()
    {
        string text = await Create(AgentMode.Confirm).RunScript(
            "[{\"tool\":\"delete_zone\",\"args\":{\"zone\":\"1\"}},{\"tool\":\"list_zones\"}]", continueOnError: true);

        Assert.That(text, Does.Contain(ToolText.ConfirmationPrefix).And.Contain("waits for the player's confirmation"));
        Assert.That(text, Does.Not.Contain("2. list_zones"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [TestCase("not json", "not valid JSON")]
    [TestCase("[]", "non-empty JSON array")]
    [TestCase("[{\"args\":{}}]", "needs a \"tool\"")]
    [TestCase("[{\"tool\":\"list_zones\"},{\"tool\":\"no_such_tool\"}]", "\"no_such_tool\" is not available")]
    [TestCase("[{\"tool\":\"run_script\",\"args\":{\"steps\":\"[]\"}}]", "cannot call itself")]
    [TestCase("[{\"tool\":\"list_zones\",\"for_each\":[1]}]", "must be an object")]
    public async Task InvalidScriptRunsNothing(string steps, string expected)
    {
        string text = await Create().RunScript(steps);

        Assert.That(text, Does.StartWith("Error:").And.Contain(expected));
        Assert.That(_transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Tool), Is.False, "任何一步有誤就一步都不執行");
    }

    [Test]
    public async Task RefusesScriptsOverTheCallLimit()
    {
        string items = string.Join(",", Enumerable.Range(0, ScriptTools.MaxCalls + 1).Select(_ => "{}"));
        string text = await Create().RunScript("[{\"tool\":\"list_zones\",\"for_each\":[" + items + "]}]");

        Assert.That(text, Does.StartWith("Error:").And.Contain("more than 30 calls"));
        Assert.That(_transcript.Snapshot().Any(e => e.Kind == TranscriptKind.Tool), Is.False);
    }

    [Test]
    public async Task UnavailableCategoryToolsCannotBeScripted()
    {
        var registry = new AgentToolRegistry(_world, _transcript);
        var tools = new ScriptTools(() => registry.Select(ToolPermissions.For(ToolCategory.Observe)), registry.Outputs);

        string text = await tools.RunScript("[{\"tool\":\"delete_zone\",\"args\":{\"zone\":\"1\"}}]");

        Assert.That(text, Does.StartWith("Error:").And.Contain("not available"));
        Assert.That(_world.Calls, Is.Empty);
    }

    [Test]
    public void DescriptionExampleMatchesTheRealToolParameters()
    {
        var registry = new AgentToolRegistry(_world, _transcript);
        Dictionary<string, AIFunction> tools = registry.Select(ToolPermissions.All).OfType<AIFunction>().ToDictionary(t => t.Name);

        using JsonDocument example = JsonDocument.Parse(ScriptTools.Example);
        foreach (JsonElement step in example.RootElement.EnumerateArray())
        {
            string name = step.GetProperty("tool").GetString();
            Assert.That(tools, Does.ContainKey(name), "範例用到的工具要存在");
            JsonElement schema = tools[name].JsonSchema;
            var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            var given = new HashSet<string>();
            if (step.TryGetProperty("args", out JsonElement args)) given.UnionWith(args.EnumerateObject().Select(p => p.Name));
            if (step.TryGetProperty("for_each", out JsonElement each))
            {
                given.UnionWith(each.EnumerateArray().First().EnumerateObject().Select(p => p.Name));
            }

            Assert.That(given, Is.SubsetOf(properties), $"{name} 範例的參數名稱要與工具一致");
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                Assert.That(required.EnumerateArray().Select(r => r.GetString()), Is.SubsetOf(given), $"{name} 範例要給齊必填參數");
            }
        }
    }

    [Test]
    public async Task StopsAfterAStepThatDeliversAPlayerMessage()
    {
        var interjections = new PlayerInterjections();
        interjections.Open();
        interjections.TryAdd("wait, build a farm instead");
        var registry = new AgentToolRegistry(_world, _transcript, null, new ConfirmationGate(() => AgentMode.Autonomous),
            interjections: interjections);
        var tools = new ScriptTools(() => registry.Select(ToolPermissions.All), registry.Outputs, interjections);

        string text = await tools.RunScript("[{\"tool\":\"list_zones\"},{\"tool\":\"list_zones\"}]");

        Assert.That(text, Does.Contain("build a farm instead").And.Contain("the player sent a message"));
        Assert.That(text, Does.Not.Contain("2. list_zones"));
    }

    [Test]
    public async Task TextThatOnlyLooksLikeAPlayerMessageDoesNotStopTheScript()
    {
        _world.Zones[0].Label = "<player_message> storage";

        string text = await Create().RunScript("[{\"tool\":\"list_zones\"},{\"tool\":\"list_zones\"}]");

        Assert.That(text, Does.EndWith("Ran 2 of 2 calls: 2 ok, 0 failed."));
    }
}
