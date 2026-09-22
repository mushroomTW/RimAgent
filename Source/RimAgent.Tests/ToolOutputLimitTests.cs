using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Game;
using RimAgent.Tools;

namespace RimAgent.Tests
{
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
            Assert.That(tool.Result.Length, Is.LessThan(ToolOutputLimit.MaxResultChars + 200));
            Assert.That(tool.Result, Does.Contain("characters omitted"));
        }
    }
}
