using System.Collections;
using System.IO;
using DevTools.Testing;
using RimAgent.UI;
using RimAgent.Agent;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// 遊戲內 UI 驅動：開關代理視窗並截圖存證，讓驗證介面改動時不必每次重新摸索怎麼操作遊戲。
/// 截圖存到 <see cref="ProofFolder"/>，用 Read 開 PNG 檢查畫面（純文字檢查看不出版面問題）。
/// 功能與驗證方式的對照見 Documentation/feature-map.md。
/// </summary>
internal static class AgentUiDriver
{
    /// <summary>截圖在影格結束才寫入，最多等這麼多格。</summary>
    private const int ScreenshotTimeoutFrames = 120;

    public static string ProofFolder => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimAgentProof");

    public static Window_Agent OpenAgentWindow()
    {
        Window_Agent window = Find.WindowStack.WindowOfType<Window_Agent>();
        if (window == null || !Window_Agent.IsOpenNow)
        {
            Window_Agent.Toggle();
            window = Find.WindowStack.WindowOfType<Window_Agent>();
        }
        return window;
    }

    public static void CloseAgentWindow()
    {
        Find.WindowStack.WindowOfType<Window_Agent>()?.Close(doCloseSound: false);
    }

    /// <summary>等 <paramref name="frames"/> 格讓視窗完成繪製，再截全畫面存成 <c>ProofFolder/name.png</c>。</summary>
    public static IEnumerator CaptureProof(string name, int frames = 3)
    {
        for (int i = 0; i < frames; i++) yield return null;

        Directory.CreateDirectory(ProofFolder);
        string path = Path.Combine(ProofFolder, name + ".png");
        if (File.Exists(path)) File.Delete(path);
        ScreenCapture.CaptureScreenshot(path);
        // 檔案先建立、之後的影格才寫完；連續兩格大小不變且非零才算寫好。
        long lastLength = -1;
        bool complete = false;
        for (int i = 0; i < ScreenshotTimeoutFrames && !complete; i++)
        {
            yield return null;
            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            complete = length > 0 && length == lastLength;
            lastLength = length;
        }

        if (complete) Log.Message(TestHelpers.LogPrefix + "proof screenshot " + path);
        else Log.Error(TestHelpers.LogPrefix + "proof screenshot timed out: " + path);
    }
}

/// <summary>驅動本身的冒煙測試：視窗能開、能截圖、能關。</summary>
[TestFixture(TestType.Playing)]
[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
#pragma warning disable MA0036, S1118 // 類別不能改成 static，否則 DevTools 無法以公開建構式註冊
internal sealed class AgentUiDriverTests
{
    [Test]
    public static IEnumerator BenchmarkProgressRenders()
    {
        Window_BenchmarkProgress.Show(BenchmarkComponent.SampleRows());
        IEnumerator capture = AgentUiDriver.CaptureProof("benchmark-progress");
        while (capture.MoveNext()) yield return capture.Current;
        Expect.IsTrue(Find.WindowStack.IsOpen<Window_BenchmarkProgress>(), "進度視窗應已開啟");
        Find.WindowStack.TryRemove(typeof(Window_BenchmarkProgress), doCloseSound: false);
    }

    [Test]
    public static IEnumerator BenchmarkStartRenders()
    {
        bool hadScore = BenchmarkComponent.Scores.Remove((null, ReasoningLevel.Medium), out BenchmarkScore saved);
        BenchmarkComponent.Scores[(null, ReasoningLevel.Medium)] = new BenchmarkScore(12, 14, 88, 9, 1860f, 868_926, 35_417, "sample", 0, 0);
        Find.WindowStack.Add(new Window_BenchmarkStart(tier: null));
        IEnumerator capture = AgentUiDriver.CaptureProof("benchmark-start");
        while (capture.MoveNext()) yield return capture.Current;
        Expect.IsTrue(Find.WindowStack.IsOpen<Window_BenchmarkStart>(), "開跑視窗應已開啟");
        Find.WindowStack.TryRemove(typeof(Window_BenchmarkStart), doCloseSound: false);
        BenchmarkComponent.Scores.Remove((null, ReasoningLevel.Medium));
        if (hadScore) BenchmarkComponent.Scores[(null, ReasoningLevel.Medium)] = saved;
    }

    [Test]
    public static IEnumerator AgentWindowOpensRendersAndCloses()
    {
        AgentUiDriver.CloseAgentWindow();
        Window_Agent window = AgentUiDriver.OpenAgentWindow();
        Expect.IsTrue(window != null && Window_Agent.IsOpenNow, "代理視窗應已開啟");

        IEnumerator capture = AgentUiDriver.CaptureProof("agent-window");
        while (capture.MoveNext()) yield return capture.Current;

        string path = Path.Combine(AgentUiDriver.ProofFolder, "agent-window.png");
        Expect.IsTrue(File.Exists(path) && new FileInfo(path).Length > 0, "截圖應寫入 " + path);

        AgentUiDriver.CloseAgentWindow();
        yield return null;
        Expect.IsFalse(Window_Agent.IsOpenNow, "代理視窗應已關閉");
    }
}
#pragma warning restore MA0036, S1118
