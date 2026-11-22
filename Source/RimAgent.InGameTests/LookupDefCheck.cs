using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimAgent.Game;
using Verse;

namespace RimAgent.InGameTests;

/// <summary>
/// lookup_def 對原版 Def：資訊卡數值、製作配方、開採／收成／動物來源、用途、研究前置與解鎖、傷病致死與免疫、地板的建築段。
/// 只檢查與語言無關的結構字串（defName、英文段落標題），不比對在地化的名稱。
/// DevTools 測試與標記檔元件共用；run_test_cycle 傳不了 DevTools 的測試參數，隔離環境靠標記檔跑。
/// </summary>
internal static class LookupDefCheck
{
    private const string ThingKind = "thing";
    private const string EmptyKind = "";

    private static readonly (string Name, string Kind, string[] Wants)[] Cases =
    [
        ("ComponentIndustrial", ThingKind, ["(ComponentIndustrial), thing", "Stats:", "Made by", "Ingredient of recipes:", "Used to build:"]),
        ("Steel", ThingKind, ["Mined from:"]),
        ("RawRice", ThingKind, ["Harvested from:"]),
        ("Milk", ThingKind, ["From animals: milking"]),
        ("MicroelectronicsBasics", "research", ["(MicroelectronicsBasics), research", "Status:", "Prerequisites:", "Unlocks:"]),
        ("Plague", "hediff", ["(Plague), hediff", "Lethal at severity 1", "builds immunity", "Can be tended"]),
        ("WoodPlankFloor", "terrain", ["(WoodPlankFloor), terrain", "Building:", "Size: 1x1"]),
        ("Gun_AssaultRifle", EmptyKind, ["(Gun_AssaultRifle), thing", "Stats:"]),
        ("GreenMushroom.RimAgent", "mod", ["(GreenMushroom.RimAgent), mod", "Adds ", "Harmony patches"])
    ];

    public static List<string> Run(RimWorldGameWorld world, bool logReplies)
    {
        var tools = new RimAgent.Tools.BuildTools(world);
        var failures = new List<string>();
        world.TakeNewGameErrors();
        foreach ((string name, string kind, string[] wants) in Cases)
        {
            string text = tools.LookupDef(name, kind);
            if (logReplies) Log.Message(TestHelpers.LogPrefix + $"lookup_def({name}) reply:\n" + text);
            if (text.StartsWith("Error:", StringComparison.Ordinal))
            {
                failures.Add($"{name}: {text}");
                continue;
            }
            foreach (string want in wants.Where(want => !text.Contains(want, StringComparison.Ordinal)))
            {
                failures.Add($"{name} lacks '{want}'");
            }
        }
        if (tools.LookupDef("ComponentIndustrial", ThingKind).Contains("Added by the mod", StringComparison.Ordinal)) failures.Add("vanilla def tagged as modded");
        if (!tools.LookupDef("Gun_AssaultRifle", EmptyKind, "all").Contains("Stats:", StringComparison.Ordinal)) failures.Add("detail=all has no stats");
        foreach (string error in world.TakeNewGameErrors()) failures.Add("game error: " + error);
        return failures;
    }
}

/// <summary>隔離環境 Config 有標記檔時，進圖後跑一次，結果以 "[RimAgent.InGameTests] LookupDefCheck PASS/FAIL" 警告輸出。</summary>
public sealed class LookupDefCheckComponent : GameComponent
{
    private const string MarkerFileName = "RimAgentLookupDefCheck.xml";
    private bool _done;

    public LookupDefCheckComponent(Verse.Game game)
    {
    }

    public override void GameComponentUpdate()
    {
        if (_done || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
        _done = true;
        if (!File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, MarkerFileName))) return;
        string prefix = TestHelpers.LogPrefix + "LookupDefCheck ";
        try
        {
            List<string> failures = LookupDefCheck.Run(new RimWorldGameWorld(Current.Game), logReplies: true);
            Log.Warning(prefix + (failures.Count == 0 ? "PASS" : $"FAIL ({failures.Count}): " + string.Join(" | ", failures)));
        }
        catch (Exception ex)
        {
            Log.Warning(prefix + "FAIL exception: " + ex);
        }
    }
}
