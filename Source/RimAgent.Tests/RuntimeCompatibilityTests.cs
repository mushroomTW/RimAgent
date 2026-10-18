using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests;

/// <summary>
/// 在 .NET 10 測試主機強制 JIT 每個方法，檢查包含 UI 在內的組件參考是否完整。
/// 遊戲原生呼叫與 Waylith 執行環境的行為另由 DevTools 整合測試驗證。
/// </summary>
[TestFixture]
public class RuntimeCompatibilityTests
{
    /// <summary>
    /// 直接碰 Unity InternalCall 的方法只能在遊戲內驗證，逐一登記：
    /// 新增這類方法時必須明確列入，不能靠數量門檻混過去。
    /// </summary>
    private static readonly HashSet<string> AllowedUnityNative =
    [
        "RimAgent.UI.Window_Agent.DrawBottomBar",          // 輸入列
        "RimAgent.UI.Window_Agent.DrawInputField",         // 輸入列
        "RimAgent.UI.Window_Agent.SetInput",               // 游標
        "RimAgent.UI.Window_Agent.HandleSlashKeys",        // 斜線選單鍵盤
        "RimAgent.UI.Window_Agent.HandleHistoryKeys",      // 輸入歷史鍵盤
        "RimAgent.UI.Window_Agent.DrawSelectableLine",     // 對話框選層
        "RimAgent.UI.Window_Agent.GetSelectableStyle",     // 對話框選層樣式
        "RimAgent.UI.Window_Agent.CopyToolHeaderOnRightClick", // 工具列右鍵複製
        "RimAgent.RimAgentMod.OpenFolder",                 // 設定頁開啟紀錄資料夾
        "RimAgent.Game.RimWorldGameWorld.TakeScreenshot",  // 截圖
        "RimAgent.UI.Window_Agent.LoadPendingScreenshot",  // 截圖預覽縮圖
        "RimAgent.UI.Window_Agent.DownscaleTexture"        // 截圖預覽縮圖繪製
    ];

    [Test]
    public void EveryMethodInRimAgentJitsOnDotNet10()
    {
        Assembly assembly = typeof(ColonyAgentSession).Assembly;
        var failures = new List<string>();
        int prepared = 0;

        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsGenericTypeDefinition) continue;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                    prepared++;
                }
                catch (System.Security.SecurityException)
                {
                    // 直接呼叫 Unity InternalCall（Event.current 等）的方法在標準測試主機上無法 JIT
                    // （「ECall 方法必須封裝在系統模組中」）；那是 Unity 原生實作，只能在遊戲內驗證。
                    string name = $"{type.FullName}.{method.Name}";
                    if (!AllowedUnityNative.Contains(name)) failures.Add(name + ": 未登記的 Unity InternalCall 方法");
                }
                catch (Exception ex)
                {
                    Exception root = ex is TypeInitializationException && ex.InnerException != null ? ex.InnerException : ex;
                    failures.Add($"{type.FullName}.{method.Name}: {root.GetType().Name}: {root.Message}");
                }
            }
        }

        Assert.That(prepared, Is.GreaterThan(100), "應該有掃到方法");
        Assert.That(failures, Is.Empty, string.Join("\n", failures));
    }
}
