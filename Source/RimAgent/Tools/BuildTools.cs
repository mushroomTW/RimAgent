#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

public sealed class BuildTools
{
    /// <summary>一次最多放的藍圖格數（20x20）；再大就分批，免得一句話鋪滿半張地圖。</summary>
    private const int MaxBlueprintCells = 400;

    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;
    private readonly Func<bool> _layoutsEnabled;

    public BuildTools(IGameWorld world, ConfirmationGate gate = null, Func<bool> layoutsEnabled = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
        _layoutsEnabled = layoutsEnabled ?? (() => true);
    }

    /// <summary>房型開關關掉時 <c>place_blueprint</c> 改用的說明（拿掉 layout），免得模型每蓋一間房都先試一次被拒。</summary>
    private const string PlaceBlueprintWithoutLayoutsDescription = "Places build blueprints. shape: single (one thing at x,z), line (straight row from x,z to x2,z2), fill (every cell of the rectangle, e.g. floors) or outline (its border, e.g. room walls; add a door on the wall afterwards). Use single for things larger than 1x1: x,z is its center cell (an even width or depth leaves the extra cell east/north, and rotation can shift it), and the reply lists the rectangle each blueprint covers. All or nothing: if any cell is refused, nothing is placed and the reasons are listed; if space is occupied, also the blocking things with ids and rectangles. Size, cost and rules: lookup_def. Max 400 cells per call.";

    internal string DescriptionOverride(string toolName)
    {
        return toolName == "place_blueprint" && !_layoutsEnabled() ? PlaceBlueprintWithoutLayoutsDescription : null;
    }

    private sealed class DesignationKind
    {
        public string Name;
        public ThingKind ThingKind;
        public string DefName;
        public bool OnCell;
        public Func<ThingInfo, bool> Applies;
        public string Verb;
    }

    private static readonly DesignationKind[] Kinds =
    [
        new() { Name = "mine", ThingKind = ThingKind.Mineable, DefName = "Mine", OnCell = true, Applies = t => true, Verb = "mining" },
        new() { Name = "cut_plants", ThingKind = ThingKind.Plant, DefName = "CutPlant", OnCell = false, Applies = t => true, Verb = "cutting" },
        new() { Name = "harvest", ThingKind = ThingKind.Plant, DefName = "HarvestPlant", OnCell = false, Applies = t => t.Harvestable, Verb = "harvesting" },
        new() { Name = "deconstruct", ThingKind = ThingKind.Building, DefName = "Deconstruct", OnCell = false, Applies = t => t.PlayerOwned, Verb = "deconstruction" },
        new() { Name = "haul", ThingKind = ThingKind.Item, DefName = "Haul", OnCell = false, Applies = t => true, Verb = "hauling" },
        new() { Name = "hunt", ThingKind = ThingKind.Pawn, DefName = "Hunt", OnCell = false, Applies = t => t.Wild, Verb = "hunting" }
    ];

    private const string KindList = "mine, cut_plants, harvest, deconstruct, haul or hunt";

    [AgentTool("designate_area", ToolCategory.Build, ToolRisk.Confirm)]
    [Description("Marks things for colonists: mine, cut_plants, harvest (ripe only), deconstruct (player-owned only), haul or hunt (wild only) in the player's selection or a rectangle (x1,z1)-(x2,z2). Reports marked, already marked and not applicable counts.")]
    public string DesignateArea(
        [Description("mine, cut_plants, harvest, deconstruct, haul or hunt.")] string kind,
        [Description("true uses the player's selection and ignores the rectangle.")] bool useSelection,
        int x1,
        int z1,
        int x2,
        int z2,
        [Description("Only names or defNames containing this text, e.g. \"steel\"; empty = all.")] string filter = "")
    {
        DesignationKind dk = ParseKind(kind);
        if (dk == null) return ToolText.Error($"unknown kind \"{kind}\". Use {KindList}.");

        Result<List<ThingInfo>> targets = CollectTargets(dk.ThingKind, useSelection, (x1, z1, x2, z2), filter, out int omitted);
        if (!targets.Success) return targets.Error;

        if (_gate != null && !_gate.AllowCommands())
        {
            int eligible = targets.Value.Count(t => dk.Applies(t) && !t.Designations.Contains(dk.DefName, StringComparer.OrdinalIgnoreCase));
            string where = useSelection
                ? "the player's selection"
                : string.Create(CultureInfo.InvariantCulture, $"rectangle ({x1},{z1})-({x2},{z2})");
            string preview = string.Create(CultureInfo.InvariantCulture, $"would mark {eligible} object(s) for {dk.Verb} ") +
                $"among {targets.Value.Count} matching object(s) in {where}." + OmittedNote(omitted);
            return ToolText.Blocked(_gate, preview);
        }

        return ExecuteDesignateArea(dk, targets.Value, omitted);
    }

    private string ExecuteDesignateArea(DesignationKind dk, List<ThingInfo> targets, int omitted)
    {
        int marked = 0, skipped = 0, notApplicable = 0;
        var failures = new List<string>();
        var wanted = new List<ThingInfo>();
        foreach (ThingInfo t in targets)
        {
            int before = marked + skipped;
            ApplyDesignationTarget(dk, t, ref marked, ref skipped, ref notApplicable, failures);
            if (marked + skipped > before) wanted.Add(t);
        }
        return FormatDesignateSummary(dk, targets.Count, marked, skipped, notApplicable, failures, omitted) + NoStorageNote(dk, wanted);
    }

    private string NoStorageNote(DesignationKind dk, List<ThingInfo> wanted)
    {
        if (dk.Name != "haul" || wanted.Count == 0) return string.Empty;
        HashSet<int> without = [.. _world.WithoutStorage([.. wanted.Select(t => t.Id)])];
        List<ThingInfo> stuck = [.. wanted.Where(t => without.Contains(t.Id))];
        if (stuck.Count == 0) return string.Empty;
        string labels = string.Join(", ", stuck.Select(t => t.Label).Distinct(StringComparer.Ordinal).Take(3));
        return string.Create(CultureInfo.InvariantCulture, $" Warning: no storage accepts {stuck.Count} of them ({labels}), so nobody will haul those and waiting will not help: allow them in a stockpile first (add_zone, set_zone_filter), or build elsewhere.");
    }

    private void ApplyDesignationTarget(DesignationKind dk, ThingInfo t, ref int marked, ref int skipped, ref int notApplicable, List<string> failures)
    {
        if (!dk.Applies(t))
        {
            notApplicable++;
            return;
        }
        if (t.Designations.Contains(dk.DefName, StringComparer.OrdinalIgnoreCase))
        {
            skipped++;
            return;
        }

        CommandResult r = dk.OnCell
            ? _world.AddDesignationOnCell(t.X, t.Z, dk.DefName)
            : _world.AddDesignationOnThing(t.Id, dk.DefName);
        if (r.Success) marked++;
        else failures.Add($"{t.Label} at {ToolText.Cell(t.X, t.Z)}: {r.Message}");
    }

    private static string OmittedNote(int omitted)
    {
        return omitted > 0 ? string.Create(CultureInfo.InvariantCulture, $" {omitted} more matching object(s) were not included (search limit): repeat the call or narrow the rectangle.") : string.Empty;
    }

    private static string FormatDesignateSummary(DesignationKind dk, int totalTargets, int marked, int skipped, int notApplicable, List<string> failures, int omitted)
    {
        if (marked == 0 && skipped == 0 && failures.Count == 0)
        {
            return ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"nothing eligible for {dk.Verb} among {totalTargets} matching object(s)") +
                                  (notApplicable > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({notApplicable} not applicable, e.g. not ripe or not player-owned).") : ".") + OmittedNote(omitted));
        }
        if (marked == 0 && skipped == 0)
        {
            return ToolText.Error($"could not mark anything for {dk.Verb}. Failures: " + string.Join("; ", failures.Take(5)));
        }

        string summary = string.Create(CultureInfo.InvariantCulture, $"Marked {marked} for {dk.Verb}");
        if (skipped > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {skipped} already marked");
        if (notApplicable > 0) summary += string.Create(CultureInfo.InvariantCulture, $", {notApplicable} not applicable");
        summary += "." + OmittedNote(omitted);
        return failures.Count == 0 ? summary : summary + " Failures: " + string.Join("; ", failures.Take(5));
    }

    [AgentTool("cancel_designation", ToolCategory.Build, ToolRisk.Safe)]
    [Description("Removes marks of one kind from the player's selection or a rectangle.")]
    public string CancelDesignation(
        [Description("mine, cut_plants, harvest, deconstruct, haul or hunt.")] string kind,
        [Description("true to use the player's current map selection instead of a rectangle.")] bool useSelection,
        [Description("Rectangle corner x (ignored when useSelection is true).")] int x1,
        [Description("Rectangle corner z.")] int z1,
        [Description("Opposite corner x.")] int x2,
        [Description("Opposite corner z.")] int z2)
    {
        DesignationKind dk = ParseKind(kind);
        if (dk == null) return ToolText.Error($"unknown kind \"{kind}\". Use {KindList}.");

        Result<List<ThingInfo>> targets = CollectTargets(dk.ThingKind, useSelection, (x1, z1, x2, z2), string.Empty, out int omitted);
        if (!targets.Success) return targets.Error;

        List<ThingInfo> markedTargets = [.. targets.Value.Where(t => t.Designations.Contains(dk.DefName, StringComparer.OrdinalIgnoreCase))];
        if (_gate != null && !_gate.AllowCommands())
        {
            string cancelPreview = $"would remove {markedTargets.Count} {dk.Verb} mark(s)." + OmittedNote(omitted);
            return ToolText.Blocked(_gate, cancelPreview);
        }

        int removed = 0;
        var failures = new List<string>();
        foreach (ThingInfo t in markedTargets)
        {
            CommandResult r = dk.OnCell
                ? _world.RemoveDesignationOnCell(t.X, t.Z, dk.DefName)
                : _world.RemoveDesignationOnThing(t.Id, dk.DefName);
            if (r.Success) removed++;
            else failures.Add($"{t.Label} at {ToolText.Cell(t.X, t.Z)}: {r.Message}");
        }

        if (removed == 0 && failures.Count == 0) return $"No {dk.Verb} marks found there." + OmittedNote(omitted);
        if (removed == 0) return ToolText.Error("could not remove any marks. Failures: " + string.Join("; ", failures.Take(5)));
        string summary = string.Create(CultureInfo.InvariantCulture, $"Removed {removed} {dk.Verb} mark(s).") + OmittedNote(omitted);
        return failures.Count == 0 ? summary : summary + " Failures: " + string.Join("; ", failures.Take(5));
    }

    [AgentTool("place_blueprint", ToolCategory.Build, ToolRisk.Confirm)]
    [Description("Places build blueprints. shape: single (one thing at x,z), line (straight row from x,z to x2,z2), fill (every cell of the rectangle, e.g. floors), outline (its border, e.g. room walls; add a door on the wall afterwards) or layout (a whole predesigned room: building = layout name, x,z = its center cell, rotation turns the whole room, stuff = one material for walls, doors and furniture, or set each part: \"walls=granite blocks; floor=fine granite tile; furniture=steel\" (any material or floor in the game, modded ones too; omitted parts keep the layout's own); an empty building lists the layouts). To build a room (bedroom, kitchen, workshop, storage, etc.), use a layout by default: list them and pick the closest fit; draw walls with outline only when the player asks for a specific size or shape, or no layout fits. Use single for things larger than 1x1: x,z is its center cell (an even width or depth leaves the extra cell east/north, and rotation can shift it), and the reply lists the rectangle each blueprint covers. All or nothing: if any cell (or any part of a layout) is refused, nothing is placed and the reasons are listed; if space is occupied, also the blocking things with ids and rectangles. Size, cost and rules: lookup_def. North is larger z, east is larger x. Max 400 cells per call.")]
#pragma warning disable S107 // Tool parameters are discovered via reflection and exposed as LLM function schema
    public string PlaceBlueprint(
        [Description("Name or defName, e.g. \"wall\", \"door\", \"solar generator\".")] string building,
        [Description("One of the shapes listed in the tool description.")] string shape,
        [Description("line: one end; fill/outline: one corner (not the center); single/layout: center cell.")] int x,
        [Description("Same point as x.")] int z,
        [Description("line: other end; fill/outline: opposite corner; ignored for single/layout.")] int x2 = -1,
        [Description("Same point as x2.")] int z2 = -1,
        [Description("0 north, 1 east, 2 south, 3 west (doors, beds). Also turns non-square buildings: east/west swaps width and depth. Otherwise 0.")] int rotation = 0,
        [Description("Material, e.g. \"wood\", \"granite blocks\"; empty = default.")] string stuff = "")
#pragma warning restore S107
    {
        if (string.Equals(shape?.Trim(), "layout", StringComparison.OrdinalIgnoreCase)) return PlaceLayout(building, x, z, rotation, stuff);

        Result<DefInfo> def = DefResolver.Resolve(building, _world.Buildables, "buildable");
        Result<string> stuffResult = ResolveStuffDefName(stuff);
        if (SplitMaterial(building) is var (bare, material))
        {
            if (stuffResult.Success && stuffResult.Value.Length > 0 && stuffResult.Value != material)
                return ToolText.Error($"building \"{building}\" names {material} but stuff is {stuffResult.Value}; use building \"{bare.DefName}\" and one stuff.");
            def = Result<DefInfo>.Ok(bare);
            if (stuffResult.Success) stuffResult = Result<string>.Ok(material);
        }
        if (!def.Success) return def.Error;
        if (!stuffResult.Success) return stuffResult.Error;
        string stuffDefName = stuffResult.Value;

        if (rotation < 0 || rotation > 3) return ToolText.Error("rotation must be 0 (north), 1 (east), 2 (south) or 3 (west).");
        Result<List<(int X, int Z)>> cells = ShapeCells(shape, x, z, x2, z2);
        if (!cells.Success) return cells.Error;

        string what = def.Value.Label + (stuffDefName.Length > 0 ? $" ({stuffDefName})" : string.Empty);
        string where = WhereText(cells.Value, x, z, x2, z2, shape);
        string blocked = ToolText.Blocked(_gate, $"would place {cells.Value.Count} {what} blueprint(s) {where}.");
        if (blocked != null) return blocked;

        bool outline = string.Equals(shape.Trim(), "outline", StringComparison.OrdinalIgnoreCase);
        return PlaceAndReport(def, cells.Value, rotation, stuffDefName, what, where, outline);
    }

    private static string WhereText(List<(int X, int Z)> cells, int x, int z, int x2, int z2, string shape)
    {
        return cells.Count == 1
            ? $"at {ToolText.Cell(x, z)}"
            : $"from {ToolText.Cell(x, z)} to {ToolText.Cell(x2, z2)} ({shape.Trim().ToLowerInvariant()})";
    }

    private string PlaceAndReport(Result<DefInfo> def, List<(int X, int Z)> cells, int rotation, string stuffDefName, string what, string where, bool outline)
    {
        MapBounds bounds = _world.GetMapBounds();
        BuildingInfo info = _world.GetBuildingInfo(def.Value.DefName);
        bool large = info != null && (info.Width > 1 || info.Height > 1);
        // 先檢查每一格都放得下（含同一次呼叫裡大型建築彼此重疊），任何一格不行就一個都不放，
        // 避免只蓋出半道牆或缺角的房間卻回報成功。
        var validation = ValidateAllCells(cells, rotation, info, bounds, large, def, stuffDefName);
        if (validation.failures.Count > 0)
        {
            string fits = large && validation.refusedByPlace && cells.Count == 1 ? NearbyFits(def.Value.DefName, cells[0], rotation, stuffDefName, bounds) : string.Empty;
            return BuildPlaceValidationError(what, cells, validation) + fits + WeakGroundNote(info, cells, rotation, bounds);
        }

        var placedCells = new List<(int X, int Z)>();
        var lateFailures = new List<string>();
        PlaceAllCells(cells, def.Value.DefName, rotation, stuffDefName, placedCells, lateFailures);
        if (lateFailures.Count > 0) return BuildPlaceLateFailureError(what, cells, placedCells, lateFailures, where);

        return $"Placed {placedCells.Count} {what} blueprint(s) {where}."
            + (outline ? OutlineNote(placedCells) : string.Empty)
            + (large ? FootprintNote(info, placedCells, rotation) : string.Empty);
    }

    /// <summary>外框與內部的大小、四側座標：模型常少算一圈牆，或把 z 較大的一側當成南邊。</summary>
    private static string OutlineNote(List<(int X, int Z)> cells)
    {
        int minX = cells.Min(c => c.X), maxX = cells.Max(c => c.X), minZ = cells.Min(c => c.Z), maxZ = cells.Max(c => c.Z);
        int width = maxX - minX + 1, height = maxZ - minZ + 1;
        return string.Create(CultureInfo.InvariantCulture, $" Outer size {width}x{height} (east-west x north-south), ")
            + (width > 2 && height > 2
                ? string.Create(CultureInfo.InvariantCulture, $"interior {width - 2}x{height - 2}")
                : "no interior")
            + string.Create(CultureInfo.InvariantCulture, $". Sides: north z={maxZ}, south z={minZ}, west x={minX}, east x={maxX}.");
    }

    private const int MaxAmbiguousShown = 4;
    private const int NearbyRadius = 3;
    private const int MaxNearbyFits = 5;

    private string NearbyFits(string defName, (int X, int Z) center, int rotation, string stuffDefName, MapBounds bounds)
    {
        int[] offsets = [.. Enumerable.Range(-NearbyRadius, 2 * NearbyRadius + 1)];
        // 一格只列一個朝向（優先要求的那個），名額留給不同的格子。
        List<string> fits = [.. (from dx in offsets
                                 from dz in offsets
                                 orderby dx * dx + dz * dz
                                 let cx = center.X + dx
                                 let cz = center.Z + dz
                                 where TargetResolver.CheckCell(cx, cz, bounds) == null
                                 let fit = Enumerable.Range(0, 4).OrderBy(r => r == rotation ? 0 : 1).Select(r => (int?)r)
                                     .FirstOrDefault(r => _world.TryPlaceBlueprint(defName, cx, cz, r.Value, stuffDefName, checkOnly: true).Success)
                                 where fit != null
                                 select string.Create(CultureInfo.InvariantCulture, $"({cx},{cz}) rotation {fit}")).Take(MaxNearbyFits)];
        return fits.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $" No cell within {NearbyRadius} fits in any rotation.")
            : " Nearby centers that fit (check they are on the right side of your walls): " + string.Join("; ", fits) + ".";
    }

    private const int FirmGroundRadius = 24;

    /// <summary>承重不足時給出附近撐得住的最大一塊地；只說「需要中型」的話，模型會去砍樹或試鋪地板。</summary>
    private string WeakGroundNote(BuildingInfo info, List<(int X, int Z)> cells, int rotation, MapBounds bounds)
    {
        if (info == null || !Enum.TryParse(info.TerrainAffordance, ignoreCase: true, out GroundSupport needed)) return string.Empty;
        List<CellRectSpec> covered = [.. cells.Select(c => OccupiedRect(c.X, c.Z, rotation, info))];
        if (covered.Any(r => !bounds.Contains(r.MinX, r.MinZ) || !bounds.Contains(r.MaxX, r.MaxZ))) return string.Empty;
        if (!covered.Any(r => HasWeakCell(r, needed))) return string.Empty;

        (int x, int z) = cells[0];
        var area = new CellRectSpec(
            Math.Max(0, x - FirmGroundRadius), Math.Max(0, z - FirmGroundRadius),
            Math.Min(bounds.Width - 1, x + FirmGroundRadius), Math.Min(bounds.Height - 1, z + FirmGroundRadius));
        string note = $" The ground here is too weak (needs {info.TerrainAffordance} support); cutting plants does not change that. ";
        return note + (ObserveTools.LargestBuildable(area, _world.GetCells(area), needed, info.Width, info.Height) is CellRectSpec site
            ? string.Create(CultureInfo.InvariantCulture, $"Largest buildable rectangle within {FirmGroundRadius} cells that holds it: {ObserveTools.SiteText(site)}.")
            : string.Create(CultureInfo.InvariantCulture, $"Nothing within {FirmGroundRadius} cells holds it."));
    }

    private bool HasWeakCell(CellRectSpec rect, GroundSupport needed)
    {
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
        {
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                if (_world.GetSupport(x, z) < needed) return true;
            }
        }
        return false;
    }

    private static string BuildPlaceValidationError(string what, List<(int X, int Z)> cells, (bool refusedByPlace, List<string> failures) validation)
    {
        return ToolText.Error($"placed nothing: {validation.failures.Count} of {cells.Count} {what} blueprint(s) cannot go there. "
            + string.Join("; ", validation.failures.Take(5)) + (validation.failures.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $"; and {validation.failures.Count - 5} more") : string.Empty)
            + ". Clear the blockers or choose other cells, then place them all again.");
    }

    private static string BuildPlaceLateFailureError(string what, List<(int X, int Z)> cells, List<(int X, int Z)> placedCells, List<string> lateFailures, string where)
    {
        return ToolText.Error($"only {placedCells.Count} of {cells.Count} {what} blueprint(s) were placed {where}; "
            + "these were refused after the check: " + string.Join("; ", lateFailures.Take(5)) + ".");
    }

    private (bool refusedByPlace, List<string> failures) ValidateAllCells(List<(int X, int Z)> cells, int rotation, BuildingInfo info, MapBounds bounds, bool large, Result<DefInfo> def, string stuffDefName)
    {
        var failures = new List<string>();
        var claimed = new HashSet<(int X, int Z)>();
        bool refusedByPlace = false;
        foreach ((int cx, int cz) in cells)
        {
            refusedByPlace |= ValidatePlaceCell(cx, cz, rotation, info, bounds, claimed, failures, large, def, stuffDefName);
        }
        return (refusedByPlace, failures);
    }

    private void PlaceAllCells(List<(int X, int Z)> cells, string defName, int rotation, string stuffDefName, List<(int X, int Z)> placedCells, List<string> lateFailures)
    {
        foreach ((int cx, int cz) in cells)
        {
            CommandResult r = _world.TryPlaceBlueprint(defName, cx, cz, rotation, stuffDefName);
            if (r.Success) placedCells.Add((cx, cz));
            else lateFailures.Add($"{ToolText.Cell(cx, cz)}: {r.Message}");
        }
    }

#pragma warning disable S107 // 私有驗證輔助方法，參數皆為放置檢查相關，無需重構
    private bool ValidatePlaceCell(int cx, int cz, int rotation, BuildingInfo info, MapBounds bounds, HashSet<(int X, int Z)> claimed, List<string> failures, bool large, Result<DefInfo> def, string stuffDefName)
    {
        string cell = ToolText.Cell(cx, cz) + (large ? " (would cover " + OccupiedRect(cx, cz, rotation, info) + ")" : string.Empty);
        if (TargetResolver.CheckCell(cx, cz, bounds) != null)
        {
            failures.Add($"{ToolText.Cell(cx, cz)}: outside the map");
            return true;
        }
        if (large && !Claim(claimed, OccupiedRect(cx, cz, rotation, info)))
        {
            failures.Add($"{cell}: overlaps another blueprint of this call");
            return true;
        }
        CommandResult check = _world.TryPlaceBlueprint(def.Value.DefName, cx, cz, rotation, stuffDefName, checkOnly: true);
        if (!check.Success) failures.Add($"{cell}: {check.Message}");
        return check.PlacementRefused;
    }
#pragma warning restore S107

    private static bool Claim(HashSet<(int X, int Z)> claimed, CellRectSpec rect)
    {
        for (int x = rect.MinX; x <= rect.MaxX; x++)
        {
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
            {
                if (claimed.Contains((x, z))) return false;
            }
        }
        for (int x = rect.MinX; x <= rect.MaxX; x++)
        {
            for (int z = rect.MinZ; z <= rect.MaxZ; z++) claimed.Add((x, z));
        }
        return true;
    }

    private const int MaxLayoutFailuresListed = 6;

    private string PlaceLayout(string layout, int x, int z, int rotation, string stuff)
    {
        if (!_layoutsEnabled()) return ToolText.Error("room layouts are turned off in the RimAgent settings; build with the other shapes instead.");
        IReadOnlyList<LayoutInfo> layouts = _world.Layouts;
        if (layouts.Count == 0) return ToolText.Error("no room layouts are installed.");

        if (string.IsNullOrWhiteSpace(layout)) return ListLayouts(layouts, MaxListedDescription);
        Result<DefInfo> chosen = ResolveLayout(layout, layouts);
        if (!chosen.Success)
        {
            List<LayoutInfo> hits = [.. layouts.Where(l => new[] { l.DefName, l.Label, l.Description }.Any(s => s.Contains(layout.Trim(), StringComparison.OrdinalIgnoreCase)))];
            if (hits.Count > 0) return chosen.Error + "\n" + ListLayouts(hits, int.MaxValue);
            return chosen.Error + " Layouts: " + string.Join("; ", layouts.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Label} {l.Width}x{l.Height}"))) + ".";
        }

        if (rotation < 0 || rotation > 3) return ToolText.Error("rotation must be 0 (north), 1 (east), 2 (south) or 3 (west).");
        Result<LayoutMaterials> materials = ResolveLayoutMaterials(stuff);
        if (!materials.Success) return materials.Error;

        LayoutPlan plan = _world.GetLayoutPlan(chosen.Value.DefName, x, z, rotation);
        if (plan == null) return ToolText.Error($"no data for layout {chosen.Value.DefName}.");
        MapBounds bounds = _world.GetMapBounds();
        if (LayoutOutOfMap(plan, bounds)) return ToolText.Error($"{chosen.Value.Label} centered at {ToolText.Cell(x, z)} would cover {plan.Rect}, which leaves the map.");

        string chosenMaterials = materials.Value.Describe();
        string what = chosen.Value.Label + (chosenMaterials.Length > 0 ? $" ({chosenMaterials})" : string.Empty);
        string blocked = ToolText.Blocked(_gate, $"would place the {what} layout: {plan.Pieces.Count} blueprints covering {plan.Rect}.");
        if (blocked != null) return blocked;

        return ApplyLayout(plan, materials.Value, what);
    }

    private static bool LayoutOutOfMap(LayoutPlan plan, MapBounds bounds)
    {
        return TargetResolver.CheckCell(plan.Rect.MinX, plan.Rect.MinZ, bounds) != null
            || TargetResolver.CheckCell(plan.Rect.MaxX, plan.Rect.MaxZ, bounds) != null;
    }

    private string ApplyLayout(LayoutPlan plan, LayoutMaterials materials, string what)
    {
        var infos = new Dictionary<string, BuildingInfo>(StringComparer.Ordinal);
        BuildingInfo Info(string defName)
        {
            if (infos.TryGetValue(defName, out BuildingInfo i)) return i;
            i = _world.GetBuildingInfo(defName);
            infos[defName] = i;
            return i;
        }
        string Label(LayoutPiece piece) => Info(piece.DefName)?.Label ?? piece.DefName;

        // 先確認每一件都放得下（牆擋住、地板需要承重地基等），任何一件不行就整間都不放，不留缺角的房間。
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        int refused = CheckLayoutPieces(plan, materials, Info, Label, failures);
        if (refused > 0)
        {
            return ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"placed nothing: {refused} of {plan.Pieces.Count} parts of the {what} layout cannot go in {plan.Rect}. ")
                + FormatLayoutFailures(failures) + " Clear the blockers or choose another spot, then place the layout again.");
        }

        int placed = 0;
        var lateFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        var fellBack = new Dictionary<string, int>(StringComparer.Ordinal);
        PlaceLayoutPieces(plan, materials, Info, Label, ref placed, lateFailures, fellBack);
        if (lateFailures.Count > 0)
        {
            return ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"only {placed} of {plan.Pieces.Count} blueprints of the {what} layout were placed in {plan.Rect}; ")
                + "these were refused after the check: " + FormatLayoutFailures(lateFailures));
        }
        string summary = string.Create(CultureInfo.InvariantCulture, $"Placed all {placed} blueprints of the {what} layout, covering {plan.Rect}.");
        if (fellBack.Count > 0) summary += " Kept the layout's own material or floor (the requested one was refused): " + FormatLayoutFailures(fellBack);
        return summary;
    }

    private static Result<DefInfo> ResolveLayout(string layout, IReadOnlyList<LayoutInfo> layouts)
    {
        return DefResolver.Resolve(layout, [.. layouts.Select(l => new DefInfo(l.DefName, l.Label))], "layout");
    }

    private int CheckLayoutPieces(LayoutPlan plan, LayoutMaterials materials, Func<string, BuildingInfo> Info, Func<LayoutPiece, string> Label, Dictionary<string, int> failures)
    {
        int refused = 0;
        foreach (LayoutPiece piece in plan.Pieces)
        {
            CommandResult check = PlaceLayoutPiece(piece, materials.For(piece.Part), Info(piece.DefName), checkOnly: true, out _);
            if (check.Success) continue;
            refused++;
            string key = $"{Label(piece)}: {check.Message}";
            failures[key] = failures.TryGetValue(key, out int n) ? n + 1 : 1;
        }
        return refused;
    }

    private void PlaceLayoutPieces(LayoutPlan plan, LayoutMaterials materials, Func<string, BuildingInfo> Info, Func<LayoutPiece, string> Label, ref int placed, Dictionary<string, int> lateFailures, Dictionary<string, int> fellBack)
    {
        foreach (LayoutPiece piece in plan.Pieces)
        {
            CommandResult r = PlaceLayoutPiece(piece, materials.For(piece.Part), Info(piece.DefName), checkOnly: false, out bool usedOwnStuff);
            string label = Label(piece);
            if (r.Success)
            {
                placed++;
                if (usedOwnStuff) fellBack[label] = fellBack.TryGetValue(label, out int k) ? k + 1 : 1;
                continue;
            }
            string key = $"{label}: {r.Message}";
            lateFailures[key] = lateFailures.TryGetValue(key, out int n) ? n + 1 : 1;
        }
    }

    private const int MaxListedDescription = 80;

    // 一個房型一行：read_tool_output 的 find 以行為單位，全部擠成一行就只找得到第一個。
    private static string ListLayouts(IReadOnlyList<LayoutInfo> layouts, int maxDescription)
    {
        string cut = layouts.Any(l => l.Description.Length > maxDescription) ? "; descriptions are shortened" : string.Empty;
        return "Layouts (size, then what it is" + cut + "):\n" + string.Join("\n",
            layouts.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Label} ({l.DefName}) {l.Width}x{l.Height}")
                + (l.Description.Length > 0 ? ": " + ToolText.TruncateField(l.Description, maxDescription) : string.Empty)));
    }

    private CommandResult PlaceLayoutPiece(LayoutPiece piece, string replacement, BuildingInfo info, bool checkOnly, out bool usedOwnStuff)
    {
        usedOwnStuff = false;
        bool floor = piece.Part == LayoutPart.Floor;
        bool skip = replacement.Length == 0
            || (floor ? replacement == piece.DefName : replacement == piece.StuffDefName || info == null || info.StuffCount == 0);
        if (skip) return _world.TryPlaceBlueprint(piece.DefName, piece.X, piece.Z, piece.Rotation, piece.StuffDefName, checkOnly);

        // 指定的材料或地板這件不能用（例如某些家具不收石磚、重地板要承重地基）時，退回房型原本的設計。
        CommandResult r = floor
            ? _world.TryPlaceBlueprint(replacement, piece.X, piece.Z, piece.Rotation, string.Empty, checkOnly)
            : _world.TryPlaceBlueprint(piece.DefName, piece.X, piece.Z, piece.Rotation, replacement, checkOnly);
        if (r.Success) return r;
        CommandResult own = _world.TryPlaceBlueprint(piece.DefName, piece.X, piece.Z, piece.Rotation, piece.StuffDefName, checkOnly);
        usedOwnStuff = own.Success;
        return own.Success ? own : r;
    }

    private sealed class LayoutMaterials
    {
        public string Walls = string.Empty;
        public string Floor = string.Empty;
        public string Furniture = string.Empty;

        public string For(LayoutPart part) => part switch
        {
            LayoutPart.Wall => Walls,
            LayoutPart.Floor => Floor,
            _ => Furniture
        };

        public string Describe()
        {
            var parts = new List<string>();
            if (Walls.Length > 0) parts.Add("walls " + Walls);
            if (Floor.Length > 0) parts.Add("floor " + Floor);
            if (Furniture.Length > 0) parts.Add("furniture " + Furniture);
            return string.Join(", ", parts);
        }
    }

    private Result<LayoutMaterials> ResolveLayoutMaterials(string stuff)
    {
        var result = new LayoutMaterials();
        string text = stuff?.Trim() ?? string.Empty;
        if (text.IndexOfAny(['=', ':']) < 0)
        {
            Result<string> both = ResolveStuffDefName(text);
            if (!both.Success) return Result<LayoutMaterials>.Fail(both.Error);
            result.Walls = result.Furniture = both.Value;
            return Result<LayoutMaterials>.Ok(result);
        }

        foreach (string entry in text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string error = ApplyLayoutMaterialEntry(result, entry);
            if (error != null) return Result<LayoutMaterials>.Fail(error);
        }
        return Result<LayoutMaterials>.Ok(result);
    }

    private string ApplyLayoutMaterialEntry(LayoutMaterials result, string entry)
    {
        int sep = entry.IndexOfAny(['=', ':']);
        string key = sep < 0 ? string.Empty : entry.Substring(0, sep).Trim().ToLowerInvariant();
        string value = sep < 0 ? string.Empty : entry.Substring(sep + 1).Trim();
        switch (key)
        {
            case "wall":
            case "walls":
            case "furniture":
                Result<string> material = ResolveStuffDefName(value);
                if (!material.Success) return material.Error;
                if (key == "furniture") result.Furniture = material.Value;
                else result.Walls = material.Value;
                return null;
            case "floor":
            case "floors":
                return ApplyFloorEntry(result, value);
            default:
                return ToolText.Error($"unknown part \"{entry.Trim()}\" in stuff; use walls=...; floor=...; furniture=...");
        }
    }

    private string ApplyFloorEntry(LayoutMaterials result, string value)
    {
        if (value.Length == 0) return null;
        Result<DefInfo> floor = DefResolver.Resolve(value, _world.Buildables, "floor");
        if (!floor.Success) return floor.Error;
        if (_world.GetBuildingInfo(floor.Value.DefName)?.IsTerrain != true)
        {
            return ToolText.Error($"{floor.Value.Label} is not a floor.");
        }
        result.Floor = floor.Value.DefName;
        return null;
    }

    private static string FormatLayoutFailures(Dictionary<string, int> failures)
    {
        IEnumerable<string> listed = failures.OrderByDescending(f => f.Value).Take(MaxLayoutFailuresListed)
            .Select(f => f.Value > 1 ? string.Create(CultureInfo.InvariantCulture, $"{f.Key} (x{f.Value})") : f.Key);
        string more = failures.Count > MaxLayoutFailuresListed ? string.Create(CultureInfo.InvariantCulture, $"; +{failures.Count - MaxLayoutFailuresListed} more kinds") : string.Empty;
        return string.Join("; ", listed) + more + ".";
    }

    [AgentTool("remove_blueprint", ToolCategory.Build, ToolRisk.Confirm)]
    [Description("Cancels the player's blueprints and unfinished construction frames touching a rectangle (x1,z1)-(x2,z2), like the game's cancel command; frames refund their materials. Use it to move something placed in the wrong spot, then place_blueprint again. Finished buildings are not affected (designate_area deconstruct).")]
    public string RemoveBlueprint(
        [Description("Rectangle corner x.")] int x1,
        [Description("Rectangle corner z.")] int z1,
        [Description("Opposite corner x; same as x1 for one cell.")] int x2,
        [Description("Opposite corner z; same as z1 for one cell.")] int z2)
    {
        CellRectSpec rect = CellRectSpec.FromCorners(x1, z1, x2, z2);
        string blocked = ToolText.Blocked(_gate, $"would cancel blueprints and frames in {rect}.");
        if (blocked != null) return blocked;
        IReadOnlyList<string> removed = _world.RemoveBlueprints(rect);
        if (removed.Count == 0) return ToolText.Error($"no blueprints or frames in {rect}.");
        return $"Cancelled {removed.Count}: " + string.Join("; ", removed.Take(MaxFootprintsListed))
            + (removed.Count > MaxFootprintsListed ? string.Create(CultureInfo.InvariantCulture, $"; +{removed.Count - MaxFootprintsListed} more.") : ".");
    }

    private const int MaxFootprintsListed = 5;

    /// <summary>建築放在 (x,z)、朝向 rotation 時佔的矩形；與 GenAdj.OccupiedRect 同一公式（遊戲內測試逐一對照）。</summary>
    internal static CellRectSpec OccupiedRect(int x, int z, int rotation, BuildingInfo info)
    {
        int w = info.Width, h = info.Height;
        if (w == 1 && h == 1) return new CellRectSpec(x, z, x, z);
        if (rotation == 1 || rotation == 3)
        {
            int swap = w;
            w = h;
            h = swap;
        }
        int shiftX = 0, shiftZ = 0;
        if (rotation == 1)
        {
            shiftZ = -1;
        }
        else if (rotation == 2)
        {
            shiftX = -1;
            shiftZ = -1;
        }
        else if (rotation == 3)
        {
            shiftX = -1;
        }
        int cx = x + (w % 2 == 0 ? shiftX : 0);
        int cz = z + (h % 2 == 0 ? shiftZ : 0);
        int minX = cx - (w - 1) / 2, minZ = cz - (h - 1) / 2;
        return new CellRectSpec(minX, minZ, minX + w - 1, minZ + h - 1);
    }

    private static string FootprintNote(BuildingInfo info, List<(int X, int Z)> placedCells, int rotation)
    {
        CellRectSpec first = OccupiedRect(placedCells[0].X, placedCells[0].Z, rotation, info);
        string size = string.Create(CultureInfo.InvariantCulture, $"{first.MaxX - first.MinX + 1}x{first.MaxZ - first.MinZ + 1}");
        if (placedCells.Count == 1) return string.Create(CultureInfo.InvariantCulture, $" Size {size}, rotation {rotation}, covers {first}.");
        IEnumerable<string> listed = placedCells.Take(MaxFootprintsListed)
            .Select(c => $"{ToolText.Cell(c.X, c.Z)} covers {OccupiedRect(c.X, c.Z, rotation, info)}");
        string more = placedCells.Count > MaxFootprintsListed ? string.Create(CultureInfo.InvariantCulture, $"; +{placedCells.Count - MaxFootprintsListed} more") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $" Each is {size}, rotation {rotation}: {string.Join("; ", listed)}{more}.");
    }

    private static readonly Dictionary<string, string> PlaceWorkerRules = new(StringComparer.Ordinal)
    {
        ["PlaceWorker_NotUnderRoof"] = "must not be under a roof",
        ["Placeworker_OnlyUnderThickRoof"] = "must be under a thick rock roof",
        ["PlaceWorker_MustBeIndoors"] = "must be in an enclosed room, not open to the map edge",
        ["Placeworker_AttachedToWall"] = "must be attached to a wall (rotation points at the wall)",
        ["PlaceWorker_OnSteamGeyser"] = "must sit on a steam geyser"
    };

    private const int DefaultStatBudget = 30;
    private const int MaxDescriptionChars = 800;

    [AgentTool("lookup_def", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Game encyclopedia, including modded content: looks up an item, weapon, apparel, building, plant, animal, floor, research project or health condition by name or defName. Reports its description and info-card stats; how to get it (recipes with workbench, ingredients, skill and research; harvest, mining, animals, traders); what it is an ingredient of or used to build; research status, prerequisites and unlocks; hediff stages and lethality. Buildables also get size and rotation, cost, ground support needed (heavy, medium or light), builder skill, placement rules, ship part, power and interaction cell: use it before planning a layout. With kind=mod it describes an active mod by name or packageId: what it adds and which game code it patches, to learn how it changes the game.")]
    public string LookupDef(
        [Description("Name or defName, e.g. \"component\", \"ship reactor\", \"Ship_Engine\", \"plague\", \"microelectronics\".")] string name,
        [Description("Optional: thing, terrain, research or hediff to narrow the search, or mod to look up a mod. Empty searches the first four.")] string kind = "",
        [Description("Optional: a stat group to show in full, named as listed under \"More stat groups\", or \"all\". Empty shows the main groups.")] string detail = "")
    {
        string wanted = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (wanted.Length > 0 && wanted != DefLookupKind.Mod && !DefLookupKind.All.Contains(wanted, StringComparer.Ordinal))
        {
            return ToolText.Error($"unknown kind \"{kind}\"; use {string.Join(", ", DefLookupKind.All)}, {DefLookupKind.Mod} or leave it empty.");
        }

        // 各種類的目錄合併後一起比對，名稱層級（精確 → 前綴 → 包含）才是全域的；再以參考對回種類。
        var kindOf = new Dictionary<DefInfo, string>();
        List<DefInfo> candidates = CollectLookupCandidates(wanted, kindOf);
        Result<DefInfo> def = DefResolver.Resolve(name, candidates, wanted.Length > 0 ? wanted : "def", string.Empty, out IReadOnlyList<DefInfo> ambiguous, abbreviations: true);
        if (def.Success) return DescribeDef(def.Value, kindOf[def.Value], detail);
        if (ambiguous.Count is 0 or > MaxAmbiguousShown) return def.Error;
        return string.Create(CultureInfo.InvariantCulture, $"\"{name.Trim()}\" matches {ambiguous.Count}; use the exact defName elsewhere.\n\n")
            + string.Join("\n\n", ambiguous.Select(d => DescribeDef(d, kindOf[d], detail)));
    }

    private string DescribeDef(DefInfo def, string resolvedKind, string detail)
    {
        DefLookupInfo info = _world.LookupDef(resolvedKind, def.DefName);
        if (info == null) return ToolText.Error($"no data for {def.DefName}.");

        var sb = new System.Text.StringBuilder();
        sb.Append(info.Label).Append(" (").Append(info.DefName).Append("), ").AppendLine(resolvedKind);
        if (info.Description.Length > 0)
        {
            sb.AppendLine(info.Description.Length > MaxDescriptionChars ? info.Description[..MaxDescriptionChars] + "…" : info.Description);
        }
        AppendStats(sb, info.Stats, (detail ?? string.Empty).Trim());
        AppendLookupFacts(sb, info);
        if (IsPlaceableBuildable(resolvedKind, info))
        {
            BuildingInfo building = _world.GetBuildingInfo(info.DefName);
            if (building != null) sb.AppendLine("Building:").AppendLine(FormatBuildingInfo(building));
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private List<DefInfo> CollectLookupCandidates(string wanted, Dictionary<DefInfo, string> kindOf)
    {
        var candidates = new List<DefInfo>();
        foreach (string k in wanted.Length > 0 ? [wanted] : DefLookupKind.All)
        {
            foreach (DefInfo d in _world.GetLookupCatalog(k))
            {
                kindOf[d] = k;
                candidates.Add(d);
            }
        }
        return candidates;
    }

    private static void AppendLookupFacts(System.Text.StringBuilder sb, DefLookupInfo info)
    {
        if (info.Facts.Count > 0)
        {
            sb.AppendLine("Facts:");
            foreach (string fact in info.Facts) sb.Append("- ").AppendLine(fact);
        }
    }

    private bool IsPlaceableBuildable(string resolvedKind, DefLookupInfo info)
    {
        return string.Equals(resolvedKind, DefLookupKind.Thing, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(resolvedKind, DefLookupKind.Terrain, StringComparison.OrdinalIgnoreCase)
                && _world.Buildables.Any(b => b.DefName == info.DefName));
    }

    private static void AppendStats(System.Text.StringBuilder sb, IReadOnlyList<DefStatEntry> stats, string detail)
    {
        if (stats.Count == 0) return;
        // 以顯示名稱分組：原版的 Basics、BasicsImportant、BasicsNonPawn 等類別同名，分開列會重複出現好幾行。
        List<IGrouping<string, DefStatEntry>> groups = [.. stats.GroupBy(s => s.CategoryLabel, StringComparer.Ordinal)];
        var shown = SelectShownGroups(groups, detail, sb);

        if (shown.Count > 0)
        {
            sb.AppendLine("Stats:");
            foreach (IGrouping<string, DefStatEntry> g in shown)
            {
                sb.AppendLine(FormatStatGroup(g));
            }
        }
        AppendHiddenGroups(sb, groups, shown);
    }

    private static List<IGrouping<string, DefStatEntry>> SelectShownGroups(List<IGrouping<string, DefStatEntry>> groups, string detail, System.Text.StringBuilder sb)
    {
        bool all = string.Equals(detail, "all", StringComparison.OrdinalIgnoreCase);
        var shown = new List<IGrouping<string, DefStatEntry>>();
        if (all)
        {
            shown.AddRange(groups);
        }
        else if (detail.Length > 0)
        {
            shown.AddRange(groups.Where(g => string.Equals(g.Key, detail, StringComparison.OrdinalIgnoreCase) ||
                                             g.Any(s => string.Equals(s.Category, detail, StringComparison.OrdinalIgnoreCase))));
            if (shown.Count == 0) sb.Append("No stat group \"").Append(detail).AppendLine("\".");
        }
        else
        {
            int used = 0;
            foreach (IGrouping<string, DefStatEntry> g in groups)
            {
                int n = g.Count();
                if (shown.Count > 0 && used + n > DefaultStatBudget) continue;
                shown.Add(g);
                used += n;
            }
        }
        return shown;
    }

    private static void AppendHiddenGroups(System.Text.StringBuilder sb, List<IGrouping<string, DefStatEntry>> groups, List<IGrouping<string, DefStatEntry>> shown)
    {
        List<IGrouping<string, DefStatEntry>> hidden = [.. groups.Where(g => !shown.Contains(g))];
        if (hidden.Count > 0)
        {
            sb.Append("More stat groups (pass detail=<name> or \"all\"): ")
              .AppendJoin(", ", hidden.Select(g => $"{g.Key} ({g.Count()})")).AppendLine(".");
        }
    }

    private static string FormatStatGroup(IGrouping<string, DefStatEntry> g)
    {
        return $"[{g.Key}] " + string.Join("; ", g.Select(s => s.Value.Length > 0 ? $"{s.Label}: {s.Value}" : s.Label));
    }

    private static string FormatBuildingInfo(BuildingInfo b)
    {
        var sb = new System.Text.StringBuilder();
        AppendSizeAndRotation(sb, b);
        AppendCost(sb, b);
        AppendGround(sb, b);
        AppendResearch(sb, b);
        AppendBuilderSkill(sb, b);
        AppendPlacement(sb, b);
        AppendShipPart(sb, b);
        AppendPower(sb, b);
        AppendInteractionCell(sb, b);
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendSizeAndRotation(StringBuilder sb, BuildingInfo b)
    {
        sb.Append("Size: ").Append(b.Width).Append('x').Append(b.Height).Append(" (x by z, facing north). ");
        sb.AppendLine(b.Width == b.Height
            ? "Rotation does not change the footprint."
            : string.Create(CultureInfo.InvariantCulture, $"Rotation 1 or 3 (east/west) makes it {b.Height}x{b.Width}."));
        if (b.Width > 1 || b.Height > 1) sb.AppendLine("place_blueprint x,z is the center cell; its reply lists the covered rectangle.");
    }

    private static void AppendCost(StringBuilder sb, BuildingInfo b)
    {
        var cost = new List<string>(b.Costs.Select(c => $"{c.Label} x{c.Count}"));
        if (b.StuffCount > 0) cost.Add(string.Create(CultureInfo.InvariantCulture, $"{b.StuffCount} of a material ({string.Join(", ", b.StuffCategories)})"));
        sb.Append("Cost: ").AppendLine(cost.Count == 0 ? "none" : string.Join(", ", cost));
    }

    private static void AppendGround(StringBuilder sb, BuildingInfo b)
    {
        if (b.TerrainAffordance.Length > 0)
        {
            sb.Append("Ground: needs ").Append(b.TerrainAffordance).Append(" support under every covered cell")
              .AppendLine(b.AffordanceFromStuff ? " (may depend on the material)." : ".");
        }
    }

    private static void AppendResearch(StringBuilder sb, BuildingInfo b)
    {
        if (b.Research.Count == 0) sb.AppendLine("Research: none.");
        else sb.Append("Research: ").AppendJoin(", ", b.Research.Select(r => r.Label + (r.Done ? " (done)" : " (NOT done)"))).AppendLine();
    }

    private static void AppendBuilderSkill(StringBuilder sb, BuildingInfo b)
    {
        if (b.ConstructionSkill > 0 || b.ArtisticSkill > 0)
        {
            var skills = new List<string>();
            if (b.ConstructionSkill > 0) skills.Add(string.Create(CultureInfo.InvariantCulture, $"construction {b.ConstructionSkill}"));
            if (b.ArtisticSkill > 0) skills.Add(string.Create(CultureInfo.InvariantCulture, $"artistic {b.ArtisticSkill}"));
            sb.Append("Builder skill: ").AppendJoin(", ", skills).AppendLine(".");
        }
    }

    private static void AppendPlacement(StringBuilder sb, BuildingInfo b)
    {
        if (b.PlaceWorkers.Count > 0)
        {
            sb.Append("Placement: ").AppendJoin("; ", b.PlaceWorkers.Select(w =>
                PlaceWorkerRules.TryGetValue(w, out string rule) ? w + " -> " + rule : w)).AppendLine();
        }
    }

    private static void AppendShipPart(StringBuilder sb, BuildingInfo b)
    {
        if (b.ShipPart) sb.AppendLine("Ship part: counts toward the starship (must touch the others edge to edge; check_ship_layout verifies).");
    }

    private static void AppendPower(StringBuilder sb, BuildingInfo b)
    {
        if (b.PowerWatts > 0) sb.Append("Power: uses ").Append(ToolText.Num(b.PowerWatts)).AppendLine(" W.");
        else if (b.PowerWatts < 0) sb.Append("Power: produces ").Append(ToolText.Num(-b.PowerWatts)).AppendLine(" W.");
    }

    private static void AppendInteractionCell(StringBuilder sb, BuildingInfo b)
    {
        if (b.InteractionOffsets.Count > 0)
        {
            sb.Append("Interaction cell: offset ").AppendJoin(", ", b.InteractionOffsets.Select(o => ToolText.Cell(o.X, o.Z))).AppendLine(" from the center when facing north (turns with rotation); keep it free.");
        }
    }

    private static Result<List<(int X, int Z)>> ShapeCells(string shape, int x, int z, int x2, int z2)
    {
        string kind = shape?.Trim().ToLowerInvariant() ?? string.Empty;
        if (kind == "single")
        {
            return Result<List<(int X, int Z)>>.Ok([(x, z)]);
        }
        if (kind != "line" && kind != "fill" && kind != "outline")
        {
            return Result<List<(int X, int Z)>>.Fail(ToolText.Error($"unknown shape \"{shape}\". Use single, line, fill, outline or layout."));
        }
        if (x2 < 0 || z2 < 0)
        {
            return Result<List<(int X, int Z)>>.Fail(ToolText.Error($"{kind} needs x2 and z2: the line's end or the opposite corner."));
        }
        if (kind == "line" && x != x2 && z != z2)
        {
            return Result<List<(int X, int Z)>>.Fail(ToolText.Error("a line must be straight: keep x or z the same at both ends."));
        }
        int minX = Math.Min(x, x2), maxX = Math.Max(x, x2), minZ = Math.Min(z, z2), maxZ = Math.Max(z, z2);
        long width = maxX - minX + 1L, height = maxZ - minZ + 1L;
        long count = kind == "outline" && width > 2 && height > 2 ? 2 * (width + height) - 4 : width * height;
        if (count > MaxBlueprintCells)
        {
            return Result<List<(int X, int Z)>>.Fail(ToolText.Error(string.Create(CultureInfo.InvariantCulture, $"that {kind} covers {count} cells; the limit is {MaxBlueprintCells} per call. Split it into smaller pieces.")));
        }

        if (kind == "line")
        {
            return Result<List<(int X, int Z)>>.Ok(GenerateLineCells(x, z, x2, z2, count));
        }
        return Result<List<(int X, int Z)>>.Ok(GenerateRectCells(kind, minX, maxX, minZ, maxZ));
    }

    private static List<(int X, int Z)> GenerateLineCells(int x, int z, int x2, int z2, long count)
    {
        var cells = new List<(int X, int Z)>();
        int dx = Math.Sign(x2 - x), dz = Math.Sign(z2 - z);
        for (int i = 0; i < count; i++) cells.Add((x + dx * i, z + dz * i));
        return cells;
    }

    private static List<(int X, int Z)> GenerateRectCells(string kind, int minX, int maxX, int minZ, int maxZ)
    {
        var cells = new List<(int X, int Z)>();
        for (int cz = minZ; cz <= maxZ; cz++)
        {
            for (int cx = minX; cx <= maxX; cx++)
            {
                bool border = cx == minX || cx == maxX || cz == minZ || cz == maxZ;
                if (kind == "fill" || border) cells.Add((cx, cz));
            }
        }
        return cells;
    }

    [AgentTool("set_forbidden", ToolCategory.Build, ToolRisk.Safe)]
    [Description("Forbids or allows the visible items and corpses in a rectangle or the whole map. Raiders' gear and other loot often start forbidden, so haulers ignore them until allowed. find_things marks forbidden items.")]
    public string SetForbidden(
        [Description("false to allow (unforbid), true to forbid.")] bool forbidden,
        [Description("Rectangle corner x; pass -1 for all four corners to cover the whole map.")] int x1,
        [Description("Rectangle corner z.")] int z1,
        [Description("Opposite corner x.")] int x2,
        [Description("Opposite corner z.")] int z2,
        [Description("Only items whose defName or label contains this text, e.g. \"steel\" or \"corpse\". Empty string matches everything.")] string filter = "")
    {
        CellRectSpec? area = null;
        bool wholeMap = x1 == -1 && z1 == -1 && x2 == -1 && z2 == -1;
        if (!wholeMap)
        {
            if (x1 < 0 || z1 < 0 || x2 < 0 || z2 < 0)
                return ToolText.Error("pass -1 for all four corners to cover the whole map; otherwise use valid map coordinates.");
            Result<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
            if (!rect.Success) return rect.Error;
            area = rect.Value;
        }
        string where = area.HasValue ? $"in {ToolText.Cell(x1, z1)}-{ToolText.Cell(x2, z2)}" : "on the whole map";
        string what = string.IsNullOrWhiteSpace(filter) ? "items and corpses" : $"items matching \"{filter.Trim()}\"";
        string verb = forbidden ? "forbid" : "allow";

        string blocked = ToolText.Blocked(_gate, $"would {verb} visible {what} {where}.");
        if (blocked != null) return blocked;

        CommandResult r = _world.SetForbidden(area, filter?.Trim() ?? string.Empty, forbidden);
        if (!r.Success) return ToolText.Error(r.Message);
        return (forbidden ? "Forbade " : "Allowed ") + r.Message + ".";
    }

    /// <summary>「wood wall」拆成材料與建築；要先於名稱比對，英文版會直接對到 Wall 而丟掉材料。</summary>
    private (DefInfo Building, string StuffDefName)? SplitMaterial(string building)
    {
        string name = (building ?? string.Empty).Trim();
        int space = name.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0 || _world.Buildables.Any(b => IsNamed(b, name))) return null;
        string bare = name[(space + 1)..].Trim();
        List<DefInfo> matches = [.. _world.Buildables.Where(b => IsNamed(b, bare))];
        if (matches.Count != 1 || _world.GetBuildingInfo(matches[0].DefName) is not { StuffCount: > 0 }) return null;
        Result<string> material = ResolveStuffDefName(name[..space]);
        return material.Success ? (matches[0], material.Value) : null;
    }

    private static bool IsNamed(DefInfo def, string name) =>
        string.Equals(def.DefName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(def.Label, name, StringComparison.OrdinalIgnoreCase);

    private Result<string> ResolveStuffDefName(string stuff)
    {
        if (string.IsNullOrWhiteSpace(stuff)) return Result<string>.Ok(string.Empty);
        Result<DefInfo> stuffDef = DefResolver.Resolve(stuff, _world.Stuffs, "material");
        if (!stuffDef.Success) return Result<string>.Fail(stuffDef.Error);
        return Result<string>.Ok(stuffDef.Value.DefName);
    }

    private static DesignationKind ParseKind(string kind)
    {
        string needle = (kind ?? string.Empty).Trim();
        return Kinds.FirstOrDefault(k => string.Equals(k.Name, needle, StringComparison.OrdinalIgnoreCase));
    }

    private Result<List<ThingInfo>> CollectTargets(ThingKind thingKind, bool useSelection, (int x1, int z1, int x2, int z2) coords, string filter, out int omitted)
    {
        omitted = 0;
        string needle = (filter ?? string.Empty).Trim();
        if (useSelection)
        {
            List<ThingInfo> selected = [.. _world.GetSelection()
                .Where(t => t.Kind == thingKind && (needle.Length == 0 || Matches(t, needle)))];
            if (selected.Count == 0)
            {
                return Result<List<ThingInfo>>.Fail(ToolText.Error(
                    $"the player's selection contains no {thingKind.ToString().ToLowerInvariant()} objects" +
                    (needle.Length > 0 ? $" matching \"{needle}\"." : ". Ask the player to select something, or give a rectangle.")));
            }
            return Result<List<ThingInfo>>.Ok(selected);
        }

        Result<CellRectSpec> rect = TargetResolver.ResolveRect(coords.x1, coords.z1, coords.x2, coords.z2, _world.GetMapBounds());
        if (!rect.Success) return Result<List<ThingInfo>>.Fail(rect.Error);

        // 挖礦照原版可框進迷霧中的岩體（Designator_Mine 接受迷霧格）；其他種類只看得到的東西。
        var query = new ThingQuery
        {
            Kind = thingKind, LabelContains = needle, Area = rect.Value, Limit = 2000,
            IncludeFogged = thingKind == ThingKind.Mineable
        };
        ThingSearchResult search = _world.FindThings(query);
        List<ThingInfo> found = [.. search];
        omitted = Math.Max(0, search.TotalMatches - found.Count);
        if (found.Count == 0)
        {
            return Result<List<ThingInfo>>.Fail(ToolText.Error(
                $"no {thingKind.ToString().ToLowerInvariant()} objects" + (needle.Length > 0 ? $" matching \"{needle}\"" : string.Empty) +
                $" in {rect.Value}. Use find_things to look around first."));
        }
        return Result<List<ThingInfo>>.Ok(found);
    }

    private static bool Matches(ThingInfo t, string needle)
    {
        return t.DefName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
               t.Label.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
