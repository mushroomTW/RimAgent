#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    private const int MaxListedDefTypes = 8;
    private const int MaxListedPatchedTypes = 15;
    private const int MaxListedPatchedMethods = 4;

    private static DefLookupInfo LookupMod(string packageId)
    {
        ModContentPack mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(m =>
            string.Equals(m.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase));
        if (mod == null) return null;

        var facts = new List<string>();
        string authors = mod.ModMetaData?.AuthorsString;
        if (!string.IsNullOrWhiteSpace(authors)) facts.Add("Authors: " + authors);

        List<string> defTypes = [.. mod.AllDefs
            .GroupBy(d => d.GetType().Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(MaxListedDefTypes)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count()}"))];
        facts.Add(defTypes.Count > 0 ? "Adds defs: " + string.Join(", ", defTypes) : "Adds no defs.");

        int xmlPatches = mod.Patches.Count();
        if (xmlPatches > 0) facts.Add(string.Create(CultureInfo.InvariantCulture, $"XML patches to other defs: {xmlPatches}."));

        List<string> patched = HarmonyPatchedTypes(mod);
        facts.Add(patched.Count > 0
            ? "Harmony patches change game code in: " + string.Join("; ", patched)
            : "No Harmony patches to game code.");

        return new DefLookupInfo
        {
            Kind = DefLookupKind.Mod,
            DefName = mod.PackageIdPlayerFacing,
            Label = mod.Name,
            Description = (mod.ModMetaData?.Description ?? string.Empty).StripTags().Trim(),
            Facts = facts
        };
    }

    /// <summary>以修補方法所在的組件判斷擁有者：Harmony ID 是作者自訂字串，對不回模組。</summary>
    private static List<string> HarmonyPatchedTypes(ModContentPack mod)
    {
        var own = new HashSet<Assembly>(mod.assemblies.loadedAssemblies);
        if (own.Count == 0) return [];
        var byType = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (MethodBase original in Harmony.GetAllPatchedMethods())
        {
            Patches info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            bool ours = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Any(p => p.PatchMethod?.DeclaringType != null && own.Contains(p.PatchMethod.DeclaringType.Assembly));
            if (!ours || original.DeclaringType == null) continue;
            string type = original.DeclaringType.Name;
            if (!byType.TryGetValue(type, out SortedSet<string> methods)) byType[type] = methods = new SortedSet<string>(StringComparer.Ordinal);
            methods.Add(original.Name);
        }
        return [.. byType
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(MaxListedPatchedTypes)
            .Select(kv => kv.Key + "." + string.Join('/', kv.Value.Take(MaxListedPatchedMethods)) +
                          (kv.Value.Count > MaxListedPatchedMethods ? string.Create(CultureInfo.InvariantCulture, $" (+{kv.Value.Count - MaxListedPatchedMethods})") : string.Empty))];
    }
}
