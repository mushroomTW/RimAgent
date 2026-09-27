using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>標記（開採／砍伐／收成／拆除／搬運）與藍圖放置。</summary>
    public sealed class BuildTools
    {
        /// <summary>一次最多放的藍圖格數（20x20）；再大就分批，免得一句話鋪滿半張地圖。</summary>
        private const int MaxBlueprintCells = 400;

        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public BuildTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
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
        {
            new DesignationKind { Name = "mine", ThingKind = ThingKind.Mineable, DefName = "Mine", OnCell = true, Applies = t => true, Verb = "mining" },
            new DesignationKind { Name = "cut_plants", ThingKind = ThingKind.Plant, DefName = "CutPlant", OnCell = false, Applies = t => true, Verb = "cutting" },
            new DesignationKind { Name = "harvest", ThingKind = ThingKind.Plant, DefName = "HarvestPlant", OnCell = false, Applies = t => t.Harvestable, Verb = "harvesting" },
            new DesignationKind { Name = "deconstruct", ThingKind = ThingKind.Building, DefName = "Deconstruct", OnCell = false, Applies = t => t.PlayerOwned, Verb = "deconstruction" },
            new DesignationKind { Name = "haul", ThingKind = ThingKind.Item, DefName = "Haul", OnCell = false, Applies = t => true, Verb = "hauling" },
            new DesignationKind { Name = "hunt", ThingKind = ThingKind.Pawn, DefName = "Hunt", OnCell = false, Applies = t => t.Wild, Verb = "hunting" }
        };

        private const string KindList = "mine, cut_plants, harvest, deconstruct, haul or hunt";

        [AgentTool("designate_area", ToolCategory.Build, ToolRisk.Confirm)]
        [Description("Marks map objects for colonists: mine (ore and rock), cut_plants, harvest (ripe plants only), deconstruct (player-owned buildings only), haul (items) or hunt (wild animals). Targets the player's current selection or a rectangle, optionally filtered by name; reports how many were marked, already marked and not applicable.")]
        public string DesignateArea(
            [Description("mine, cut_plants, harvest, deconstruct, haul or hunt.")] string kind,
            [Description("true to use the player's current map selection instead of a rectangle.")] bool useSelection,
            [Description("Rectangle corner x (ignored when useSelection is true).")] int x1,
            [Description("Rectangle corner z.")] int z1,
            [Description("Opposite corner x.")] int x2,
            [Description("Opposite corner z.")] int z2,
            [Description("Mark only objects whose defName or label contains this text, e.g. \"steel\" or \"tree\"; empty marks everything of that kind.")] string filter)
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

            List<ThingInfo> markedTargets = targets.Value.Where(t => t.Designations.Contains(dk.DefName, StringComparer.OrdinalIgnoreCase)).ToList();
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
        [Description("Places blueprints for colonists to build. shape single places one building or floor at (x, z); line places a straight row from (x, z) to (x2, z2); fill covers every cell of the rectangle (floors, ship structural beams); outline places only its border (the walls of a room; put a door on the wall line afterwards). line, fill and outline suit 1x1 things; place larger buildings one at a time. Up to 400 cells per call. Reports how many cells the game refused and the first few reasons.")]
#pragma warning disable S107 // Tool parameters are discovered via reflection and exposed as LLM function schema
        public string PlaceBlueprint(
            [Description("Building or floor name or defName, e.g. \"wall\", \"door\", \"solar generator\", \"ship structural beam\".")] string building,
            [Description("single, line, fill or outline.")] string shape,
            [Description("x of the cell, line start or rectangle corner.")] int x,
            [Description("z of the cell, line start or rectangle corner.")] int z,
            [Description("x of the line end or opposite corner; ignored for single.")] int x2,
            [Description("z of the line end or opposite corner; ignored for single.")] int z2,
            [Description("0 north, 1 east, 2 south, 3 west; matters for doors, beds and other oriented buildings, otherwise 0.")] int rotation,
            [Description("Material name or defName for buildings made of stuff, e.g. \"wood\", \"steel\", \"granite blocks\"; empty uses the default material.")] string stuff)
#pragma warning restore S107
        {
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
            var failures = new List<string>();
            int placed = 0;
            foreach ((int cx, int cz) in cells.Value)
            {
                if (TargetResolver.CheckCell(cx, cz, bounds) != null)
                {
                    failures.Add($"{ToolText.Cell(cx, cz)}: outside the map");
                    continue;
                }
                CommandResult r = _world.TryPlaceBlueprint(def.Value.DefName, cx, cz, rotation, stuffDefName);
                if (r.Success) placed++;
                else failures.Add($"{ToolText.Cell(cx, cz)}: {r.Message}");
            }

            if (placed == 0)
            {
                return ToolText.Error($"could not place any {what} blueprint. " + string.Join("; ", failures.Take(5)));
            }
            string summary = $"Placed {placed} {what} blueprint(s) {where}.";
            return failures.Count == 0 ? summary : summary + $" {failures.Count} failed: " + string.Join("; ", failures.Take(5));
        }

        /// <summary>形狀展開成格子；line 照模型給的起點走向終點，fill／outline 由下往上逐列。</summary>
        private static Result<List<(int X, int Z)>> ShapeCells(string shape, int x, int z, int x2, int z2)
        {
            string kind = shape?.Trim().ToLowerInvariant() ?? string.Empty;
            if (kind == "single")
            {
                return Result<List<(int X, int Z)>>.Ok(new List<(int X, int Z)> { (x, z) });
            }
            if (kind != "line" && kind != "fill" && kind != "outline")
            {
                return Result<List<(int X, int Z)>>.Fail(ToolText.Error($"unknown shape \"{shape}\". Use single, line, fill or outline."));
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
                List<ThingInfo> selected = _world.GetSelection()
                    .Where(t => t.Kind == thingKind)
                    .Where(t => needle.Length == 0 || Matches(t, needle))
                    .ToList();
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
            List<ThingInfo> found = _world.FindThings(query).ToList();
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
}
