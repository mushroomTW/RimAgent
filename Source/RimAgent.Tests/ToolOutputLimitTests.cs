using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>工具結果的硬上限：頭尾都留、中間省略並說明，且上限套用在真正送進模型的那份文字。</summary>
[TestFixture]
public class ToolOutputLimitTests
{
    [Test]
    public void ShortOutputIsUnchanged()
    {
        string text = "Set Ali's doctor priority 3 -> 1";

        Assert.That(ToolOutputLimit.Apply(text), Is.SameAs(text));
    }

    [Test]
    public void LongOutputKeepsHeadAndTailAndReportsTheOmission()
    {
        string text = "HEAD" + new string('x', 200) + "TAIL";

        string limited = ToolOutputLimit.Apply(text, 100);

        Assert.That(limited, Does.StartWith("HEAD"));
        Assert.That(limited, Does.EndWith("TAIL"));
        Assert.That(limited, Does.Contain("108 characters omitted"));
        Assert.That(limited.Length, Is.LessThan(text.Length));
    }

    [Test]
    public void TruncationNeverSplitsASurrogatePair()
    {
        // 每個 emoji 佔兩個 char，所有可能的切點都會落在代理對中間。
        string text = string.Concat(Enumerable.Repeat("\U0001F600", 200));

        for (int max = 2; max <= 60; max++)
        {
            string limited = ToolOutputLimit.Apply(text, max);

            for (int i = 0; i < limited.Length; i++)
            {
                if (char.IsHighSurrogate(limited[i]))
                {
                    Assert.That(i + 1 < limited.Length && char.IsLowSurrogate(limited[i + 1]), Is.True,
                        $"max={max} 位置 {i} 留下了沒有配對的高位字元");
                    i++;
                }
                else
                {
                    Assert.That(char.IsLowSurrogate(limited[i]), Is.False,
                        $"max={max} 位置 {i} 留下了沒有配對的低位字元");
                }
            }
        }
    }

    [Test]
    public void ErrorPrefixSurvivesTruncationSoFailuresAreStillDetected()
    {
        string text = ToolText.Error("something broke") + new string('x', 20000);

        string limited = ToolOutputLimit.Apply(text);

        Assert.That(limited, Does.StartWith(ToolText.ErrorPrefix));
    }

    /// <summary>整條路徑：觀察工具的巨量輸出送到模型前必須已經被夾住。</summary>
    [Test]
    public async Task HugeToolResultIsCappedBeforeItReachesTheModel()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        for (int i = 0; i < 200; i++)
        {
            world.Things.Add(FakeGameWorld.MakeThing(1000 + i, "MineableSteel",
                "compacted steel deposit with a deliberately long label for this test", ThingKind.Mineable, i % 50, i / 50));
        }
        var client = new ScriptedChatClient();
        client.CallTool("find_things", new { kind = "mineable", name = "", x1 = -1, z1 = -1, x2 = -1, z2 = -1, limit = 200 })
              .Reply("Done.");
        var session = new ColonyAgentSession(client, world, () => ToolPermissions.All, 8, () => AgentMode.Autonomous);

        await session.SendAsync("list everything mineable");

        TranscriptEntry tool = session.Transcript.Snapshot().Single(e => e.Kind == TranscriptKind.Tool);
        Assert.That(tool.Result.Length, Is.LessThan(ToolOutputLimit.MaxResultChars + 400));
        Assert.That(tool.Result, Does.Contain("characters omitted"));
    }

    [Test]
    public void LongOutputIsSavedUnderAnIdThatPointsAtTheOmittedPart()
    {
        var store = new ToolOutputStore();
        string text = "HEAD" + new string('x', 200) + "MIDDLE" + new string('y', 200) + "TAIL";

        string limited = ToolOutputLimit.Apply(text, 100, store);

        Assert.That(limited, Does.StartWith("HEAD").And.EndWith("TAIL"));
        Assert.That(limited, Does.Contain("out_1").And.Contain("offset 50"));
        Assert.That(store.TryGet("out_1", out string saved), Is.True);
        Assert.That(saved, Is.EqualTo(text));
    }

    [Test]
    public void ShortOutputIsNotSaved()
    {
        var store = new ToolOutputStore();

        ToolOutputLimit.Apply("short", 100, store);

        Assert.That(store.TryGet("out_1", out _), Is.False);
    }

    [Test]
    public void StoreKeepsOnlyTheMostRecentOutputs()
    {
        var store = new ToolOutputStore();
        for (int i = 0; i < ToolOutputStore.Capacity + 1; i++) store.Save("output " + i);

        Assert.That(store.TryGet("out_1", out _), Is.False, "最舊的被淘汰");
        Assert.That(store.TryGet("out_" + (ToolOutputStore.Capacity + 1), out string last), Is.True);
        Assert.That(last, Is.EqualTo("output " + ToolOutputStore.Capacity));
    }

    [Test]
    public void StoreEvictsOldestOutputsWhenTotalSizeExceedsTheCap()
    {
        var store = new ToolOutputStore();
        string first = store.Save(new string('a', ToolOutputStore.MaxTotalChars * 3 / 5));
        string second = store.Save(new string('b', ToolOutputStore.MaxTotalChars * 3 / 5));

        Assert.That(store.TryGet(first, out _), Is.False);
        Assert.That(store.TryGet(second, out _), Is.True, "最新的一定留著");
    }

    [Test]
    public void OversizedOutputIsSavedOnlyUpToTheCap()
    {
        var store = new ToolOutputStore();
        string id = store.Save(new string('a', ToolOutputStore.MaxTotalChars + 10));

        Assert.That(store.TryGet(id, out string saved), Is.True);
        Assert.That(saved.Length, Is.LessThan(ToolOutputStore.MaxTotalChars + 200));
        Assert.That(saved, Does.EndWith("characters were kept]"));
    }

    [Test]
    public void SubagentWithoutObserveStillGetsTheOutputReaderButNotAlone()
    {
        var registry = new AgentToolRegistry(new FakeGameWorld(), new AgentTranscript());

        var builder = registry.SelectForSubagent(ToolPermissions.All, AgentMode.Autonomous, new[] { ToolCategory.Build }, readOnly: false).Select(t => t.Name).ToList();
        var readOnlyCombat = registry.SelectForSubagent(ToolPermissions.All, AgentMode.Autonomous, new[] { ToolCategory.Combat }, readOnly: true);

        Assert.That(builder, Does.Contain("place_blueprint").And.Contain(ToolOutputTools.ToolName));
        Assert.That(readOnlyCombat, Is.Empty, "沒有其他可用工具時不補讀取器");
    }

    [Test]
    public void ReadToolOutputReturnsASliceFromTheOffsetAndSaysWhereToContinue()
    {
        var store = new ToolOutputStore();
        string id = store.Save("0123456789" + new string('z', ToolOutputTools.SliceChars * 2));
        var tools = new ToolOutputTools(store);

        string first = tools.ReadToolOutput(id, 5, string.Empty);
        string beyond = tools.ReadToolOutput(id, 1000000, string.Empty);

        Assert.That(first, Does.Contain("56789"));
        Assert.That(first, Does.Contain("offset " + (5 + ToolOutputTools.SliceChars)));
        Assert.That(beyond, Does.Contain("end of output"));
    }

    [Test]
    public void ReadToolOutputFindsMatchingLines()
    {
        var store = new ToolOutputStore();
        string id = store.Save("steel at (1, 2)\nwood at (3, 4)\nSTEEL at (5, 6)");

        string found = new ToolOutputTools(store).ReadToolOutput(id, 0, "steel");

        Assert.That(found, Does.Contain("(1, 2)").And.Contain("(5, 6)").And.Not.Contain("wood"));
    }

    [Test]
    public void UnknownIdTellsTheModelToRerunTheTool()
    {
        string result = new ToolOutputTools(new ToolOutputStore()).ReadToolOutput("out_9", 0, string.Empty);

        Assert.That(result, Does.StartWith(ToolText.ErrorPrefix).And.Contain("again"));
    }

    /// <summary>整條路徑：模型看到代號後能讀回被省略的中段；清除對話後代號失效。</summary>
    [Test]
    public async Task ModelCanReadTheOmittedMiddleOfAHugeResultUntilTheConversationIsCleared()
    {
        var world = new FakeGameWorld();
        world.Colonists.Add(FakeGameWorld.MakeColonist(101, "Alice", "Ali", "Smith"));
        for (int i = 0; i < 200; i++)
        {
            world.Things.Add(FakeGameWorld.MakeThing(1000 + i, "MineableSteel",
                "compacted steel deposit with a deliberately long label for this test", ThingKind.Mineable, i % 50, i / 50));
        }
        var client = new ScriptedChatClient();
        client.CallTool("find_things", new { kind = "mineable", name = "", x1 = -1, z1 = -1, x2 = -1, z2 = -1, limit = 200 })
              .CallTool("read_tool_output", new { id = "out_1", offset = 0, find = "(25,1)" })
              .Reply("Done.")
              .CallTool("read_tool_output", new { id = "out_1", offset = 0, find = "" })
              .Reply("Gone.");
        var session = new ColonyAgentSession(client, world, () => ToolPermissions.All, 8, () => AgentMode.Autonomous);

        await session.SendAsync("list everything mineable");
        session.Clear();
        await session.SendAsync("read it again");

        TranscriptEntry[] reads = [.. session.Transcript.Snapshot().Where(e => e.ToolName == "read_tool_output")];
        Assert.That(reads, Has.Length.EqualTo(1), "清除對話也清掉行動紀錄，只剩清除後那次");
        Assert.That(reads[0].Failed, Is.True, "清除對話後代號失效");
        ChatMessage firstRead = client.ReceivedMessages[2].Last();
        Assert.That(firstRead.Contents.OfType<FunctionResultContent>().Single().Result?.ToString(), Does.Contain("(25,1)"));
    }
}
