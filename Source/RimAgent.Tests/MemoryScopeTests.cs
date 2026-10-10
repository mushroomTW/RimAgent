using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests
{
    /// <summary>
    /// 記憶的兩個範圍：局內記憶跟著存檔序列化，以 global_ 開頭的全域記憶跨存檔、寫在磁碟檔裡。
    /// 模型只看到一組 file_memory_* 工具，範圍全由檔名決定。
    /// </summary>
    [TestFixture]
    public class MemoryScopeTests
    {
        private string _folder;
        private string _globalPath;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "RimAgentTests", Path.GetRandomFileName());
            _globalPath = Path.Combine(_folder, GlobalMemoryFile.FileName);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
                // 暫存資料夾清不掉不影響測試結果。
            }
        }

        [Test]
        public async Task GlobalFilesGoToTheGlobalScopeAndSurviveANewSave()
        {
            AgentMemoryStore save = new AgentMemoryStore();
            ScopedMemoryStore memory = NewMemory(save);

            await Invoke(memory, "file_memory_write", ("fileName", "global_preferences.md"), ("content", "Never sell components."), ("description", "Player rules"));
            await Invoke(memory, "file_memory_write", ("fileName", "base.md"), ("content", "Storage is near the river."), ("description", "Base layout"));

            Assert.That(File.Exists(_globalPath), Is.True, "全域記憶立刻寫進磁碟");
            Assert.That(save.Snapshot().Select(e => e.name), Does.Contain("base.md").And.Not.Contain("global_preferences.md"),
                "存檔只留局內記憶");

            // 新的一局：局內記憶空白，全域記憶仍看得到。
            ScopedMemoryStore nextGame = NewMemory(new AgentMemoryStore());

            Assert.That(nextGame.ReadAsync("global_preferences.md").GetAwaiter().GetResult(), Is.EqualTo("Never sell components."));
            Assert.That(nextGame.ReadAsync("base.md").GetAwaiter().GetResult(), Is.Null, "局內記憶不跨存檔");
            Assert.That(await Invoke(nextGame, "file_memory_ls"), Does.Contain("global_preferences.md").And.Not.Contain("base.md"));
        }

        [Test]
        public async Task IndexMergesBothScopesAndCarriesDescriptions()
        {
            AgentMemoryStore global = NewGlobalStore();
            ScopedMemoryStore memory = new ScopedMemoryStore(new AgentMemoryStore(), global);

            await Invoke(memory, "file_memory_write", ("fileName", "global_preferences.md"), ("content", "Never sell components."), ("description", "Player rules"));
            await Invoke(memory, "file_memory_write", ("fileName", "base.md"), ("content", "Storage is near the river."), ("description", "Base layout"));

            Assert.That(memory.Index, Does.Contain("global_preferences.md").And.Contain("Player rules"));
            Assert.That(memory.Index, Does.Contain("base.md").And.Contain("Base layout"));
            Assert.That(memory.Index, Does.Not.Contain("Never sell components."), "索引只列檔名與描述");
            Assert.That(global.Index, Is.Empty, "索引算局內，不佔全域儲存");
        }

        [Test]
        public async Task GrepSearchesBothScopes()
        {
            ScopedMemoryStore memory = NewMemory(new AgentMemoryStore());
            await Invoke(memory, "file_memory_write", ("fileName", "global_preferences.md"), ("content", "Never sell components."), ("description", "Player rules"));
            await Invoke(memory, "file_memory_write", ("fileName", "base.md"), ("content", "Storage is near the river."), ("description", "Base layout"));

            Assert.That(await Invoke(memory, "file_memory_grep", ("regexPattern", "components")), Does.Contain("global_preferences.md"));
            Assert.That(await Invoke(memory, "file_memory_grep", ("regexPattern", "river")), Does.Contain("base.md"));
        }

        [Test]
        public async Task DeletingAGlobalFileAlsoUpdatesTheDiskFile()
        {
            ScopedMemoryStore memory = NewMemory(new AgentMemoryStore());
            await Invoke(memory, "file_memory_write", ("fileName", "global_preferences.md"), ("content", "Never sell components."), ("description", "Player rules"));

            await Invoke(memory, "file_memory_delete", ("fileName", "global_preferences.md"));

            AgentMemoryStore reloaded = NewGlobalStore();
            Assert.That(reloaded.ReadAsync("global_preferences.md").GetAwaiter().GetResult(), Is.Null);
            Assert.That(reloaded.DescribedFiles(), Is.Empty);
        }

        [Test]
        public void FileLimitsAreCountedPerScope()
        {
            ScopedMemoryStore memory = NewMemory(new AgentMemoryStore());
            for (int i = 0; i < AgentMemoryStore.MaxFiles; i++)
            {
                memory.WriteAsync("global_note" + i + ".md", "value").GetAwaiter().GetResult();
            }

            Assert.ThrowsAsync<InvalidOperationException>(() => memory.WriteAsync("global_extra.md", "value"));
            Assert.DoesNotThrowAsync(() => memory.WriteAsync("extra.md", "value"), "局內記憶的名額不受全域記憶影響");
        }

        [Test]
        public void GlobalMemoryIsRoundTrippedThroughTheDiskFile()
        {
            AgentMemoryStore global = NewGlobalStore();
            global.WriteAsync("global_style.md", "Prefer wood over stone.").GetAwaiter().GetResult();
            global.WriteAsync("global_style_description.md", "Build style\nand rules").GetAwaiter().GetResult();
            GlobalMemoryFile.Save(_globalPath, global.Snapshot());

            AgentMemoryStore reloaded = GlobalMemoryFile.Load(_globalPath, out string warning);

            Assert.That(warning, Is.Null);
            Assert.That(reloaded.ReadAsync("global_style.md").GetAwaiter().GetResult(), Is.EqualTo("Prefer wood over stone."));
            Assert.That(reloaded.DescribedFiles().Single().Description, Is.EqualTo("Build style and rules"), "描述壓成單行");
        }

        [Test]
        public void UnreadableGlobalFileIsPreservedAndReported()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(_globalPath, "{ not json");

            AgentMemoryStore global = GlobalMemoryFile.Load(_globalPath, out string warning);

            Assert.That(warning, Does.Contain("could not be read"));
            Assert.That(global.DescribedFiles(), Is.Empty);
            Assert.That(File.Exists(_globalPath + GlobalMemoryFile.PreservedSuffix), Is.True, "原檔改名保留");
            Assert.That(File.Exists(_globalPath), Is.False);
        }

        [Test]
        public async Task ScopePrefixIsCaseInsensitive()
        {
            ScopedMemoryStore memory = NewMemory(new AgentMemoryStore());

            await Invoke(memory, "file_memory_write", ("fileName", "Global_Preferences.md"), ("content", "x"), ("description", "d"));

            Assert.That(memory.GlobalScope.DescribedFiles().Select(f => f.Name), Is.EqualTo(new[] { "Global_Preferences.md" }));
            Assert.That(memory.SaveScope.DescribedFiles(), Is.Empty);
        }

        [Test]
        public void IndexListsEveryFileWhenBothScopesAreFull()
        {
            var save = new AgentMemoryStore();
            var global = new AgentMemoryStore();
            for (int i = 0; i < AgentMemoryStore.MaxFiles; i++)
            {
                save.WriteAsync("note" + i.ToString("D2") + ".md", "value").GetAwaiter().GetResult();
                global.WriteAsync("global_note" + i.ToString("D2") + ".md", "value").GetAwaiter().GetResult();
            }

            // net472 沒有 Split(char, StringSplitOptions)（Mono 專屬），一律傳 char 陣列。
            string[] lines = new ScopedMemoryStore(save, global).Index.Split(new[] { '\n' });

            Assert.That(lines.Length, Is.EqualTo(AgentMemoryStore.MaxFiles * 2), "兩邊都滿也不會有檔案落在索引外");
            Assert.That(lines[0], Is.EqualTo("- **global_note00.md**"), "兩個範圍一起排序");
            Assert.That(lines[lines.Length - 1], Is.EqualTo("- **note29.md**"));
        }

        [Test]
        public void SavingOverAnExistingGlobalFileReplacesItWithoutLeavingATempFile()
        {
            GlobalMemoryFile.Save(_globalPath, new List<AgentMemoryEntry> { new AgentMemoryEntry { name = "global_a.md", content = "old" } });
            GlobalMemoryFile.Save(_globalPath, new List<AgentMemoryEntry> { new AgentMemoryEntry { name = "global_a.md", content = "new" } });

            AgentMemoryStore reloaded = GlobalMemoryFile.Load(_globalPath, out string warning);

            Assert.That(warning, Is.Null);
            Assert.That(reloaded.ReadAsync("global_a.md").GetAwaiter().GetResult(), Is.EqualTo("new"));
            Assert.That(File.Exists(_globalPath + ".tmp"), Is.False, "暫存檔換進正式檔後不留下");
        }

        [Test]
        public void GlobalFileNamesLeftInAnOldSaveAreHandedBackToGlobalMemory()
        {
            var entries = new List<AgentMemoryEntry>
            {
                new AgentMemoryEntry { name = "base.md", content = "a" },
                new AgentMemoryEntry { name = "global_preferences.md", content = "b" },
                new AgentMemoryEntry { name = "global_preferences_description.md", content = "c" }
            };

            List<AgentMemoryEntry> taken = MemoryScopes.TakeGlobalEntries(entries);

            Assert.That(taken.Select(e => e.name), Is.EqualTo(new[] { "global_preferences.md", "global_preferences_description.md" }));
            Assert.That(entries.Select(e => e.name), Is.EqualTo(new[] { "base.md" }));
        }

        [Test]
        public void DroppedCountsAreSummedAcrossBothScopes()
        {
            var saveEntries = new List<AgentMemoryEntry>();
            var globalEntries = new List<AgentMemoryEntry>();
            for (int i = 0; i < AgentMemoryStore.MaxFiles + 1; i++)
            {
                saveEntries.Add(new AgentMemoryEntry { name = "note" + i + ".md", content = "value" });
                globalEntries.Add(new AgentMemoryEntry { name = "global_note" + i + ".md", content = "value" });
            }

            var memory = new ScopedMemoryStore(new AgentMemoryStore(saveEntries), new AgentMemoryStore(globalEntries));

            Assert.That(memory.DroppedOnLoad, Is.EqualTo(2), "兩邊各丟一個，各自計數相加");
        }

        [Test]
        public void ScopeIsDecidedByFileName()
        {
            Assert.That(MemoryScopes.IsGlobal("global_preferences.md"), Is.True);
            Assert.That(MemoryScopes.IsGlobal("/Global_notes.md"), Is.True);
            Assert.That(MemoryScopes.IsGlobal("preferences.md"), Is.False);
            Assert.That(MemoryScopes.IsGlobal("my_global_notes.md"), Is.False);
            Assert.That(MemoryScopes.IsGlobal(null), Is.False);
        }

        [Test]
        public async Task SessionPromptListsGlobalMemoryEvenOnANewSave()
        {
            AgentMemoryStore global = NewGlobalStore();
            global.WriteAsync("global_preferences.md", "Never sell components.").GetAwaiter().GetResult();
            global.WriteAsync("global_preferences_description.md", "Player rules").GetAwaiter().GetResult();
            ScopedMemoryStore memory = new ScopedMemoryStore(new AgentMemoryStore(), global);
            var client = new ScriptedChatClient().Reply("done");

            var session = new ColonyAgentSession(client, new FakeGameWorld(),
                () => ToolPermissions.For(ToolCategory.Memory), 4, memory: memory);
            await session.SendAsync("what did I tell you about components?");

            string instructions = client.ReceivedOptions[0].Instructions;
            Assert.That(instructions, Does.Contain("global_preferences.md").And.Contain("Player rules"));
            Assert.That(instructions, Does.Contain("names starting with `global_` are shared by every save"));
        }

        /// <summary>一份新的合成儲存：全域記憶從磁碟檔讀，改動後寫回同一個檔案。</summary>
        private ScopedMemoryStore NewMemory(AgentMemoryStore save)
        {
            AgentMemoryStore global = NewGlobalStore();
            return new ScopedMemoryStore(save, global, () => GlobalMemoryFile.Save(_globalPath, global.Snapshot()));
        }

        private AgentMemoryStore NewGlobalStore()
        {
            return GlobalMemoryFile.Load(_globalPath, out _);
        }

        private static async Task<string> Invoke(AgentMemoryStore memory, string toolName, params (string Key, object Value)[] arguments)
        {
            var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript(), null,
                new ConfirmationGate(() => AgentMode.Autonomous), memory: memory);
            AIFunction tool = registry.All.OfType<AIFunction>().Single(t => t.Name == toolName);
            var args = new AIFunctionArguments(arguments.ToDictionary(a => a.Key, a => a.Value));
            return (await tool.InvokeAsync(args))?.ToString();
        }
    }
}
