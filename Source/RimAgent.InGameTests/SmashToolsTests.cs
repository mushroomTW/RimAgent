using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using CoreLib.Performance;
using DevTools.Testing;
using HarmonyLib;
using SmashTools;
using UnityEngine;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>驗證隨附 SDK 的初始化擁有者、補丁唯一性與主執行緒工作派送。</summary>
[TestFixture(TestType.MainMenu)]
internal sealed class SmashToolsTests
{
    [Test]
    public void SetupBelongsToRimAgentAndPatchesOnce()
    {
        var setup = LoadedModManager.GetMod<ProjectSetup>();
        Expect.IsNotNull(setup);
        if (setup == null) return;
        Expect.ReferencesAreEqual(TestHelpers.RimAgentContent, setup.Content);

        var sdkAssembly = typeof(ProjectSetup).Assembly;
        int count = 0;
        foreach (var target in Harmony.GetAllPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(target);
            foreach (var patches in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
            {
                var sdkPatches = patches.Where(p => p.PatchMethod.DeclaringType?.Assembly == sdkAssembly).ToList();
                count += sdkPatches.Count;
                Expect.All(sdkPatches, p => p.owner == setup.Content.PackageIdPlayerFacing, target.ToString());
                Expect.All(sdkPatches.GroupBy(p => p.PatchMethod), group => group.Count() == 1, target.ToString());
            }
        }
        Expect.GreaterThan(count, 0, "SmashTools 必須實際安裝生命週期補丁");
    }

    [Test]
    public IEnumerator BackgroundDispatchReturnsToGameThread()
    {
        // 必須先在遊戲執行緒建立 UnityThread，才能從背景工作派送。
        Expect.IsTrue(UnityThread.IsInMainThread);
        bool ranOnGameThread = false;
        Task dispatch = Task.Run(() => UnityThread.ExecuteOnMainThreadAndWait(
            () => ranOnGameThread = UnityThread.IsInMainThread));
        float deadline = Time.realtimeSinceStartup + 5f;
        while (!dispatch.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        Expect.IsTrue(dispatch.IsCompleted, "主執行緒派送逾時");
        if (!dispatch.IsCompleted) yield break;
        Expect.IsFalse(dispatch.IsFaulted, dispatch.Exception?.ToString());
        Expect.IsTrue(ranOnGameThread);
    }
}
