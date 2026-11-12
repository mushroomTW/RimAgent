using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimAgent.Game;

/// <summary>
/// lookup_def 的資料來源：數值照遊戲「i」資訊卡（Def.SpecialDisplayStats + StatsReportUtility.StatsToDraw），
/// 模組加的數值與組件資訊自動涵蓋；再補資訊卡沒有的取得方式、用途與研究狀態。
/// </summary>
internal sealed partial class RimWorldGameWorld
{
    private const int MaxListedSources = 6;
    private const int MaxListedNames = 10;

    // 原版私有方法：資訊卡的說明列 + StatDef 數值 + 材料係數。用反射呼叫才能與資訊卡一致；找不到時只剩 SpecialDisplayStats。
    private static readonly MethodInfo StatsToDrawMethod =
        AccessTools.Method(typeof(StatsReportUtility), "StatsToDraw", [typeof(Def), typeof(ThingDef)]);

    private readonly Dictionary<string, IReadOnlyList<DefInfo>> _lookupCatalogs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DefInfo> GetLookupCatalog(string kind)
    {
        string key = kind ?? string.Empty;
        if (_lookupCatalogs.TryGetValue(key, out IReadOnlyList<DefInfo> cached)) return cached;
        IEnumerable<Def> defs = key.ToLowerInvariant() switch
        {
            // 藍圖、框架與屍體會讓名稱比對出一堆同名候選（「muffalo corpse」），不收。
            DefLookupKind.Thing => DefDatabase<ThingDef>.AllDefsListForReading.Where(d =>
                d.category is ThingCategory.Item or ThingCategory.Building or ThingCategory.Plant or ThingCategory.Pawn
                && !d.IsBlueprint && !d.IsFrame && !d.IsCorpse),
            DefLookupKind.Terrain => DefDatabase<TerrainDef>.AllDefsListForReading,
            DefLookupKind.Research => DefDatabase<ResearchProjectDef>.AllDefsListForReading,
            DefLookupKind.Hediff => DefDatabase<HediffDef>.AllDefsListForReading,
            _ => null
        };
        if (defs == null) return [];
        List<DefInfo> catalog = [.. defs.Select(d => new DefInfo(d.defName, d.label))];
        _lookupCatalogs[key] = catalog;
        return catalog;
    }

    public DefLookupInfo LookupDef(string kind, string defName)
    {
        Def def = (kind ?? string.Empty).ToLowerInvariant() switch
        {
            DefLookupKind.Thing => DefDatabase<ThingDef>.GetNamedSilentFail(defName),
            DefLookupKind.Terrain => DefDatabase<TerrainDef>.GetNamedSilentFail(defName),
            DefLookupKind.Research => DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName),
            DefLookupKind.Hediff => DefDatabase<HediffDef>.GetNamedSilentFail(defName),
            _ => null
        };
        if (def == null) return null;

        // 用材料做的東西沒給材料時，部分數值算不出來；資訊卡同樣以預設材料顯示。
        ThingDef stuff = def is ThingDef { MadeFromStuff: true } made ? GenStuff.DefaultStuffFor(made) : null;
        var facts = new List<string>();
        if (stuff != null) facts.Add($"Stats assume the default material {stuff.label ?? stuff.defName}.");
        switch (def)
        {
            case ThingDef thing: facts.AddRange(ThingFacts(thing)); break;
            case ResearchProjectDef research: facts.AddRange(ResearchFacts(research)); break;
            case HediffDef hediff: facts.AddRange(HediffFacts(hediff)); break;
        }

        return new DefLookupInfo
        {
            Kind = kind.ToLowerInvariant(),
            DefName = def.defName,
            Label = def.label ?? def.defName,
            Description = (def.description ?? string.Empty).StripTags().Trim(),
            Stats = InfoCardStats(def, stuff),
            Facts = facts
        };
    }

    private static List<DefStatEntry> InfoCardStats(Def def, ThingDef stuff)
    {
        var entries = new List<StatDrawEntry>();
        StatRequest req = def is BuildableDef buildable ? StatRequest.For(buildable, stuff) : StatRequest.ForEmpty();
        AddSafely(entries, () => def.SpecialDisplayStats(req));
        // StatsToDraw 第一個一定是說明列（DescriptionEntry），說明已另外輸出，跳過。
        if (StatsToDrawMethod != null) AddSafely(entries, () => ((IEnumerable<StatDrawEntry>)StatsToDrawMethod.Invoke(null, [def, stuff])).Skip(1));

        var stats = new List<(int Order, int Priority, DefStatEntry Entry)>();
        foreach (StatDrawEntry e in entries)
        {
            try
            {
                if (!e.ShouldDisplay()) continue;
                stats.Add((e.category?.displayOrder ?? int.MaxValue, e.DisplayPriorityWithinCategory, new DefStatEntry
                {
                    Category = e.category?.defName ?? "Other",
                    CategoryLabel = e.category != null ? e.category.LabelCap.ToString() : "Other",
                    Label = e.LabelCap ?? string.Empty,
                    Value = (e.ValueString ?? string.Empty).StripTags()
                }));
            }
            catch (Exception)
            {
                // 少數模組數值在沒有實體 Thing 時算不出來；略過那一列，其餘照常。
            }
        }
        // 同資訊卡：類別順序 → 類別內優先度（大的在前）→ 名稱。
        return [.. stats.OrderBy(s => s.Order).ThenByDescending(s => s.Priority).ThenBy(s => s.Entry.Label).Select(s => s.Entry)];
    }

    /// <summary>逐筆取出；某些組件假設有實體 Thing 而在列舉中途拋例外，保留已取得的部分。</summary>
    private static void AddSafely(List<StatDrawEntry> into, Func<IEnumerable<StatDrawEntry>> source)
    {
        try
        {
            foreach (StatDrawEntry e in source()) into.Add(e);
        }
        catch (Exception)
        {
        }
    }

    private static IEnumerable<string> ThingFacts(ThingDef def)
    {
        List<ThingDef> things = DefDatabase<ThingDef>.AllDefsListForReading;

        List<RecipeDef> making = [.. DefDatabase<RecipeDef>.AllDefsListForReading.Where(r => r.products != null && r.products.Any(p => p.thingDef == def))];
        foreach (RecipeDef recipe in making.Take(MaxListedSources)) yield return RecipeFact(recipe, def);
        if (making.Count > MaxListedSources) yield return $"{making.Count - MaxListedSources} more recipes also make it.";

        string plants = NameList(things.Where(d => d.plant?.harvestedThingDef == def), d => $"{Name(d)} (yield {d.plant.harvestYield:0.#})");
        if (plants != null) yield return "Harvested from: " + plants + ".";
        string rocks = NameList(things.Where(d => d.building?.mineableThing == def), d => $"{Name(d)} (yield {d.building.mineableYield})");
        if (rocks != null) yield return "Mined from: " + rocks + ".";

        // 屍體 ThingDef 也帶 race，會讓每種動物出現兩次。
        List<ThingDef> races = [.. things.Where(d => d.race != null && !d.IsCorpse)];
        var animal = new List<string>();
        AddNames(animal, "butchering", races.Where(d => d.race.meatDef == def || d.race.leatherDef == def));
        AddNames(animal, "milking", races.Where(d => d.GetCompProperties<CompProperties_Milkable>()?.milkDef == def));
        AddNames(animal, "shearing", races.Where(d => d.GetCompProperties<CompProperties_Shearable>()?.woolDef == def));
        AddNames(animal, "eggs from", races.Where(d => d.GetCompProperties<CompProperties_EggLayer>()?.eggUnfertilizedDef == def));
        if (animal.Count > 0) yield return "From animals: " + string.Join("; ", animal) + ".";

        if (def.category == ThingCategory.Item && def.tradeability.TraderCanSell()) yield return "Traders can sell it.";

        string ingredientIn = NameList(DefDatabase<RecipeDef>.AllDefsListForReading.Where(r =>
            r.ingredients != null && r.ingredients.Any(i => i.IsFixedIngredient && i.FixedIngredient == def)), r => r.label ?? r.defName);
        if (ingredientIn != null) yield return "Ingredient of recipes: " + ingredientIn + ".";
        string builds = NameList(things.Where(d => d.BuildableByPlayer && CostsInclude(d, def))
            .Cast<BuildableDef>()
            .Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer && CostsInclude(d, def))), Name);
        if (builds != null) yield return "Used to build: " + builds + ".";
    }

    private static string RecipeFact(RecipeDef recipe, ThingDef product)
    {
        List<string> benches = [.. recipe.AllRecipeUsers.Select(Name).Distinct()];
        var parts = new List<string> { benches.Count > 0 ? "at " + string.Join(", ", benches.Take(4)) : "no workbench lists it" };
        if (!recipe.ingredients.NullOrEmpty()) parts.Add("ingredients " + string.Join(", ", recipe.ingredients.Select(i => i.SummaryFor(recipe))));
        if (!recipe.skillRequirements.NullOrEmpty()) parts.Add("needs " + string.Join(", ", recipe.skillRequirements.Select(s => s.Summary)));
        List<ResearchProjectDef> research = [.. new[] { recipe.researchPrerequisite }.Concat(recipe.researchPrerequisites ?? []).Where(r => r != null).Distinct()];
        if (research.Count > 0) parts.Add("research " + string.Join(", ", research.Select(ResearchWithStatus)));
        int count = recipe.products.First(p => p.thingDef == product).count;
        return $"Made by \"{recipe.label ?? recipe.defName}\" ({string.Join("; ", parts)}) -> {count}.";
    }

    private static IEnumerable<string> ResearchFacts(ResearchProjectDef def)
    {
        bool playing = Current.ProgramState == ProgramState.Playing;
        if (playing)
        {
            if (def.IsFinished) yield return "Status: finished.";
            else if (def.ProgressReal > 0f) yield return $"Status: in progress, {def.ProgressApparentString} / {def.CostApparent:0}.";
            else yield return "Status: not started" + (def.CanStartNow ? ", can start now." : ".");
        }
        yield return $"Cost: {def.CostApparent:0}" + (def.knowledgeCategory != null ? $" ({def.knowledgeCategory.label} knowledge)" : string.Empty) + $"; tech level {def.techLevel.ToString().ToLowerInvariant()}.";
        List<ResearchProjectDef> prerequisites = [.. (def.prerequisites ?? []).Concat(def.hiddenPrerequisites ?? []).Distinct()];
        yield return prerequisites.Count == 0 ? "Prerequisites: none." : "Prerequisites: " + string.Join(", ", prerequisites.Select(ResearchWithStatus)) + ".";
        if (def.requiredResearchBuilding != null) yield return "Needs research bench: " + Name(def.requiredResearchBuilding) + ".";
        if (!def.requiredResearchFacilities.NullOrEmpty()) yield return "Needs bench facilities: " + string.Join(", ", def.requiredResearchFacilities.Select(Name)) + ".";
        if (def.TechprintCount > 0) yield return $"Needs {def.TechprintCount} techprint(s)" + (playing ? $", {def.TechprintsApplied} applied." : ".");
        string unlocks = NameList(def.UnlockedDefs ?? [], d => d.label ?? d.defName);
        if (unlocks != null) yield return "Unlocks: " + unlocks + ".";
    }

    private static IEnumerable<string> HediffFacts(HediffDef def)
    {
        if (def.lethalSeverity > 0f) yield return $"Lethal at severity {def.lethalSeverity:0.##}.";
        if (def.tendable) yield return "Can be tended by a doctor.";
        if (def.chronic) yield return "Chronic.";
        if (def.CompProps<HediffCompProperties_Immunizable>() != null) yield return "The body builds immunity against it; it is a race between immunity and severity.";
        if (def.stages != null && def.stages.Count > 1)
        {
            yield return "Stages: " + string.Join(", ", def.stages.Select(s =>
                $"{(string.IsNullOrEmpty(s.label) ? "-" : s.label)} from {s.minSeverity:0.##}{(s.lifeThreatening ? " (life-threatening)" : string.Empty)}")) + ".";
        }
    }

    private static bool CostsInclude(BuildableDef def, ThingDef material) => def.CostList != null && def.CostList.Any(c => c.thingDef == material);

    private static string ResearchWithStatus(ResearchProjectDef r) =>
        Name(r) + (Current.ProgramState == ProgramState.Playing ? (r.IsFinished ? " (done)" : " (NOT done)") : string.Empty);

    private static string Name(Def d) => d.label ?? d.defName;

    private static void AddNames(List<string> into, string how, IEnumerable<ThingDef> sources)
    {
        string names = NameList(sources, Name);
        if (names != null) into.Add(how + " " + names);
    }

    /// <summary>逗號列出前幾個名稱，超過時附上總數；沒有任何項目回傳 null。</summary>
    private static string NameList<T>(IEnumerable<T> items, Func<T, string> name)
    {
        List<string> names = [.. items.Select(name).Distinct()];
        if (names.Count == 0) return null;
        string head = string.Join(", ", names.Take(MaxListedNames));
        return names.Count > MaxListedNames ? $"{head} and {names.Count - MaxListedNames} more" : head;
    }
}
