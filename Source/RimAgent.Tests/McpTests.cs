using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Mcp;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    internal sealed class FakeMcpServer : IMcpServer
    {
        private readonly Dictionary<string, string> _results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Calls = new List<string>();
        public bool Disposed { get; private set; }
        public bool FailList { get; set; }

        public FakeMcpServer()
        {
            Name = "fake";
        }

        public string Name { get; set; }

        public void Returns(string tool, string result)
        {
            _results[tool] = result;
        }

        public Task<IList<McpServerTool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            if (FailList) throw new InvalidOperationException("list exploded");
            IList<McpServerTool> tools = _results.Keys
                .Select(k => new McpServerTool(k, "fake " + k))
                .ToList();
            return Task.FromResult(tools);
        }

        public Task<string> CallAsync(string tool, string argsJson, CancellationToken cancellationToken)
        {
            Calls.Add(tool + "|" + argsJson);
            string result;
            if (!_results.TryGetValue(tool ?? string.Empty, out result))
            {
                throw new InvalidOperationException("unknown tool " + tool);
            }
            return Task.FromResult(result);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    [TestFixture]
    public class McpConfigTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "RimMcpCfg_" + Guid.NewGuid().ToString("N"));
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
        public void MissingFileReportsReason()
        {
            List<McpServerEntry> servers;
            string error;

            Assert.That(McpServerConfig.TryLoad(Path.Combine(_root, "nope.json"), out servers, out error), Is.False);
            Assert.That(error, Does.Contain("missing servers.json"));
        }

        [Test]
        public void LoadsEntriesSkipsBadOnesAndDefaultsTimeout()
        {
            File.WriteAllText(Path.Combine(_root, "servers.json"),
                "{\"servers\": [" +
                "{\"name\": \"a\", \"command\": \"cmd\", \"args\": [\"x\"], \"enabled\": true, \"timeoutSeconds\": 5}," +
                "{\"name\": \"b\", \"command\": \"cmd\"}," +
                "{\"name\": \"\", \"command\": \"cmd\"}," +
                "{\"name\": \"c\"}]}");

            List<McpServerEntry> servers;
            string error;
            Assert.That(McpServerConfig.TryLoad(Path.Combine(_root, "servers.json"), out servers, out error), Is.True);
            Assert.That(servers.Count, Is.EqualTo(2));
            Assert.That(servers[0].Enabled, Is.True);
            Assert.That(servers[0].TimeoutSeconds, Is.EqualTo(5));
            Assert.That(servers[0].Args, Is.EqualTo(new[] { "x" }));
            Assert.That(servers[1].TimeoutSeconds, Is.EqualTo(McpServerConfig.DefaultTimeoutSeconds));
        }

        [Test]
        public void BadJsonReportsReason()
        {
            File.WriteAllText(Path.Combine(_root, "servers.json"), "{oops");

            List<McpServerEntry> servers;
            string error;
            Assert.That(McpServerConfig.TryLoad(Path.Combine(_root, "servers.json"), out servers, out error), Is.False);
            Assert.That(error, Does.Contain("cannot read servers.json"));
        }
    }

    [TestFixture]
    public class McpManagerTests
    {
        private string _root;
        private FakeMcpServer _fake;
        private int _created;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "RimMcpMgr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _fake = new FakeMcpServer();
            _created = 0;
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

        private McpManager ManagerFor(string json)
        {
            string path = Path.Combine(_root, "servers.json");
            File.WriteAllText(path, json);
            return new McpManager(path, entry =>
            {
                _created++;
                _fake.Name = entry.Name;
                return _fake;
            });
        }

        [Test]
        public void StatusWithoutConfigExplains()
        {
            var manager = new McpManager(Path.Combine(_root, "nope.json"), entry => _fake);

            Assert.That(manager.Status(), Does.Contain("missing servers.json"));
        }

        [Test]
        public void StatusListsServerStates()
        {
            McpManager manager = ManagerFor("{\"servers\": [{\"name\": \"a\", \"command\": \"c\", \"enabled\": true}, {\"name\": \"b\", \"command\": \"c\"}]}");

            Assert.That(manager.Status(), Does.Contain("[a] idle").And.Contain("[b] disabled"));
        }

        [Test]
        public async Task ListToolsConnectsAndFormats()
        {
            _fake.Returns("fetch", "ok");
            McpManager manager = ManagerFor("{\"servers\": [{\"name\": \"web\", \"command\": \"c\", \"enabled\": true}]}");

            string text = await manager.ListToolsAsync(CancellationToken.None);

            Assert.That(text, Does.Contain("[web]").And.Contain("fetch"));
            Assert.That(_created, Is.EqualTo(1));
        }

        [Test]
        public async Task CallSuccessAndUnknownPaths()
        {
            _fake.Returns("fetch", "page text");
            McpManager manager = ManagerFor("{\"servers\": [{\"name\": \"web\", \"command\": \"c\", \"enabled\": true}, {\"name\": \"off\", \"command\": \"c\"}]}");

            Assert.That(await manager.CallAsync("web", "fetch", "{\"url\": \"x\"}", CancellationToken.None), Is.EqualTo("page text"));
            Assert.That(_fake.Calls.Single(), Does.Contain("fetch"));
            Assert.That(await manager.CallAsync("nope", "fetch", "{}", CancellationToken.None), Does.StartWith("Error:"));
            Assert.That(await manager.CallAsync("off", "fetch", "{}", CancellationToken.None), Does.StartWith("Error:").And.Contain("disabled"));
        }

        [Test]
        public async Task FailureDropsClientSoNextCallReconnects()
        {
            _fake.Returns("fetch", "ok");
            McpManager manager = ManagerFor("{\"servers\": [{\"name\": \"web\", \"command\": \"c\", \"enabled\": true}]}");
            _fake.FailList = true;

            string first = await manager.ListToolsAsync(CancellationToken.None);

            Assert.That(first, Does.Contain("failed"));
            Assert.That(_fake.Disposed, Is.True);
            _fake.FailList = false;
            string second = await manager.ListToolsAsync(CancellationToken.None);
            Assert.That(second, Does.Contain("fetch"));
            Assert.That(_created, Is.EqualTo(2));
        }

        [Test]
        public void ShutdownIsSafeWithoutConnections()
        {
            McpManager manager = ManagerFor("{\"servers\": []}");

            Assert.DoesNotThrow(() => manager.Shutdown());
            Assert.DoesNotThrow(() => manager.SweepIdle());
        }
    }

    [TestFixture]
    public class McpToolsTests
    {
        private FakeGameWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = new FakeGameWorld();
        }

        [Test]
        public void McpPermissionOffUntilGrantedButSettingsDefaultOn()
        {
            Assert.That(new ToolPermissions().Allows(ToolCategory.Mcp), Is.False);
            Assert.That(new RimAgentSettings().ToPermissions().Allows(ToolCategory.Mcp), Is.True, "設定頁預設全開");
        }

        [Test]
        public void RegistryGatesTheMcpToolsByPermission()
        {
            var registry = new AgentToolRegistry(_world, new AgentTranscript());

            Assert.That(registry.Select(ToolPermissions.For(ToolCategory.Mcp)).OfType<AIFunction>().Count(t => t.Name == "mcp_list_tools" || t.Name == "mcp_call_tool"), Is.EqualTo(2));
            Assert.That(registry.Select(new ToolPermissions()).OfType<AIFunction>().Any(t => t.Name == "mcp_list_tools"), Is.False);
        }

        [Test]
        public async Task ListWithoutConfigExplains()
        {
            var tools = new McpTools(new McpManager(null));

            string text = await tools.ListTools(CancellationToken.None);

            Assert.That(text, Does.Contain("missing servers.json"));
            Assert.That(text.IndexOf("missing servers.json", System.StringComparison.Ordinal), Is.EqualTo(text.LastIndexOf("missing servers.json", System.StringComparison.Ordinal)), "沒有設定時狀態只印一次");
        }
    }
}
