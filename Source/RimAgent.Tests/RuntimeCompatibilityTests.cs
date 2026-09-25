using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using RimAgent.Agent;

namespace RimAgent.Tests
{
    /// <summary>
    /// RimAgent.dll 對 Krafs 的 Mono mscorlib 編譯，測試卻跑在真 .NET Framework 4.7.2 上；
    /// 兩邊的 BCL 佈局不同（例如 Mono 把 Queue&lt;T&gt; 放在 mscorlib、無參數 TrimEnd() 只有 Mono 有）。
    /// 強制 JIT 每個方法，讓 UI 這類測試碰不到的路徑也不能藏著不相容的參考。
    /// </summary>
    [TestFixture]
    public class RuntimeCompatibilityTests
    {
        /// <summary>
        /// 直接碰 Unity InternalCall 的方法只能在遊戲內驗證，逐一登記：
        /// 新增這類方法時必須明確列入，不能靠數量門檻混過去。
        /// </summary>
        private static readonly HashSet<string> AllowedUnityNative = new HashSet<string>
        {
            "RimAgent.UI.Window_Agent.DrawBottomBar",          // 輸入列
            "RimAgent.UI.Window_Agent.DrawInputField",         // 輸入列
            "RimAgent.UI.Window_Agent.SetInput",               // 游標
            "RimAgent.UI.Window_Agent.HandleSlashKeys",        // 斜線選單鍵盤
            "RimAgent.UI.Window_Agent.HandleHistoryKeys",      // 輸入歷史鍵盤
            "RimAgent.RimAgentMod.OpenFolder",                 // 設定頁開啟紀錄資料夾
            "RimAgent.Game.RimWorldGameWorld.TakeScreenshot",  // 截圖
            "RimAgent.UI.Window_Agent.LoadPendingScreenshot"   // 截圖預覽縮圖
        };

        [Test]
        public void EveryMethodInRimAgentJitsOnDotNetFramework()
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
                        // 直接呼叫 Unity InternalCall（Event.current 等）的方法在真 .NET Framework 上無法 JIT
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
}
