using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using RimAgent.Agent;
using RimAgent.Tools;

namespace RimAgent.Tests;

/// <summary>截圖附圖：只附本輪最新一張、插在工具訊息之後、未寫完會重試、關閉時原樣送出。</summary>
[TestFixture]
public class ImageAttachingTests
{
    private const string ShotPath = "C:/RimWorld/Screenshots/base.png";
    private static readonly byte[] Image = [1, 2, 3];

    private static List<ChatMessage> TurnWithScreenshot(string result = null)
    {
        return
        [
            new ChatMessage(ChatRole.System, "system"),
            new ChatMessage(ChatRole.User, "look at the base"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "take_screenshot"), new FunctionCallContent("c2", "list_zones")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", result ?? $"Screenshot saved: {ShotPath} (1920x1080)")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c2", "There are no zones on the map.")])
        ];
    }

    private static ImageAttachingChatClient Create(bool enabled, System.Func<string, Task<byte[]>> load)
    {
        return new ImageAttachingChatClient(new ScriptedChatClient(), () => enabled, load);
    }

    [Test]
    public async Task AttachesImageAfterTheToolMessagesOfTheCurrentTurn()
    {
        var client = Create(true, path => Task.FromResult(path == ShotPath ? Image : null));

        List<ChatMessage> sent = [.. await client.AttachAsync(TurnWithScreenshot(), CancellationToken.None)];

        Assert.That(sent, Has.Count.EqualTo(6));
        ChatMessage attached = sent[5];
        Assert.That(attached.Role, Is.EqualTo(ChatRole.User), "工具訊息之後另起一則使用者訊息，不拆開同一批工具結果");
        DataContent data = attached.Contents.OfType<DataContent>().Single();
        Assert.That(data.MediaType, Is.EqualTo("image/jpeg"));
        Assert.That(data.Data.ToArray(), Is.EqualTo(Image));
        Assert.That(attached.Text, Does.Contain("base.png"));
    }

    [Test]
    public async Task DisabledSendsMessagesUnchanged()
    {
        int loads = 0;
        var client = Create(false, _ => { loads++; return Task.FromResult(Image); });
        List<ChatMessage> messages = TurnWithScreenshot();

        IEnumerable<ChatMessage> sent = await client.AttachAsync(messages, CancellationToken.None);

        Assert.That(sent, Is.SameAs(messages));
        Assert.That(loads, Is.Zero);
    }

    [Test]
    public async Task ScreenshotsFromEarlierTurnsAreNotResent()
    {
        var client = Create(true, _ => Task.FromResult(Image));
        List<ChatMessage> messages = TurnWithScreenshot();
        messages.Add(new ChatMessage(ChatRole.Assistant, "done"));
        messages.Add(new ChatMessage(ChatRole.User, "next instruction"));

        IEnumerable<ChatMessage> sent = await client.AttachAsync(messages, CancellationToken.None);

        Assert.That(sent, Is.SameAs(messages), "下一句指令之後不再付圖片的 token");
    }

    [Test]
    public async Task FailedScreenshotResultIsIgnored()
    {
        var client = Create(true, _ => Task.FromResult(Image));
        List<ChatMessage> messages = TurnWithScreenshot("Error: failed to take screenshot.");

        Assert.That(await client.AttachAsync(messages, CancellationToken.None), Is.SameAs(messages));
    }

    [Test]
    public async Task RetriesUntilTheFileIsWrittenThenCaches()
    {
        int loads = 0;
        var client = Create(true, _ => Task.FromResult(++loads < 3 ? null : Image));

        List<ChatMessage> first = [.. await client.AttachAsync(TurnWithScreenshot(), CancellationToken.None)];
        List<ChatMessage> second = [.. await client.AttachAsync(TurnWithScreenshot(), CancellationToken.None)];

        Assert.That(first, Has.Count.EqualTo(6));
        Assert.That(second, Has.Count.EqualTo(6));
        Assert.That(loads, Is.EqualTo(3), "截圖在該幀結束才寫出，讀到後同一張不再重讀");
    }

    [Test]
    public async Task PlayerImagesAttachToTheCurrentInstructionWithoutTouchingHistory()
    {
        var client = Create(true, _ => Task.FromResult<byte[]>(null));
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.User, "old instruction"),
            new ChatMessage(ChatRole.Assistant, "ok"),
            new ChatMessage(ChatRole.User, "what is in this picture?")
        ];
        client.SetPlayerImages([Image, Image]);

        List<ChatMessage> sent = [.. await client.AttachAsync(messages, CancellationToken.None)];

        Assert.That(sent[2].Text, Is.EqualTo("what is in this picture?"));
        Assert.That(sent[2].Contents.OfType<DataContent>().Count(), Is.EqualTo(2));
        Assert.That(sent[0].Contents.OfType<DataContent>().Any(), Is.False, "只附在本句指令");
        Assert.That(messages[2].Contents.OfType<DataContent>().Any(), Is.False, "對話歷史裡的原訊息不能被改");

        client.SetPlayerImages(null);
        Assert.That(await client.AttachAsync(messages, CancellationToken.None), Is.SameAs(messages));
    }

    [Test]
    public async Task PlayerImagesAndScreenshotCanBothBeAttached()
    {
        var client = Create(true, _ => Task.FromResult(Image));
        client.SetPlayerImages([Image]);

        List<ChatMessage> sent = [.. await client.AttachAsync(TurnWithScreenshot(), CancellationToken.None)];

        Assert.That(sent, Has.Count.EqualTo(6));
        Assert.That(sent[1].Contents.OfType<DataContent>().Count(), Is.EqualTo(1));
        Assert.That(sent[5].Contents.OfType<DataContent>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task PlayerImagesAreNotSentWhenImageInputIsOff()
    {
        var client = Create(false, _ => Task.FromResult(Image));
        client.SetPlayerImages([Image]);
        List<ChatMessage> messages = [new ChatMessage(ChatRole.User, "hi")];

        Assert.That(await client.AttachAsync(messages, CancellationToken.None), Is.SameAs(messages));
    }

    [Test]
    public async Task InnerClientReceivesTheAttachedImage()
    {
        var inner = new ScriptedChatClient().Reply("ok");
        var client = new ImageAttachingChatClient(inner, () => true, _ => Task.FromResult(Image));

        await client.GetResponseAsync(TurnWithScreenshot());

        Assert.That(inner.ReceivedMessages[0].Last().Contents.OfType<DataContent>().Any(), Is.True);
    }

    [Test]
    public async Task ScreenshotTakenInsideRunScriptIsAttached()
    {
        var client = Create(true, path => Task.FromResult(path == ShotPath ? Image : null));
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.User, "pause and look"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("s1", "run_script")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("s1",
                "1. set_time_speed(speed=Paused): Paused.\n" +
                $"2. take_screenshot(): Screenshot saved: {ShotPath} (1920x1080)\n" +
                "Ran 2 of 2 calls: 2 ok, 0 failed.")])
        ];

        List<ChatMessage> sent = [.. await client.AttachAsync(messages, CancellationToken.None)];

        Assert.That(sent, Has.Count.EqualTo(4));
        Assert.That(sent[3].Contents.OfType<DataContent>().Single().Data.ToArray(), Is.EqualTo(Image));
    }

    [Test]
    public void ScriptResultWithoutScreenshotHasNoPath()
    {
        Assert.That(RimAgent.UI.TranscriptScreenshot.TryParseScriptSavedPath("1. list_zones(): Screenshot saved: x.png (1x1)"), Is.Null,
            "只認 take_screenshot 那一步");
        Assert.That(RimAgent.UI.TranscriptScreenshot.TryParseScriptSavedPath(
            $"1. take_screenshot(name=a): Screenshot saved: a.png (2x2)\n2. take_screenshot(): Screenshot saved: {ShotPath} (2x2)"),
            Is.EqualTo(ShotPath), "取最後一張");
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void FrameworkVisionSupportDecidesImageInput(bool supported, bool expected)
    {
        string asked = null;
        var session = new ColonyAgentSession(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 8,
            visionLookup: model => { asked = model; return supported; }) { PreferredModel = "Test:vision" };

        Assert.That(session.ImageInputEnabled, Is.EqualTo(expected));
        Assert.That(asked, Is.EqualTo("Test:vision"), "問的是目前主模型");
    }

    [Test]
    public void UnknownVisionSupportFallsBackToTheManualSetting()
    {
        // 單元測試沒有載入 mod 設定，手動設定視為關閉。
        var session = new ColonyAgentSession(new ScriptedChatClient(), new FakeGameWorld(), () => ToolPermissions.All, 8,
            visionLookup: _ => null) { PreferredModel = "Test:unknown" };

        Assert.That(session.ImageInputEnabled, Is.False);
    }
}
