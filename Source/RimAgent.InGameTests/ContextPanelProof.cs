using System;
using System.Collections;
using System.IO;
using System.Linq;
using DevTools.Testing;
using RimAgent.UI;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

[TestFixture(TestType.Playing)]
[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
#pragma warning disable MA0036, S1118 // 類別不能改成 static，否則 DevTools 無法以公開建構式註冊
internal sealed class ContextPanelProof
{
    [Test]
    public static void TokenizerShipsWithTheFramework()
    {
        var tokenizers = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "Microsoft.ML.Tokenizers")
            .ToList();
        Expect.IsTrue(tokenizers.Count == 1, "Microsoft.ML.Tokenizers 應只載入一份，實際 " + tokenizers.Count);
        string location = tokenizers.Count == 1 ? tokenizers[0].Location : string.Empty;
        Expect.IsTrue(location.IndexOf("RimLLM Framework", StringComparison.OrdinalIgnoreCase) >= 0, "應由框架出貨：" + location);
    }

    [Test]
    public static IEnumerator ContextPanelOpensAndRenders()
    {
        AgentUiDriver.CloseAgentWindow();
        Window_Agent window = AgentUiDriver.OpenAgentWindow();
        Expect.IsTrue(window != null, "代理視窗應已開啟");
        Agent.ColonyAgentSession session = RimAgentGameComponent.Instance?.Session;
        Expect.IsTrue(session != null, "應有代理 session");
        if (window == null || session == null) yield break;

        Window_Agent.ContextPanel.ToggleContextPanel(session, new Vector2(window.windowRect.xMax, window.windowRect.y + 40f));
        yield return null;
        Expect.IsTrue(Find.WindowStack.WindowOfType<Window_Agent.ContextPanel>() != null, "上下文面板應已開啟");

        IEnumerator capture = AgentUiDriver.CaptureProof("context-panel", frames: 90);
        while (capture.MoveNext()) yield return capture.Current;
        Expect.IsTrue(File.Exists(Path.Combine(AgentUiDriver.ProofFolder, "context-panel.png")), "截圖應寫入");

        Find.WindowStack.WindowOfType<Window_Agent.ContextPanel>()?.Close(doCloseSound: false);
        AgentUiDriver.CloseAgentWindow();
    }
}
#pragma warning restore MA0036, S1118
