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
        [Test]
        public void EveryMethodInRimAgentJitsOnDotNetFramework()
        {
            Assembly assembly = typeof(ColonyAgentSession).Assembly;
            var failures = new List<string>();
            int prepared = 0;
            int unityNative = 0;

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
                        unityNative++;
                    }
                    catch (Exception ex)
                    {
                        Exception root = ex is TypeInitializationException && ex.InnerException != null ? ex.InnerException : ex;
                        failures.Add($"{type.FullName}.{method.Name}: {root.GetType().Name}: {root.Message}");
                    }
                }
            }

            Assert.That(prepared, Is.GreaterThan(100), "應該有掃到方法");
            Assert.That(unityNative, Is.LessThan(6), "只有極少數 UI 方法應直接碰 Unity InternalCall（輸入列、斜線選單鍵盤、游標、截圖）");
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }
    }
}
