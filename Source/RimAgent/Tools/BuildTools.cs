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
        private const int MaxBlueprintCount = 50;

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

            ResolveResult<List<ThingInfo>> targets = CollectTargets(dk.ThingKind, useSelection, x1, z1, x2, z2, filter);
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

            ResolveResult<List<ThingInfo>> targets = CollectTargets(dk.ThingKind, useSelection, x1, z1, x2, z2, string.Empty);
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

        private sealed class BlueprintLineSpec
        {
            public string DefName { get; set; }
            public string StuffDefName { get; set; }
            public int X { get; set; }
            public int Z { get; set; }
            public int Dx { get; set; }
            public int Dz { get; set; }
            public int Count { get; set; }
            public int Rotation { get; set; }
        }

        [AgentTool("place_blueprint", ToolCategory.Build, ToolRisk.Confirm)]
        [Description("Places blueprints for colonists to build: one building or floor, or a straight line of them (count and direction, e.g. a wall). Reports how many cells the game refused and the first few reasons.")]
        public string PlaceBlueprint(
            [Description("Building or floor name or defName, e.g. \"wall\", \"door\", \"solar generator\", \"bed\".")] string building,
            [Description("x of the first cell.")] int x,
            [Description("z of the first cell.")] int z,
            [Description("0 north, 1 east, 2 south, 3 west; matters for doors, beds and other oriented buildings, otherwise 0.")] int rotation,
            [Description("Material name or defName for buildings made of stuff, e.g. \"wood\", \"steel\", \"granite blocks\"; empty uses the default material.")] string stuff,
            [Description("Blueprints in the line, 1-50; 1 for a single building.")] int count,
            [Description("Line direction when count > 1: north, east, south or west.")] string direction)
        {
            ResolveResult<DefInfo> def = DefResolver.Resolve(building, _world.Buildables, "buildable");
            if (!def.Success) return def.Error;

            ResolveResult<string> stuffResult = ResolveStuffDefName(stuff);
            if (!stuffResult.Success) return stuffResult.Error;
            string stuffDefName = stuffResult.Value;

            string validationError = ValidatePlacementArgs(rotation, count, direction, out int dx, out int dz);
            if (validationError != null) return validationError;

            if (_gate != null && !_gate.AllowCommands())
            {
                return CheckBlueprintPreview(def.Value.Label, stuffDefName, count, direction, x, z);
            }

            var failures = new List<string>();
            var spec = new BlueprintLineSpec
            {
                DefName = def.Value.DefName,
                StuffDefName = stuffDefName,
                X = x,
                Z = z,
                Dx = dx,
                Dz = dz,
                Count = count,
                Rotation = rotation
            };
            int placed = PlaceBlueprintLine(spec, _world.GetMapBounds(), failures);

            string what = def.Value.Label + (stuffDefName.Length > 0 ? $" ({stuffDefName})" : string.Empty);
            if (placed == 0)
            {
                return ToolText.Error($"could not place any {what} blueprint. " + string.Join("; ", failures.Take(5)));
            }

            string summary = $"Placed {placed} {what} blueprint(s) starting at {ToolText.Cell(x, z)}.";
            return failures.Count == 0 ? summary : summary + $" {failures.Count} failed: " + string.Join("; ", failures.Take(5));
        }

        private ResolveResult<string> ResolveStuffDefName(string stuff)
        {
            if (string.IsNullOrWhiteSpace(stuff)) return ResolveResult<string>.Ok(string.Empty);
            ResolveResult<DefInfo> stuffDef = DefResolver.Resolve(stuff, _world.Stuffs, "material");
            if (!stuffDef.Success) return ResolveResult<string>.Fail(stuffDef.Error);
            return ResolveResult<string>.Ok(stuffDef.Value.DefName);
        }

        private static string ValidatePlacementArgs(int rotation, int count, string direction, out int dx, out int dz)
        {
            dx = 0;
            dz = 0;
            if (rotation < 0 || rotation > 3) return ToolText.Error("rotation must be 0 (north), 1 (east), 2 (south) or 3 (west).");
            if (count < 1 || count > MaxBlueprintCount) return ToolText.Error($"count must be between 1 and {MaxBlueprintCount}.");
            if (count > 1 && !TryGetDirectionDelta(direction, out dx, out dz))
            {
                return ToolText.Error($"direction \"{direction}\" is not north, east, south or west.");
            }
            return null;
        }

        private string CheckBlueprintPreview(string label, string stuffDefName, int count, string direction, int x, int z)
        {
            string whatPreview = label + (stuffDefName.Length > 0 ? $" ({stuffDefName})" : string.Empty);
            string linePreview = count > 1 ? $" in a line toward {direction}" : string.Empty;
            string blueprintPreview = $"would place {count} {whatPreview} blueprint(s) " +
                $"starting at {ToolText.Cell(x, z)}{linePreview}.";
            return ToolText.Blocked(_gate, blueprintPreview);
        }

        private static bool TryGetDirectionDelta(string direction, out int dx, out int dz)
        {
            dx = 0;
            dz = 0;
            switch ((direction ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "north": dz = 1; return true;
                case "south": dz = -1; return true;
                case "east": dx = 1; return true;
                case "west": dx = -1; return true;
                default: return false;
            }
        }

        private int PlaceBlueprintLine(BlueprintLineSpec spec, MapBounds bounds, List<string> failures)
        {
            int placed = 0;
            for (int i = 0; i < spec.Count; i++)
            {
                int cx = spec.X + spec.Dx * i;
                int cz = spec.Z + spec.Dz * i;
                string outOfBounds = TargetResolver.CheckCell(cx, cz, bounds);
                if (outOfBounds != null)
                {
                    failures.Add($"{ToolText.Cell(cx, cz)}: outside the map");
                    continue;
                }

                CommandResult r = _world.TryPlaceBlueprint(spec.DefName, cx, cz, spec.Rotation, spec.StuffDefName);
                if (r.Success) placed++;
                else failures.Add($"{ToolText.Cell(cx, cz)}: {r.Message}");
            }
            return placed;
        }

        private static DesignationKind ParseKind(string kind)
        {
            string needle = (kind ?? string.Empty).Trim();
            return Kinds.FirstOrDefault(k => string.Equals(k.Name, needle, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>依「選取」或「矩形＋過濾」收集候選物件。</summary>
        private ResolveResult<List<ThingInfo>> CollectTargets(ThingKind thingKind, bool useSelection, int x1, int z1, int x2, int z2, string filter)
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
                    return ResolveResult<List<ThingInfo>>.Fail(ToolText.Error(
                        $"the player's selection contains no {thingKind.ToString().ToLowerInvariant()} objects" +
                        (needle.Length > 0 ? $" matching \"{needle}\"." : ". Ask the player to select something, or give a rectangle.")));
                }
                return ResolveResult<List<ThingInfo>>.Ok(selected);
            }

            ResolveResult<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
            if (!rect.Success) return ResolveResult<List<ThingInfo>>.Fail(rect.Error);

            var query = new ThingQuery { Kind = thingKind, LabelContains = needle, Area = rect.Value, Limit = 2000 };
            List<ThingInfo> found = _world.FindThings(query).ToList();
            if (found.Count == 0)
            {
                return ResolveResult<List<ThingInfo>>.Fail(ToolText.Error(
                    $"no {thingKind.ToString().ToLowerInvariant()} objects" + (needle.Length > 0 ? $" matching \"{needle}\"" : string.Empty) +
                    $" in {rect.Value}. Use find_things to look around first."));
            }
            return ResolveResult<List<ThingInfo>>.Ok(found);
        }

        private static bool Matches(ThingInfo t, string needle)
        {
            return t.DefName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   t.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
