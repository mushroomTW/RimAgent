using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>標記（開採／砍伐／收成／拆除／搬運）與藍圖放置。</summary>
public sealed class BuildTools
{
    /// <summary>一次最多放的藍圖格數（20x20）；再大就分批，免得一句話鋪滿半張地圖。</summary>
    private const int MaxBlueprintCells = 400;

    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;
    private readonly Func<bool> _layoutsEnabled;

    /// <param name="layoutsEnabled">設定頁的「模組化房型」開關；null 視為開啟。</param>
    public BuildTools(IGameWorld world, ConfirmationGate gate = null, Func<bool> layoutsEnabled = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
        _layoutsEnabled = layoutsEnabled ?? (() => true);
    }

    /// <summary>
    /// 房型開關關掉時 <c>place_blueprint</c> 改用的說明：拿掉 layout，免得模型每蓋一間房都先試一次被拒。
    /// 內容與 <see cref="PlaceBlueprint"/> 的 <see cref="DescriptionAttribute"/> 相同，只少了 layout 那段。
    /// </summary>
    private const string PlaceBlueprintWithoutLayoutsDescription = "Places build blueprints. shape: single (one thing at x,z), line (straight row from x,z to x2,z2), fill (every cell of the rectangle, e.g. floors) or outline (its border, e.g. room walls; add a door on the wall afterwards). Use single for things larger than 1x1: x,z is its center cell (an even width or depth leaves the extra cell east/north, and rotation can shift it), and the reply lists the rectangle each blueprint covers. Refused cells give the reason; if space is occupied, also the blocking things with ids and rectangles. Size, cost and rules: lookup_def. Max 400 cells per call.";

    /// <summary>註冊工具時取代 <see cref="DescriptionAttribute"/> 的說明；不需取代時回 null。</summary>
    /// <param name="toolName">工具名稱。</param>
    /// <returns>取代用的說明，或 null 表示沿用屬性上的說明。</returns>
    internal string DescriptionOverride(string toolName)
    {
        return toolName == "place_blueprint" && !_layoutsEnabled() ? PlaceBlueprintWithoutLayoutsDescription : null;
    }

    /// <summary>模型看到的標記種類 → 要找的物件種類、標記 defName、標記是掛在格子還是物件上。</summary>
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
        [Description("Only names or defNames containing this text, e.g. \"steel\"; empty = all.")] string filter)
    {
        DesignationKind dk = ParseKind(kind);
        if (dk == null) return ToolText.Error($"unknown kind \"{kind}\". Use {KindList}.");

        Result<List<ThingInfo>> targets = CollectTargets(dk.ThingKind, useSelection, x1, z1, x2, z2, filter);
        if (!targets.Success) return targets.Error;

        if (_gate != null && !_gate.AllowCommands())
        {
            int eligible = targets.Value.Count(t => dk.Applies(t) && !t.Designations.Contains(dk.DefName, StringComparer.OrdinalIgnoreCase));
            string where = useSelection
                ? "the player's selection"
                : $"rectangle ({x1},{z1})-({x2},{z2})";
            string preview = $"would mark {eligible} object(s) for {dk.Verb} " +
                $"among {targets.Value.Count} matching object(s) in {where}.";
            return ToolText.Blocked(_gate, preview);
        }

        return ExecuteDesignateArea(dk, targets.Value);
    }

    private string ExecuteDesignateArea(DesignationKind dk, List<ThingInfo> targets)
    {
        int marked = 0, skipped = 0, notApplicable = 0;
        var failures = new List<string>();
        foreach (ThingInfo t in targets)
        {
            ApplyDesignationTarget(dk, t, ref marked, ref skipped, ref notApplicable, failures);
        }
        return FormatDesignateSummary(dk, targets.Count, marked, skipped, notApplicable, failures);
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

    private static string FormatDesignateSummary(DesignationKind dk, int totalTargets, int marked, int skipped, int notApplicable, List<string> failures)
    {
        if (marked == 0 && skipped == 0 && failures.Count == 0)
        {
            return ToolText.Error($"nothing eligible for {dk.Verb} among {totalTargets} matching object(s)" +
                                  (notApplicable > 0 ? $" ({notApplicable} not applicable, e.g. not ripe or not player-owned)." : "."));
        }

        string summary = $"Marked {marked} for {dk.Verb}";
        if (skipped > 0) summary += $", {skipped} already marked";
        if (notApplicable > 0) summary += $", {notApplicable} not applicable";
        summary += ".";
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

        Result<List<ThingInfo>> targets = CollectTargets(dk.ThingKind, useSelection, x1, z1, x2, z2, string.Empty);
        if (!targets.Success) return targets.Error;

        List<ThingInfo> markedTargets = [.. targets.Value.Where(t => t.Designations.Contains(dk.DefName, StringComparer.OrdinalIgnoreCase))];
        if (_gate != null && !_gate.AllowCommands())
        {
            string cancelPreview = $"would remove {markedTargets.Count} {dk.Verb} mark(s).";
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

        if (removed == 0 && failures.Count == 0) return $"No {dk.Verb} marks found there.";
        string summary = $"Removed {removed} {dk.Verb} mark(s).";
        return failures.Count == 0 ? summary : summary + " Failures: " + string.Join("; ", failures.Take(5));
    }

    [AgentTool("place_blueprint", ToolCategory.Build, ToolRisk.Confirm)]
    [Description("Places build blueprints. shape: single (one thing at x,z), line (straight row from x,z to x2,z2), fill (every cell of the rectangle, e.g. floors), outline (its border, e.g. room walls; add a door on the wall afterwards) or layout (a whole predesigned room: building = layout name, x,z = its center cell, rotation turns the whole room, stuff = one material for walls, doors and furniture, or set each part: \"walls=granite blocks; floor=fine granite tile; furniture=steel\" (any material or floor in the game, modded ones too; omitted parts keep the layout's own); an empty building lists the layouts). Use single for things larger than 1x1: x,z is its center cell (an even width or depth leaves the extra cell east/north, and rotation can shift it), and the reply lists the rectangle each blueprint covers. Refused cells give the reason; if space is occupied, also the blocking things with ids and rectangles. Size, cost and rules: lookup_def. Max 400 cells per call.")]
#pragma warning disable S107 // Tool parameters are discovered via reflection and exposed as LLM function schema
    public string PlaceBlueprint(
        [Description("Name or defName, e.g. \"wall\", \"door\", \"solar generator\".")] string building,
        [Description("One of the shapes listed in the tool description.")] string shape,
        int x,
        int z,
        [Description("Line end or opposite corner; ignored for single.")] int x2,
        [Description("Line end or opposite corner; ignored for single.")] int z2,
        [Description("0 north, 1 east, 2 south, 3 west (doors, beds). Also turns non-square buildings: east/west swaps width and depth. Otherwise 0.")] int rotation,
        [Description("Material, e.g. \"wood\", \"granite blocks\"; empty = default.")] string stuff)
#pragma warning restore S107
    {
        if (string.Equals(shape?.Trim(), "layout", StringComparison.OrdinalIgnoreCase)) return PlaceLayout(building, x, z, rotation, stuff);

        Result<DefInfo> def = DefResolver.Resolve(building, _world.Buildables, "buildable");
        if (!def.Success) return def.Error;

        Result<string> stuffResult = ResolveStuffDefName(stuff);
        if (!stuffResult.Success) return stuffResult.Error;
        string stuffDefName = stuffResult.Value;

        if (rotation < 0 || rotation > 3) return ToolText.Error("rotation must be 0 (north), 1 (east), 2 (south) or 3 (west).");
        Result<List<(int X, int Z)>> cells = ShapeCells(shape, x, z, x2, z2);
        if (!cells.Success) return cells.Error;

        string what = def.Value.Label + (stuffDefName.Length > 0 ? $" ({stuffDefName})" : string.Empty);
        string where = cells.Value.Count == 1
            ? $"at {ToolText.Cell(x, z)}"
            : $"from {ToolText.Cell(x, z)} to {ToolText.Cell(x2, z2)} ({shape.Trim().ToLowerInvariant()})";
        string blocked = ToolText.Blocked(_gate, $"would place {cells.Value.Count} {what} blueprint(s) {where}.");
        if (blocked != null) return blocked;

        MapBounds bounds = _world.GetMapBounds();
        BuildingInfo info = _world.GetBuildingInfo(def.Value.DefName);
        bool large = info != null && (info.Width > 1 || info.Height > 1);
        var failures = new List<string>();
        var placedCells = new List<(int X, int Z)>();
        foreach ((int cx, int cz) in cells.Value)
        {
            if (TargetResolver.CheckCell(cx, cz, bounds) != null)
            {
                failures.Add($"{ToolText.Cell(cx, cz)}: outside the map");
                continue;
            }
            CommandResult r = _world.TryPlaceBlueprint(def.Value.DefName, cx, cz, rotation, stuffDefName);
            if (r.Success) placedCells.Add((cx, cz));
            else failures.Add($"{ToolText.Cell(cx, cz)}{(large ? " (would cover " + OccupiedRect(cx, cz, rotation, info) + ")" : string.Empty)}: {r.Message}");
        }

        int placed = placedCells.Count;
        if (placed == 0)
        {
            return ToolText.Error($"could not place any {what} blueprint. " + string.Join("; ", failures.Take(5)));
        }
        string summary = $"Placed {placed} {what} blueprint(s) {where}." + (large ? FootprintNote(info, placedCells, rotation) : string.Empty);
        return failures.Count == 0 ? summary : summary + $" {failures.Count} failed: " + string.Join("; ", failures.Take(5));
    }

    /// <summary>失敗原因最多列幾種；同一種東西同一個原因合併計數。</summary>
    private const int MaxLayoutFailuresListed = 6;

    /// <summary>
    /// 整組放一個房型：先建築後地板，逐件走一般藍圖放置（同樣的放置檢查與失敗原因）。
    /// 牆（含門）、地板、家具可各自指定材料；不能用的那件退回房型原本的設計。
    /// </summary>
    private string PlaceLayout(string layout, int x, int z, int rotation, string stuff)
    {
        if (!_layoutsEnabled()) return ToolText.Error("room layouts are turned off in the RimAgent settings; build with the other shapes instead.");
        IReadOnlyList<LayoutInfo> layouts = _world.Layouts;
        if (layouts.Count == 0) return ToolText.Error("no room layouts are installed.");

        if (string.IsNullOrWhiteSpace(layout))
        {
            return "Layouts (size, then what it is): " + string.Join("; ",
                layouts.Select(l => $"{l.Label} ({l.DefName}) {l.Width}x{l.Height}" + (l.Description.Length > 0 ? ": " + l.Description : string.Empty))) + ".";
        }
        Result<DefInfo> chosen = DefResolver.Resolve(layout, [.. layouts.Select(l => new DefInfo(l.DefName, l.Label))], "layout");
        // 名稱打錯只附名稱與尺寸，完整說明用空名稱查。
        if (!chosen.Success) return chosen.Error + " Layouts: " + string.Join("; ", layouts.Select(l => $"{l.Label} {l.Width}x{l.Height}")) + ".";

        if (rotation < 0 || rotation > 3) return ToolText.Error("rotation must be 0 (north), 1 (east), 2 (south) or 3 (west).");
        Result<LayoutMaterials> materials = ResolveLayoutMaterials(stuff);
        if (!materials.Success) return materials.Error;

        LayoutPlan plan = _world.GetLayoutPlan(chosen.Value.DefName, x, z, rotation);
        if (plan == null) return ToolText.Error($"no data for layout {chosen.Value.DefName}.");
        MapBounds bounds = _world.GetMapBounds();
        if (TargetResolver.CheckCell(plan.Rect.MinX, plan.Rect.MinZ, bounds) != null || TargetResolver.CheckCell(plan.Rect.MaxX, plan.Rect.MaxZ, bounds) != null)
        {
            return ToolText.Error($"{chosen.Value.Label} centered at {ToolText.Cell(x, z)} would cover {plan.Rect}, which leaves the map.");
        }

        string chosenMaterials = materials.Value.Describe();
        string what = chosen.Value.Label + (chosenMaterials.Length > 0 ? $" ({chosenMaterials})" : string.Empty);
        string blocked = ToolText.Blocked(_gate, $"would place the {what} layout: {plan.Pieces.Count} blueprints covering {plan.Rect}.");
        if (blocked != null) return blocked;

        int placed = 0;
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var fellBack = new Dictionary<string, int>(StringComparer.Ordinal);
        var infos = new Dictionary<string, BuildingInfo>(StringComparer.Ordinal);
        BuildingInfo Info(string defName) => infos.TryGetValue(defName, out BuildingInfo i) ? i : infos[defName] = _world.GetBuildingInfo(defName);
        foreach (LayoutPiece piece in plan.Pieces)
        {
            CommandResult r = PlaceLayoutPiece(piece, materials.Value.For(piece.Part), Info(piece.DefName), out bool usedOwnStuff);
            string label = Info(piece.DefName)?.Label ?? piece.DefName;
            if (r.Success)
            {
                placed++;
                if (usedOwnStuff) fellBack[label] = fellBack.TryGetValue(label, out int k) ? k + 1 : 1;
                continue;
            }
            string key = $"{label}: {r.Message}";
            failures[key] = failures.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        if (placed == 0)
        {
            return ToolText.Error($"could not place any part of the {what} layout in {plan.Rect}. " + FormatLayoutFailures(failures));
        }
        string summary = $"Placed {placed} of {plan.Pieces.Count} blueprints of the {what} layout, covering {plan.Rect}.";
        if (fellBack.Count > 0) summary += " Kept the layout's own material or floor (the requested one was refused): " + FormatLayoutFailures(fellBack);
        return failures.Count == 0 ? summary : summary + " Not placed: " + FormatLayoutFailures(failures);
    }

    /// <summary>放一件房型內容；有指定替換時先試替換，被拒再退回房型原本的設計。</summary>
    /// <param name="piece">要放的一件。</param>
    /// <param name="replacement">這一類指定的材料 defName（地板則是地板 defName）；空字串表示不替換。</param>
    /// <param name="info">這件的建造資訊；查不到時為 null。</param>
    /// <param name="usedOwnStuff">指定的替換被拒、改用房型原本的設計放成功時為 true。</param>
    /// <returns>放置結果；替換與原設計都失敗時回替換的失敗原因。</returns>
    private CommandResult PlaceLayoutPiece(LayoutPiece piece, string replacement, BuildingInfo info, out bool usedOwnStuff)
    {
        usedOwnStuff = false;
        bool floor = piece.Part == LayoutPart.Floor;
        bool skip = replacement.Length == 0
            || (floor ? replacement == piece.DefName : replacement == piece.StuffDefName || info == null || info.StuffCount == 0);
        if (skip) return _world.TryPlaceBlueprint(piece.DefName, piece.X, piece.Z, piece.Rotation, piece.StuffDefName);

        // 指定的材料或地板這件不能用（例如某些家具不收石磚、重地板要承重地基）時，退回房型原本的設計。
        CommandResult r = floor
            ? _world.TryPlaceBlueprint(replacement, piece.X, piece.Z, piece.Rotation, string.Empty)
            : _world.TryPlaceBlueprint(piece.DefName, piece.X, piece.Z, piece.Rotation, replacement);
        if (r.Success) return r;
        CommandResult own = _world.TryPlaceBlueprint(piece.DefName, piece.X, piece.Z, piece.Rotation, piece.StuffDefName);
        usedOwnStuff = own.Success;
        return own.Success ? own : r;
    }

    /// <summary>房型三類各自指定的替換；空字串表示該類照房型原本的設計。</summary>
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

        /// <summary>回覆與確認訊息用，只列有指定的類別。</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Walls.Length > 0) parts.Add("walls " + Walls);
            if (Floor.Length > 0) parts.Add("floor " + Floor);
            if (Furniture.Length > 0) parts.Add("furniture " + Furniture);
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// 解析房型的 stuff：單一材料同時套用到牆與家具（地板不變）；
    /// 或用 <c>walls=…; floor=…; furniture=…</c> 各自指定，省略的類別照房型原本的設計。
    /// 材料從遊戲全部可用材料、地板從全部可建地板中解析，模組加入的也算。
    /// </summary>
    /// <param name="stuff">模型給的 stuff 參數。</param>
    /// <returns>解析好的三類替換，或第一個解析失敗的錯誤。</returns>
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
            int sep = entry.IndexOfAny(['=', ':']);
            string key = sep < 0 ? string.Empty : entry.Substring(0, sep).Trim().ToLowerInvariant();
            string value = sep < 0 ? string.Empty : entry.Substring(sep + 1).Trim();
            switch (key)
            {
                case "wall":
                case "walls":
                case "furniture":
                    Result<string> material = ResolveStuffDefName(value);
                    if (!material.Success) return Result<LayoutMaterials>.Fail(material.Error);
                    if (key == "furniture") result.Furniture = material.Value;
                    else result.Walls = material.Value;
                    break;
                case "floor":
                case "floors":
                    if (value.Length == 0) break;
                    Result<DefInfo> floor = DefResolver.Resolve(value, _world.Buildables, "floor");
                    if (!floor.Success) return Result<LayoutMaterials>.Fail(floor.Error);
                    if (_world.GetBuildingInfo(floor.Value.DefName)?.IsTerrain != true)
                    {
                        return Result<LayoutMaterials>.Fail(ToolText.Error($"{floor.Value.Label} is not a floor."));
                    }
                    result.Floor = floor.Value.DefName;
                    break;
                default:
                    return Result<LayoutMaterials>.Fail(ToolText.Error($"unknown part \"{entry.Trim()}\" in stuff; use walls=...; floor=...; furniture=..."));
            }
        }
        return Result<LayoutMaterials>.Ok(result);
    }

    private static string FormatLayoutFailures(Dictionary<string, int> failures)
    {
        IEnumerable<string> listed = failures.OrderByDescending(f => f.Value).Take(MaxLayoutFailuresListed)
            .Select(f => f.Value > 1 ? $"{f.Key} (x{f.Value})" : f.Key);
        string more = failures.Count > MaxLayoutFailuresListed ? $"; +{failures.Count - MaxLayoutFailuresListed} more kinds" : string.Empty;
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
            + (removed.Count > MaxFootprintsListed ? $"; +{removed.Count - MaxFootprintsListed} more." : ".");
    }

    /// <summary>最多逐一列出幾個藍圖的佔格；更多的只報數量。</summary>
    private const int MaxFootprintsListed = 5;

    /// <summary>
    /// 一個建築放在 (x,z)、朝向 rotation 時佔的矩形；與 GenAdj.OccupiedRect 同一公式（遊戲內測試逐一對照）：
    /// 朝東西時寬深對調；(x,z) 是中心格，偶數邊長多出的那格落在東／北側，旋轉再依方向挪一格。
    /// </summary>
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
        if (rotation == 1) shiftZ = -1;
        else if (rotation == 2) { shiftX = -1; shiftZ = -1; }
        else if (rotation == 3) shiftX = -1;
        int cx = x + (w % 2 == 0 ? shiftX : 0);
        int cz = z + (h % 2 == 0 ? shiftZ : 0);
        int minX = cx - (w - 1) / 2, minZ = cz - (h - 1) / 2;
        return new CellRectSpec(minX, minZ, minX + w - 1, minZ + h - 1);
    }

    /// <summary>成功訊息裡大於 1x1 的建築的佔格：單一藍圖直接寫範圍，多個則逐一列出中心與範圍。</summary>
    private static string FootprintNote(BuildingInfo info, List<(int X, int Z)> placedCells, int rotation)
    {
        CellRectSpec first = OccupiedRect(placedCells[0].X, placedCells[0].Z, rotation, info);
        string size = $"{first.MaxX - first.MinX + 1}x{first.MaxZ - first.MinZ + 1}";
        if (placedCells.Count == 1) return $" Size {size}, rotation {rotation}, covers {first}.";
        IEnumerable<string> listed = placedCells.Take(MaxFootprintsListed)
            .Select(c => $"{ToolText.Cell(c.X, c.Z)} covers {OccupiedRect(c.X, c.Z, rotation, info)}");
        string more = placedCells.Count > MaxFootprintsListed ? $"; +{placedCells.Count - MaxFootprintsListed} more" : string.Empty;
        return $" Each is {size}, rotation {rotation}: {string.Join("; ", listed)}{more}.";
    }

    /// <summary>PlaceWorker 類別 → 模型看得懂的限制；沒收錄的只回類別名。</summary>
    private static readonly Dictionary<string, string> PlaceWorkerRules = new(StringComparer.Ordinal)
    {
        ["PlaceWorker_NotUnderRoof"] = "must not be under a roof",
        ["Placeworker_OnlyUnderThickRoof"] = "must be under a thick rock roof",
        ["PlaceWorker_MustBeIndoors"] = "must be in an enclosed room, not open to the map edge",
        ["Placeworker_AttachedToWall"] = "must be attached to a wall (rotation points at the wall)",
        ["PlaceWorker_OnSteamGeyser"] = "must sit on a steam geyser"
    };

    /// <summary>預設輸出的數值列上限；整個類別放得下才放，其餘只列類別名稱讓模型用 detail 再查。</summary>
    private const int DefaultStatBudget = 30;
    private const int MaxDescriptionChars = 800;

    [AgentTool("lookup_def", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Game encyclopedia, including modded content: looks up an item, weapon, apparel, building, plant, animal, floor, research project or health condition by name or defName. Reports its description and info-card stats; how to get it (recipes with workbench, ingredients, skill and research; harvest, mining, animals, traders); what it is an ingredient of or used to build; research status, prerequisites and unlocks; hediff stages and lethality. Buildables also get size and rotation, cost, ground support needed (H/m/l in view_map_grid layer=build), builder skill, placement rules, ship part, power and interaction cell: use it before planning a layout.")]
    public string LookupDef(
        [Description("Name or defName, e.g. \"component\", \"ship reactor\", \"Ship_Engine\", \"plague\", \"microelectronics\".")] string name,
        [Description("Optional: thing, terrain, research or hediff to narrow the search. Empty searches all four.")] string kind = "",
        [Description("Optional: a stat group to show in full, named as listed under \"More stat groups\", or \"all\". Empty shows the main groups.")] string detail = "")
    {
        string wanted = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (wanted.Length > 0 && !DefLookupKind.All.Contains(wanted))
        {
            return ToolText.Error($"unknown kind \"{kind}\"; use {string.Join(", ", DefLookupKind.All)} or leave it empty.");
        }

        // 各種類的目錄合併後一起比對，名稱層級（精確 → 前綴 → 包含）才是全域的；再以參考對回種類。
        var kindOf = new Dictionary<DefInfo, string>();
        var candidates = new List<DefInfo>();
        foreach (string k in wanted.Length > 0 ? new[] { wanted } : DefLookupKind.All)
        {
            foreach (DefInfo d in _world.GetLookupCatalog(k))
            {
                kindOf[d] = k;
                candidates.Add(d);
            }
        }
        Result<DefInfo> def = DefResolver.Resolve(name, candidates, wanted.Length > 0 ? wanted : "def");
        if (!def.Success) return def.Error;
        string resolvedKind = kindOf[def.Value];
        DefLookupInfo info = _world.LookupDef(resolvedKind, def.Value.DefName);
        if (info == null) return ToolText.Error($"no data for {def.Value.DefName}.");

        var sb = new System.Text.StringBuilder();
        sb.Append(info.Label).Append(" (").Append(info.DefName).Append("), ").AppendLine(resolvedKind);
        if (info.Description.Length > 0)
        {
            sb.AppendLine(info.Description.Length > MaxDescriptionChars ? info.Description.Substring(0, MaxDescriptionChars) + "…" : info.Description);
        }
        AppendStats(sb, info.Stats, (detail ?? string.Empty).Trim());
        if (info.Facts.Count > 0)
        {
            sb.AppendLine("Facts:");
            foreach (string fact in info.Facts) sb.Append("- ").AppendLine(fact);
        }
        if (resolvedKind is DefLookupKind.Thing or DefLookupKind.Terrain && _world.Buildables.Any(b => b.DefName == info.DefName))
        {
            BuildingInfo building = _world.GetBuildingInfo(info.DefName);
            if (building != null) sb.AppendLine("Building:").AppendLine(FormatBuildingInfo(building));
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// 依類別分行輸出數值。detail 空白時依序放整個類別直到超過預算（第一個類別一定放）；
    /// 指定類別（defName 或顯示名稱）只放那一類；all 全放。沒放的類別列在最後。
    /// </summary>
    private static void AppendStats(System.Text.StringBuilder sb, IReadOnlyList<DefStatEntry> stats, string detail)
    {
        if (stats.Count == 0) return;
        // 以顯示名稱分組：原版的 Basics、BasicsImportant、BasicsNonPawn 等類別同名，分開列會重複出現好幾行。
        List<IGrouping<string, DefStatEntry>> groups = [.. stats.GroupBy(s => s.CategoryLabel)];
        bool all = string.Equals(detail, "all", StringComparison.OrdinalIgnoreCase);
        var shown = new List<IGrouping<string, DefStatEntry>>();
        if (all) shown.AddRange(groups);
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

        if (shown.Count > 0)
        {
            sb.AppendLine("Stats:");
            foreach (IGrouping<string, DefStatEntry> g in shown)
            {
                sb.Append('[').Append(g.Key).Append("] ")
                  .AppendLine(string.Join("; ", g.Select(s => s.Value.Length > 0 ? $"{s.Label}: {s.Value}" : s.Label)));
            }
        }
        List<IGrouping<string, DefStatEntry>> hidden = [.. groups.Where(g => !shown.Contains(g))];
        if (hidden.Count > 0)
        {
            sb.Append("More stat groups (pass detail=<name> or \"all\"): ")
              .AppendLine(string.Join(", ", hidden.Select(g => $"{g.Key} ({g.Count()})")) + ".");
        }
    }

    private static string FormatBuildingInfo(BuildingInfo b)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Size: ").Append(b.Width).Append('x').Append(b.Height).Append(" (x by z, facing north). ");
        sb.AppendLine(b.Width == b.Height
            ? "Rotation does not change the footprint."
            : $"Rotation 1 or 3 (east/west) makes it {b.Height}x{b.Width}.");
        if (b.Width > 1 || b.Height > 1) sb.AppendLine("place_blueprint x,z is the center cell; its reply lists the covered rectangle.");

        var cost = new List<string>(b.Costs.Select(c => $"{c.Label} x{c.Count}"));
        if (b.StuffCount > 0) cost.Add($"{b.StuffCount} of a material ({string.Join(", ", b.StuffCategories)})");
        sb.Append("Cost: ").AppendLine(cost.Count == 0 ? "none" : string.Join(", ", cost));

        if (b.TerrainAffordance.Length > 0)
        {
            sb.Append("Ground: needs ").Append(b.TerrainAffordance).Append(" support under every covered cell")
              .AppendLine(b.AffordanceFromStuff ? " (may depend on the material)." : ".");
        }
        if (b.Research.Count == 0) sb.AppendLine("Research: none.");
        else sb.Append("Research: ").AppendLine(string.Join(", ", b.Research.Select(r => r.Label + (r.Done ? " (done)" : " (NOT done)"))));
        if (b.ConstructionSkill > 0 || b.ArtisticSkill > 0)
        {
            var skills = new List<string>();
            if (b.ConstructionSkill > 0) skills.Add($"construction {b.ConstructionSkill}");
            if (b.ArtisticSkill > 0) skills.Add($"artistic {b.ArtisticSkill}");
            sb.Append("Builder skill: ").Append(string.Join(", ", skills)).AppendLine(".");
        }
        if (b.PlaceWorkers.Count > 0)
        {
            sb.Append("Placement: ").AppendLine(string.Join("; ", b.PlaceWorkers.Select(w =>
                PlaceWorkerRules.TryGetValue(w, out string rule) ? w + " -> " + rule : w)));
        }
        if (b.ShipPart) sb.AppendLine("Ship part: counts toward the starship (must touch the others edge to edge; check_ship_layout verifies).");
        if (b.PowerWatts > 0) sb.Append("Power: uses ").Append(ToolText.Num(b.PowerWatts)).AppendLine(" W.");
        else if (b.PowerWatts < 0) sb.Append("Power: produces ").Append(ToolText.Num(-b.PowerWatts)).AppendLine(" W.");
        if (b.InteractionOffsets.Count > 0)
        {
            sb.Append("Interaction cell: offset ").Append(string.Join(", ", b.InteractionOffsets.Select(o => ToolText.Cell(o.X, o.Z))))
              .AppendLine(" from the center when facing north (turns with rotation); keep it free.");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>形狀展開成格子；line 照模型給的起點走向終點，fill／outline 由下往上逐列。</summary>
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
        if (kind == "line" && x != x2 && z != z2)
        {
            return Result<List<(int X, int Z)>>.Fail(ToolText.Error("a line must be straight: keep x or z the same at both ends."));
        }
        int minX = Math.Min(x, x2), maxX = Math.Max(x, x2), minZ = Math.Min(z, z2), maxZ = Math.Max(z, z2);
        long width = maxX - minX + 1L, height = maxZ - minZ + 1L;
        long count = kind == "outline" && width > 2 && height > 2 ? 2 * (width + height) - 4 : width * height;
        if (count > MaxBlueprintCells)
        {
            return Result<List<(int X, int Z)>>.Fail(ToolText.Error($"that {kind} covers {count} cells; the limit is {MaxBlueprintCells} per call. Split it into smaller pieces."));
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
        [Description("Only items whose defName or label contains this text, e.g. \"steel\" or \"corpse\". Empty string matches everything.")] string filter)
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

    /// <summary>依「選取」或「矩形＋過濾」收集候選物件。</summary>
    private Result<List<ThingInfo>> CollectTargets(ThingKind thingKind, bool useSelection, int x1, int z1, int x2, int z2, string filter)
    {
        string needle = (filter ?? string.Empty).Trim();
        if (useSelection)
        {
            List<ThingInfo> selected = [.. _world.GetSelection()
                .Where(t => t.Kind == thingKind)
                .Where(t => needle.Length == 0 || Matches(t, needle))];
            if (selected.Count == 0)
            {
                return Result<List<ThingInfo>>.Fail(ToolText.Error(
                    $"the player's selection contains no {thingKind.ToString().ToLowerInvariant()} objects" +
                    (needle.Length > 0 ? $" matching \"{needle}\"." : ". Ask the player to select something, or give a rectangle.")));
            }
            return Result<List<ThingInfo>>.Ok(selected);
        }

        Result<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
        if (!rect.Success) return Result<List<ThingInfo>>.Fail(rect.Error);

        var query = new ThingQuery { Kind = thingKind, LabelContains = needle, Area = rect.Value, Limit = 2000 };
        List<ThingInfo> found = [.. _world.FindThings(query)];
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
        return t.DefName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
               t.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
