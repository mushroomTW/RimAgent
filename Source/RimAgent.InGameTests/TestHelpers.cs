using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DevTools.Testing;
using Verse;

namespace RimAgent.InGameTests;

internal static class TestHelpers
{
    public const string LogPrefix = "[RimAgent.InGameTests] ";

    /// <summary>收集所有失敗後透過 Expect 報告，若有失敗則回傳 false 供呼叫端 return。</summary>
    public static bool AssertNone(List<string> failures, string message)
    {
        if (failures.Count > 0)
        {
            Expect.IsTrue(false, $"{message} ({failures.Count}):\n{string.Join("\n", failures)}");
            return false;
        }
        return true;
    }

    /// <summary>DevTools 測試跳過設定。</summary>
    public static void Skip(string reason)
    {
        Test.Skip(reason);
    }

    public static void Skip(string test, string reason)
    {
        Test.Skip($"{test}: {reason}");
    }

    /// <summary>沒有當前地圖時記 skipped，回傳 true 讓呼叫端直接 return。</summary>
    public static bool SkipWithoutMap(string test)
    {
        if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null) return false;
        Skip(test, "needs a current map");
        return true;
    }

    public static ModContentPack RimAgentContent => LoadedModManager.GetMod<RimAgentMod>()?.Content;

    public static ModContentPack ModByPackageId(string packageId) =>
        LoadedModManager.RunningMods.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>一個 mod 的 Assemblies/（含版本資料夾）裡出貨的 DLL 名稱（不含副檔名）。</summary>
    public static List<string> ShippedAssemblyNames(ModContentPack mod)
    {
        return [.. mod.foldersToLoadDescendingOrder
            .Select(folder => Path.Combine(folder, "Assemblies"))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.GetFiles(dir, "*.dll"))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
