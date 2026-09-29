using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimTestRedux;
using Verse;

namespace RimAgent.InGameTests
{
    internal static class TestHelpers
    {
        public const string LogPrefix = "[RimAgent.InGameTests] ";

        /// <summary>收集所有失敗後一次丟出，一輪就看得到全部問題。</summary>
        public static void AssertNone(List<string> failures, string message)
        {
            if (failures.Count > 0)
                throw new AssertionException($"{message} ({failures.Count}):\n{string.Join("\n", failures)}");
        }

        /// <summary>
        /// RimTest Redux 沒有執行期 skip：前提不成立時記一行 skipped 再 return（runner 會算成通過），
        /// 統計時以這一行為準。
        /// </summary>
        public static void Skip(string test, string reason)
        {
            Log.Message($"{LogPrefix}skipped: {test}: {reason}");
        }

        /// <summary>沒有當前地圖（主選單那一輪）時記 skipped，回傳 true 讓呼叫端直接 return。</summary>
        public static bool SkipWithoutMap(string test)
        {
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null) return false;
            Skip(test, "needs a current map");
            return true;
        }

        /// <summary>載入期測試已經跑過一輪（由 TestDriver 設定）；結果不會變，之後的輪次不重跑。</summary>
        public static bool StartupSuiteDone;

        public static bool SkipStartupRerun(string test)
        {
            if (!StartupSuiteDone) return false;
            Skip(test, "startup checks already ran this session");
            return true;
        }

        public static ModContentPack RimAgentContent => LoadedModManager.GetMod<RimAgentMod>()?.Content;

        public static ModContentPack ModByPackageId(string packageId) =>
            LoadedModManager.RunningMods.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

        /// <summary>一個 mod 的 Assemblies/（含版本資料夾，如 1.6/Assemblies）裡出貨的 DLL 名稱（不含副檔名）。</summary>
        public static List<string> ShippedAssemblyNames(ModContentPack mod)
        {
            return mod.foldersToLoadDescendingOrder
                .Select(folder => Path.Combine(folder, "Assemblies"))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "*.dll"))
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
