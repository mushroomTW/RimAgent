#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
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
        return [.. Map.zoneManager.AllZones.Select(ToZoneWithExtents)];
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
        _storageFilterables ?? (_storageFilterables = [.. DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Item)
            .Select(d => new DefInfo(d.defName, d.label))]);

    public IReadOnlyList<DefInfo> GrowablePlants =>
        _growablePlants ?? (_growablePlants = [.. DefDatabase<ThingDef>.AllDefsListForReading
            // 種植區只收 Ground 標籤（PlantUtility.CanSowOnGrower）；裝飾花卉、僅水耕的作物不算。
            .Where(d => d.category == ThingCategory.Plant && d.plant != null && d.plant.Sowable
                        && d.plant.sowTags.Contains("Ground"))
            .Select(d => new DefInfo(d.defName, d.label))]);

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

        List<IntVec3> cells = CollectZoneCells(map, rect, designator, growing, out Dictionary<string, List<IntVec3>> skipped);
        string skippedText = SkippedText(skipped);
        if (cells.Count == 0) return CommandResult.Fail($"no cell in {rect} can hold a {kind.ToLowerInvariant()} zone.{skippedText}{NearbyZoneRects(map, rect, designator, plant)}");

        // 與原版拖曳相同：不相連的格子各自成區，避免單一區域被切成不連通的幾塊。
        var added = new List<string>();
        foreach (List<IntVec3> group in SplitContiguous(cells))
        {
            Zone zone = CreateZoneInstance(map, stockpile, plant);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 cell in group) zone.AddCell(cell);
            added.Add(string.Create(CultureInfo.InvariantCulture, $"{zone.label} [{zone.ID}] covering {group.Count} cells"));
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

    private static List<IntVec3> CollectZoneCells(Map map, CellRectSpec rect, Designator_ZoneAdd designator, bool growing, out Dictionary<string, List<IntVec3>> skipped)
    {
        var cells = new List<IntVec3>();
        var reasons = new Dictionary<string, List<IntVec3>>(StringComparer.Ordinal);
        for (int x = rect.MinX; x <= rect.MaxX; x++)
        {
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            {
                var cell = new IntVec3(x, 0, z);
                string why = GetCellSkipReason(map, cell, designator, growing);
                if (why == null)
                {
                    cells.Add(cell);
                    continue;
                }
                if (!reasons.TryGetValue(why, out List<IntVec3> at)) reasons[why] = at = [];
                at.Add(cell);
            }
        }
        skipped = reasons;
        return cells;
    }

    private static string GetCellSkipReason(Map map, IntVec3 cell, Designator_ZoneAdd designator, bool growing)
    {
        if (!cell.InBounds(map)) return "outside the map";
        if (map.zoneManager.ZoneAt(cell) is Zone existing) return $"already in zone \"{existing.label}\"";
        AcceptanceReport report = designator.CanDesignateCell(cell);
        return report.Accepted ? null : GetZoneRejectionReason(map, cell, report, growing);
    }

    private const int ZoneSearchRadius = 25;
    private const int MaxZoneSuggestionSide = 20;

    /// <summary>整塊都不能畫時，列出附近同樣大小、可用格最多的矩形；inspect_area 只給肥沃格數，模型無從得知在哪。</summary>
    private static string NearbyZoneRects(Map map, CellRectSpec rect, Designator_ZoneAdd designator, ThingDef plant)
    {
        int width = Math.Min(rect.MaxX - rect.MinX + 1, MaxZoneSuggestionSide), height = Math.Min(rect.MaxZ - rect.MinZ + 1, MaxZoneSuggestionSide);
        var center = new IntVec3((rect.MinX + rect.MaxX) / 2, 0, (rect.MinZ + rect.MaxZ) / 2);
        CellRect area = CellRect.CenteredOn(center, ZoneSearchRadius).ClipInsideMap(map);
        if (area.Width < width || area.Height < height) return string.Empty;

        int[,] sum = BuildUsablePrefixSum(map, area, designator, plant);

        var found = new List<(CellRect Rect, int Cells)>();
        IEnumerable<(CellRect Rect, int Cells)> ranked =
            (from x in Enumerable.Range(0, area.Width - width + 1)
             from z in Enumerable.Range(0, area.Height - height + 1)
             let cells = sum[x + width, z + height] - sum[x, z + height] - sum[x + width, z] + sum[x, z]
             where cells * 2 >= width * height
             let candidate = new CellRect(area.minX + x, area.minZ + z, width, height)
             orderby cells descending, candidate.CenterCell.DistanceToSquared(center)
             select (candidate, cells));
        foreach ((CellRect Rect, int Cells) option in ranked)
        {
            if (found.Exists(f => f.Rect.Overlaps(option.Rect))) continue;
            found.Add(option);
            if (found.Count == 3) break;
        }
        if (found.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $" No mostly usable {width}x{height} rectangle within {ZoneSearchRadius} cells; try a smaller one or look farther away.");
        }
        string cappedNote = width < rect.MaxX - rect.MinX + 1 || height < rect.MaxZ - rect.MinZ + 1
            ? string.Create(CultureInfo.InvariantCulture, $" These are smaller than you asked for (capped at {MaxZoneSuggestionSide} per side); combine several.")
            : string.Empty;
        return " Nearby rectangles with usable cells: " + string.Join("; ", found.Select(f =>
            string.Create(CultureInfo.InvariantCulture, $"({f.Rect.minX},{f.Rect.minZ})-({f.Rect.maxX},{f.Rect.maxZ}) {f.Cells} of {width * height}"))) + "." + cappedNote;
    }

    // 二維前綴和：每個候選矩形的可用格數都是常數時間。
    private static int[,] BuildUsablePrefixSum(Map map, CellRect area, Designator_ZoneAdd designator, ThingDef plant)
    {
        int[,] sum = new int[area.Width + 1, area.Height + 1];
        for (int x = 0; x < area.Width; x++)
        {
            for (int z = 0; z < area.Height; z++)
            {
                var cell = new IntVec3(area.minX + x, 0, area.minZ + z);
                int usable = map.zoneManager.ZoneAt(cell) == null && designator.CanDesignateCell(cell).Accepted
                    && (plant == null || map.fertilityGrid.FertilityAt(cell) >= plant.plant.fertilityMin) ? 1 : 0;
                sum[x + 1, z + 1] = usable + sum[x, z + 1] + sum[x + 1, z] - sum[x, z];
            }
        }
        return sum;
    }

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

    private const int MaxSkippedCellsListed = 8;
    private const int MaxSkipReasonsListed = 6;

    private static string SkippedText(Dictionary<string, List<IntVec3>> skipped)
    {
        if (skipped.Count == 0) return string.Empty;
        List<KeyValuePair<string, List<IntVec3>>> groups = [.. skipped.OrderByDescending(p => p.Value.Count)];
        int unlisted = groups.Skip(MaxSkipReasonsListed).Sum(p => p.Value.Count);
        string more = unlisted == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"; and {unlisted} more for other reasons");
        return " Skipped " + string.Join("; ", groups.Take(MaxSkipReasonsListed).Select(p => SkippedCells(p.Key, p.Value))) + more + ".";
    }

    private static string SkippedCells(string reason, List<IntVec3> cells)
    {
        CellRect extents = CellRect.FromCellList(cells);
        string where = cells.Count > MaxSkippedCellsListed
            ? string.Create(CultureInfo.InvariantCulture, $"within ({extents.minX},{extents.minZ})-({extents.maxX},{extents.maxZ})")
            : "at " + string.Join(", ", cells.Select(c => string.Create(CultureInfo.InvariantCulture, $"({c.x},{c.z})")));
        return string.Create(CultureInfo.InvariantCulture, $"{cells.Count} cell{(cells.Count == 1 ? "" : "s")} ({reason}) {where}");
    }

    private static string GetZoneRejectionReason(Map map, IntVec3 cell, AcceptanceReport report, bool growing)
    {
        if (!string.IsNullOrEmpty(report.Reason)) return report.Reason;
        // 迷霧裡的東西不能洩漏。
        if (cell.Fogged(map)) return "fogged";
        if (cell.InNoZoneEdgeArea(map)) return "at the map edge";
        Thing blocker = cell.GetThingList(map).FirstOrDefault(t => !t.def.CanOverlapZones);
        if (blocker != null) return "blocked by " + blocker.LabelNoParenthesis;
        TerrainDef terrain = cell.GetTerrain(map);
        if (terrain.passability == Traversability.Impassable) return "impassable terrain: " + terrain.label;
        // 門檻同 Designator_ZoneAdd_Growing.CanDesignateCell（1.7）；Ideology 的真菌只會把它調低。
        float minFertility = ModsConfig.BiotechActive ? 0.5f : ThingDefOf.Plant_Potato.plant.fertilityMin;
        return growing && cell.GetFertility(map) < minFertility ? "not fertile enough" : "not allowed here";
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
        if (zone == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no zone has id {zoneId}"));
        string label = zone.label;
        // 原生刪除路徑：Zone.Delete() 清格子並經 Deregister() 走 ZoneManager.DeregisterZone。
        zone.Delete();
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Deleted zone [{zoneId}] {label}. Stored items stay on the ground."));
    }

    public CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames)
    {
        Zone zone = Map.zoneManager.AllZones.FirstOrDefault(z => z.ID == zoneId);
        if (zone == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no zone has id {zoneId}"));
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
            stockpile.settings.filter.SetAllow(def, allow: true);
            applied.Add(def);
        }
        foreach (string defName in disallows)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamed(defName);
            stockpile.settings.filter.SetAllow(def, allow: false);
            removed.Add(def);
        }
        var parts = new List<string>();
        if (applied.Count > 0) parts.Add("allows " + string.Join(", ", applied.Select(d => d.label).ToArray()));
        if (removed.Count > 0) parts.Add("disallows " + string.Join(", ", removed.Select(d => d.label).ToArray()));
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Zone [{zoneId}] {zone.label} now {string.Join("; ", parts.ToArray())}."));
    }
}
