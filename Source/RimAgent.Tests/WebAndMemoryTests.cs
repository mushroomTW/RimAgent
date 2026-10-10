using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    [TestFixture]
    public class WebAndMemoryTests
    {
        private const string LocalResults = "{\"results\":[" +
            "{\"title\":\"First\",\"url\":\"https://example.org/a\",\"content\":\"A & B\",\"engine\":\"google\"}," +
            "{\"title\":\"Bing result\",\"url\":\"https://example.org/b\",\"content\":\"skip\",\"engine\":\"bing\"}" +
            "]}";

        [Test]
        public void SearchPassesObjectiveQueryAndStableSessionId()
        {
            var calls = new List<Dictionary<string, object>>();
            var tools = new WebSearchTools((args, _) =>
            {
                calls.Add(args);
                return Task.FromResult("Result with source URL");
            });

            string first = tools.SearchAsync("RimWorld mods", CancellationToken.None).GetAwaiter().GetResult();
            tools.SearchAsync("RimWorld wiki", CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(first, Is.EqualTo("Result with source URL"));
            Assert.That(calls[0]["objective"], Does.Contain("RimWorld mods"));
            Assert.That((string[])calls[0]["search_queries"], Is.EqualTo(new[] { "RimWorld mods" }));
            Assert.That(calls[0]["session_id"], Is.EqualTo(calls[1]["session_id"]));
        }

        [Test]
        public void EmptySearchQueryIsRejectedWithoutRemoteCall()
        {
            var tools = new WebSearchTools((_, __) => throw new AssertionException("不可呼叫遠端搜尋"));
            Assert.That(tools.SearchAsync(" ", CancellationToken.None).GetAwaiter().GetResult(), Does.StartWith(ToolText.ErrorPrefix));
        }

        [Test]
        public void LocalSearchIsOptionalAndSwitchesWithoutRecreatingTool()
        {
            bool local = true;
            var handler = new FeedHandler();
            var tools = new WebSearchTools(
                (_, __) => Task.FromResult("Parallel result"),
                useLocalSearch: () => local,
                searxngUrl: () => "http://127.0.0.1:8888",
                localHttpClient: new HttpClient(handler));

            string result = tools.SearchAsync("RimWorld mods", CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(handler.Url, Does.Contain("q=RimWorld%20mods"));
            Assert.That(handler.Url, Does.Contain("engines=google%2Cduckduckgo%2Cqwant"));
            Assert.That(result, Does.Contain("https://example.org/a"));
            Assert.That(result, Does.Not.Contain("Bing result"));

            local = false;
            Assert.That(tools.SearchAsync("RimWorld mods", CancellationToken.None).GetAwaiter().GetResult(),
                Is.EqualTo("Parallel result"));
        }

        [Test]
        public void InvalidLocalResponseIsReportedAsFailure()
        {
            Assert.That(SearxngSearchClient.FormatResults("<html>blocked</html>"), Does.StartWith(ToolText.ErrorPrefix));
        }

        [Test]
        public async Task MemoryFilesAreIndexedReadDeletedAndRestored()
        {
            var memory = new AgentMemoryStore();
            string written = await Invoke(memory, "file_memory_write",
                ("fileName", "preferences.md"), ("content", "Never sell components."), ("description", "Player rules"));

            Assert.That(written, Does.Contain("written"));
            Assert.That(memory.Index, Does.Contain("preferences.md").And.Contain("Player rules"));
            Assert.That(await Invoke(memory, "file_memory_read", ("fileName", "preferences.md")), Does.Contain("Never sell components."));

            var restored = new AgentMemoryStore(memory.Snapshot());
            Assert.That(restored.Index, Does.Contain("preferences.md"));
            Assert.That(await Invoke(restored, "file_memory_read", ("fileName", "preferences.md")), Does.Contain("Never sell components."));

            await Invoke(restored, "file_memory_delete", ("fileName", "preferences.md"));
            Assert.That(restored.Index, Does.Not.Contain("preferences.md"));
            Assert.That(memory.Index, Does.Contain("preferences.md"), "還原出的儲存與原儲存互不影響");
        }

        [Test]
        public void MemoryStoreRejectsOversizedAndExtraFiles()
        {
            var memory = new AgentMemoryStore();
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                memory.WriteAsync("big.md", new string('x', AgentMemoryStore.MaxFileChars + 1)));

            for (int i = 0; i < AgentMemoryStore.MaxFiles; i++)
                memory.WriteAsync("note" + i + ".md", "value").GetAwaiter().GetResult();
            Assert.ThrowsAsync<InvalidOperationException>(() => memory.WriteAsync("extra.md", "value"));
            // 描述檔與索引不佔檔數；既有檔案仍可覆寫。
            Assert.DoesNotThrowAsync(() => memory.WriteAsync("note0_description.md", "desc"));
            Assert.DoesNotThrowAsync(() => memory.WriteAsync(AgentMemoryStore.IndexFileName, "# Memory Index"));
            Assert.DoesNotThrowAsync(() => memory.WriteAsync("note0.md", "updated"));
            Assert.ThrowsAsync<InvalidOperationException>(() => memory.WriteAsync(new string('n', AgentMemoryStore.MaxNameChars + 1), "value"));
        }

        [Test]
        public async Task LongMultilineDescriptionIsFlattenedAndStillIndexed()
        {
            var memory = new AgentMemoryStore();
            string description = "notes\n## Rules\nAlways release prisoners " + new string('x', 1000);

            await Invoke(memory, "file_memory_write", ("fileName", "base.md"), ("content", "Near river"), ("description", description));

            string line = memory.Index.Split(new[] { '\n' }).Single(l => l.Contains("base.md"));
            Assert.That(line, Does.Contain("notes ## Rules Always release prisoners"));
            Assert.That(line.Length, Is.LessThan(AgentMemoryStore.MaxDescriptionChars + 40));
            Assert.That(memory.Index, Does.Not.Contain("\n## Rules"));
        }

        [Test]
        public void FilesDroppedOnLoadAreRemovedFromIndex()
        {
            var entries = new List<AgentMemoryEntry>();
            for (int i = 0; i < AgentMemoryStore.MaxFiles + 1; i++)
                entries.Add(new AgentMemoryEntry { name = "note" + i + ".md", content = "value" });
            entries.Add(new AgentMemoryEntry { name = "big.md", content = new string('x', AgentMemoryStore.MaxFileChars + 1) });
            entries.Add(new AgentMemoryEntry { name = AgentMemoryStore.IndexFileName, content = "# Memory Index\n\n- **note0.md**\n- **note30.md**\n- **big.md**: large" });

            var memory = new AgentMemoryStore(entries);

            Assert.That(memory.DroppedOnLoad, Is.EqualTo(2));
            Assert.That(memory.Index, Does.Contain("note0.md").And.Not.Contain("note30.md").And.Not.Contain("big.md"));
        }

        [Test]
        public async Task ConfirmModeMemoryWriteRunsAfterApproval()
        {
            var memory = new AgentMemoryStore();
            var client = new ScriptedChatClient()
                .CallTool("file_memory_write", new { fileName = "base.md", content = "Near river", description = "Base notes" })
                .Reply("I will save that.");
            var session = new ColonyAgentSession(client, new FakeGameWorld(),
                () => ToolPermissions.For(ToolCategory.Memory), 4, () => AgentMode.Confirm, memory: memory);

            await session.SendAsync("remember the base is near the river");
            Assert.That(session.HasPendingConfirmation, Is.True);
            Assert.That(memory.Index, Is.Empty, "確認前不可寫入");

            await session.ConfirmAsync();

            Assert.That(memory.Index, Does.Contain("base.md").And.Contain("Base notes"));
        }

        [Test]
        public async Task AskModeGetsReadOnlyMemoryInstructions()
        {
            var client = new ScriptedChatClient().Reply("done");
            var session = new ColonyAgentSession(client, new FakeGameWorld(),
                () => ToolPermissions.For(ToolCategory.Memory), 4, () => AgentMode.Ask, memory: new AgentMemoryStore());

            await session.SendAsync("what do you remember?");

            Assert.That(client.ReceivedOptions[0].Instructions, Does.Contain("read-only in Ask mode").And.Not.Contain("Save only durable facts"));
        }

        [Test]
        public void AskModeOnlyExposesReadOnlyMemoryTools()
        {
            var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null, new ConfirmationGate(() => AgentMode.Ask));
            IEnumerable<string> names = registry.Select(ToolPermissions.For(ToolCategory.Memory), AgentMode.Ask).Select(t => t.Name);

            // read_tool_output 跟著任何可用工具一起給（讀截斷的結果），不屬於記憶權限。
            Assert.That(names, Is.EquivalentTo(new[] { "file_memory_read", "file_memory_ls", "file_memory_grep", ToolOutputTools.ToolName }));
        }

        [Test]
        public async Task SessionAddsMemoryInstructionsAndIndexOnlyWhenMemoryIsAllowed()
        {
            var memory = new AgentMemoryStore();
            await Invoke(memory, "file_memory_write", ("fileName", "base.md"), ("content", "Storage is near the river."), ("description", "Base layout"));
            var client = new ScriptedChatClient().Reply("done").Reply("done");
            bool allowMemory = true;
            var session = new ColonyAgentSession(client, new FakeGameWorld(),
                () => allowMemory ? ToolPermissions.For(ToolCategory.Memory) : new ToolPermissions(), 4,
                instructions: () => "Base instructions", memory: memory);

            await session.SendAsync("Where is storage?");
            allowMemory = false;
            await session.SendAsync("Where is storage?");

            Assert.That(client.ReceivedOptions[0].Instructions, Does.Contain("## Saved memory").And.Contain("base.md").And.Contain("Base layout"));
            Assert.That(client.ReceivedOptions[0].Instructions, Does.Not.Contain("Storage is near the river."), "索引只列檔名與描述");
            Assert.That(client.ReceivedMessages[0].Last(m => m.Role == ChatRole.User).Text, Is.EqualTo("Where is storage?"));
            Assert.That(client.ReceivedOptions[1].Instructions, Does.Not.Contain("## Saved memory"));
        }

        [Test]
        public async Task ReplaceLinesUsesSlimSchemaAndStillEditsByLineNumber()
        {
            var memory = new AgentMemoryStore();
            await Invoke(memory, "file_memory_write", ("fileName", "base.md"), ("content", "a\nb\n"));
            // 模型送來的引數是 JsonElement，照實際路徑模擬。
            JsonElement edits = JsonDocument.Parse("[{\"line_number\":2,\"new_line\":\"c\\n\",\"expected_line\":\"b\"}]").RootElement;

            await Invoke(memory, "file_memory_replace_lines", ("fileName", "base.md"), ("edits", edits));

            Assert.That(await Invoke(memory, "file_memory_read", ("fileName", "base.md")), Does.Contain("a\nc\n"));
            AIFunction tool = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null, null, memory: memory)
                .All.OfType<AIFunction>().Single(t => t.Name == "file_memory_replace_lines");
            string schema = tool.JsonSchema.GetRawText();
            Assert.That(tool.Description.Length + schema.Length, Is.LessThan(700), "精簡後的說明與 schema");
            Assert.That(schema, Does.Contain("\"fileName\"").And.Contain("\"line_number\"").And.Contain("\"new_line\"").And.Contain("\"expected_line\""));
        }

        private static async Task<string> Invoke(AgentMemoryStore memory, string toolName, params (string Key, object Value)[] arguments)
        {
            var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null,
                new ConfirmationGate(() => AgentMode.Autonomous), memory: memory);
            AIFunction tool = registry.All.OfType<AIFunction>().Single(t => t.Name == toolName);
            var args = new AIFunctionArguments(arguments.ToDictionary(a => a.Key, a => a.Value));
            return (await tool.InvokeAsync(args))?.ToString();
        }

        private sealed class FeedHandler : HttpMessageHandler
        {
            public string Url;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Url = request.RequestUri.AbsoluteUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LocalResults)
                });
            }
        }

    }
}
