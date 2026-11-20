#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public IReadOnlyList<ZoneInfo> GetZones()
    {
        return Map.zoneManager.AllZones.Select(ToZoneWithExtents).ToList();
    }

    private static ZoneInfo ToZone(Zone zone)
    {
        return new ZoneInfo
        {
            Id = zone.ID,
            Label = zone.label,
            Kind = ResolveZoneKind(zone),
            CellCount = zone.CellCount
        };
    }

    /// <summary>連同範圍：要掃過整個區域的格子，只給列區域的地方用（逐格查詢用 <see cref="ToZone"/>）。</summary>
    private static ZoneInfo ToZoneWithExtents(Zone zone)
    {
        ZoneInfo info = ToZone(zone);
        info.Extents = Extents(zone.Cells);
        return info;
    }

    private static string ResolveZoneKind(Zone zone)
    {
        if (zone is Zone_Stockpile) return "Stockpile";
        if (zone is Zone_Growing) return "Growing";
        return zone.GetType().Name;
    }

    public IReadOnlyList<DefInfo> StorageFilterables =>
        _storageFilterables ?? (_storageFilterables = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Item)
            .Select(d => new DefInfo(d.defName, d.label))
            .ToList());

    public IReadOnlyList<DefInfo> GrowablePlants =>
        _growablePlants ?? (_growablePlants = DefDatabase<ThingDef>.AllDefsListForReading
            // 種植區只收 Ground 標籤（PlantUtility.CanSowOnGrower）；裝飾花卉、僅水耕的作物不算。
            .Where(d => d.category == ThingCategory.Plant && d.plant != null && d.plant.Sowable
                        && d.plant.sowTags.Contains("Ground"))
            .Select(d => new DefInfo(d.defName, d.label))
            .ToList());

    public CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName)
    {
        Map map = Map;
        bool stockpile = string.Equals(kind, "Stockpile", StringComparison.OrdinalIgnoreCase);
        bool growing = string.Equals(kind, "Growing", StringComparison.OrdinalIgnoreCase);
        if (!stockpile && !growing) return CommandResult.Fail($"unknown zone kind {kind}. Use stockpile or growing.");

        CommandResult plantResult = TryResolveGrowingPlant(growing, plantDefName, out ThingDef plant);
        if (plantResult != null) return plantResult;

        // 格子能不能畫區域交給原版 designator 判斷（迷霧、地圖邊緣禁區、不可重疊的物件、種植區的肥沃度）。
        Designator_ZoneAdd designator = stockpile
            ? (Designator_ZoneAdd)new Designator_ZoneAddStockpile_Resources()
            : new Designator_ZoneAdd_Growing();

        List<IntVec3> cells = CollectZoneCells(map, rect, designator, growing, out Dictionary<string, int> skipped);
        string skippedText = skipped.Count == 0
            ? string.Empty
            : " Skipped " + string.Join("; ", skipped.Select(p => $"{p.Value} cell{(p.Value == 1 ? "" : "s")} ({p.Key})").ToArray()) + ".";
        if (cells.Count == 0) return CommandResult.Fail($"no cell in {rect} can hold a {kind.ToLowerInvariant()} zone.{skippedText}");

        // 與原版拖曳相同：不相連的格子各自成區，避免單一區域被切成不連通的幾塊。
        var added = new List<string>();
        foreach (List<IntVec3> group in SplitContiguous(cells))
        {
            Zone zone = CreateZoneInstance(map, stockpile, plant);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 cell in group) zone.AddCell(cell);
            added.Add($"{zone.label} [{zone.ID}] covering {group.Count} cells");
        }
        return CommandResult.Ok("Added " + string.Join(", ", added.ToArray()) + "." + skippedText);
    }

    private static CommandResult TryResolveGrowingPlant(bool growing, string plantDefName, out ThingDef plant)
    {
        plant = null;
        if (growing && !string.IsNullOrEmpty(plantDefName))
        {
            plant = DefDatabase<ThingDef>.GetNamedSilentFail(plantDefName);
            if (plant == null || plant.category != ThingCategory.Plant || plant.plant == null)
                return CommandResult.Fail($"{plantDefName} is not a growable plant");
            // 與 GrowablePlants 清單及 Command_SetPlantToGrow 一致：可播種、地面標籤、研究已完成。
            if (!plant.plant.Sowable || !plant.plant.sowTags.Contains("Ground"))
                return CommandResult.Fail($"{plant.label} cannot be sown in a growing zone");
            if (plant.plant.sowResearchPrerequisites != null && plant.plant.sowResearchPrerequisites.Any(r => !r.IsFinished))
                return CommandResult.Fail($"{plant.label} needs research first");
        }
        return null;
    }

    /// <summary>
    /// 收集矩形內可畫區域的格子；不合格的格子跳過並依原因計數，與原版拖曳只套用合格格子的行為一致。
    /// </summary>
    private static List<IntVec3> CollectZoneCells(Map map, CellRectSpec rect, Designator_ZoneAdd designator, bool growing, out Dictionary<string, int> skipped)
    {
        var cells = new List<IntVec3>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int x = rect.MinX; x <= rect.MaxX; x++)
        {
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            {
                var cell = new IntVec3(x, 0, z);
                string why = null;
                if (!cell.InBounds(map))
                {
                    why = "outside the map";
                }
                else if (map.zoneManager.ZoneAt(cell) is Zone existing)
                {
                    why = $"already in zone \"{existing.label}\"";
                }
                else
                {
                    AcceptanceReport report = designator.CanDesignateCell(cell);
                    if (!report.Accepted) why = GetZoneRejectionReason(report, growing);
                }
                if (why == null)
                {
                    cells.Add(cell);
                    continue;
                }
                reasons.TryGetValue(why, out int count);
                reasons[why] = count + 1;
            }
        }
        skipped = reasons;
        return cells;
    }

    /// <summary>以四方向相鄰把格子分成連通的群組。</summary>
    private static List<List<IntVec3>> SplitContiguous(List<IntVec3> cells)
    {
        var remaining = new HashSet<IntVec3>(cells);
        var groups = new List<List<IntVec3>>();
        foreach (IntVec3 start in cells)
        {
            if (!remaining.Remove(start)) continue;
            var group = new List<IntVec3> { start };
            for (int i = 0; i < group.Count; i++)
            {
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 next = group[i] + dir;
                    if (remaining.Remove(next)) group.Add(next);
                }
            }
            groups.Add(group);
        }
        return groups;
    }

    private static string GetZoneRejectionReason(AcceptanceReport report, bool growing)
    {
        if (!string.IsNullOrEmpty(report.Reason)) return report.Reason;
        return growing ? "not fertile enough, fogged, blocked or at the map edge" : "fogged, blocked or at the map edge";
    }

    private static Zone CreateZoneInstance(Map map, bool stockpile, ThingDef plant)
    {
        if (stockpile)
        {
            return new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
        }
        var growingZone = new Zone_Growing(map.zoneManager);
        if (plant != null) growingZone.SetPlantDefToGrow(plant);
        return growingZone;
    }

    public CommandResult DeleteZone(int zoneId)
    {
        Zone zone = Map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
        string label = zone.label;
        // 原生刪除路徑：Zone.Delete() 清格子並經 Deregister() 走 ZoneManager.DeregisterZone。
        zone.Delete();
        return CommandResult.Ok($"Deleted zone [{zoneId}] {label}. Stored items stay on the ground.");
    }

    public CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames)
    {
        Zone zone = Map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
        if (zone is not Zone_Stockpile stockpile) return CommandResult.Fail($"{zone.label} is not a stockpile and has no storage filter");
        List<string> allows = [.. (allowDefNames ?? [])];
        List<string> disallows = [.. (disallowDefNames ?? [])];
        if (allows.Count == 0 && disallows.Count == 0) return CommandResult.Fail("nothing to allow or disallow");
        var applied = new List<ThingDef>();
        var removed = new List<ThingDef>();
        foreach (string defName in allows.Concat(disallows))
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null) return CommandResult.Fail($"unknown stored item {defName}");
            // 與 StorageFilterables 一致：只有物品能放進儲存篩選器。
            if (def.category != ThingCategory.Item) return CommandResult.Fail($"{defName} is not a storable item");
        }
        string conflict = allows.FirstOrDefault(a => disallows.Any(d => string.Equals(a, d, StringComparison.OrdinalIgnoreCase)));
        if (conflict != null) return CommandResult.Fail($"{conflict} is listed as both allowed and disallowed");
        foreach (string defName in allows)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
            stockpile.settings.filter.SetAllow(def, true);
            applied.Add(def);
        }
        foreach (string defName in disallows)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
            stockpile.settings.filter.SetAllow(def, false);
            removed.Add(def);
        }
        var parts = new List<string>();
        if (applied.Count > 0) parts.Add("allows " + string.Join(", ", applied.Select(d => d.label).ToArray()));
        if (removed.Count > 0) parts.Add("disallows " + string.Join(", ", removed.Select(d => d.label).ToArray()));
        return CommandResult.Ok($"Zone [{zoneId}] {zone.label} now {string.Join("; ", parts.ToArray())}.");
    }
}
