using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>區域：查詢、新增儲藏／種植區、刪除、儲藏篩選。</summary>
    public sealed class ZoneTools
    {
        private readonly IGameWorld _world;

        public ZoneTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("list_zones", ToolCategory.Zone)]
        [Description("Lists the zones on the map: stockpiles and growing zones with their labels and cell counts. Use this before deleting a zone or changing a stockpile filter.")]
        public string ListZones()
        {
            List<ZoneInfo> zones = _world.GetZones().ToList();
            if (zones.Count == 0) return "There are no zones on the map.";

            var sb = new StringBuilder();
            sb.Append(zones.Count == 1 ? "1 zone:" : zones.Count + " zones:").AppendLine();
            foreach (ZoneInfo z in zones)
            {
                sb.Append("[").Append(z.Id).Append("] ").Append(z.Label);
                sb.Append(" (").Append(z.Kind).Append(", ").Append(z.CellCount).Append(z.CellCount == 1 ? " cell)" : " cells)").AppendLine();
            }
            return sb.ToString().Trim();
        }

        [AgentTool("add_zone", ToolCategory.Zone)]
        [Description("Adds a stockpile or growing zone over a map rectangle. Every cell must be inside the map and free of other zones, otherwise nothing is built.")]
        public string AddZone(
            [Description("Zone kind: \"stockpile\" or \"growing\".")] string kind,
            [Description("Rectangle corner x.")] int x1,
            [Description("Rectangle corner z.")] int z1,
            [Description("Opposite corner x.")] int x2,
            [Description("Opposite corner z.")] int z2,
            [Description("Plant to grow, e.g. \"rice\". Empty string keeps the game's default. Growing zones only.")] string plant)
        {
            string canonical = CanonicalKind(kind);
            if (canonical == null) return ToolText.Error($"unknown zone kind \"{kind}\". Use stockpile or growing.");
            if (!string.IsNullOrWhiteSpace(plant) && canonical != "Growing")
            {
                return ToolText.Error("only growing zones take a plant.");
            }

            ResolveResult<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
            if (!rect.Success) return rect.Error;

            string plantDef = string.Empty;
            if (!string.IsNullOrWhiteSpace(plant))
            {
                plantDef = (plant ?? string.Empty).Trim();
            }

            CommandResult result = _world.AddZone(canonical, rect.Value, plantDef);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("delete_zone", ToolCategory.Zone)]
        [Description("Deletes a zone by its id or label. Stockpiled items stay on the ground.")]
        public string DeleteZone(
            [Description("Zone id in square brackets from list_zones, or its label.")] string zone)
        {
            ResolveResult<ZoneInfo> target = ResolveZone(zone, _world.GetZones());
            if (!target.Success) return target.Error;

            CommandResult result = _world.DeleteZone(target.Value.Id);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("set_zone_filter", ToolCategory.Zone)]
        [Description("Allows or disallows items in a stockpile zone's storage filter, e.g. allow \"corn, rice\", disallow \"steel\". At least one side must name something.")]
        public string SetZoneFilter(
            [Description("Zone id in square brackets from list_zones, or its label.")] string zone,
            [Description("Items to allow, e.g. \"corn, rice\". Empty string allows nothing new.")] string allow,
            [Description("Items to disallow, e.g. \"steel\". Empty string disallows nothing.")] string disallow)
        {
            ResolveResult<ZoneInfo> target = ResolveZone(zone, _world.GetZones());
            if (!target.Success) return target.Error;

            ResolveResult<List<DefInfo>> allows = ResolveItems(allow, "allow");
            if (!allows.Success) return allows.Error;
            ResolveResult<List<DefInfo>> disallows = ResolveItems(disallow, "disallow");
            if (!disallows.Success) return disallows.Error;
            if (allows.Value.Count == 0 && disallows.Value.Count == 0)
            {
                return ToolText.Error("nothing to allow or disallow. Name at least one item, e.g. allow \"corn\".");
            }

            CommandResult result = _world.SetZoneFilter(
                target.Value.Id,
                allows.Value.Select(d => d.DefName).ToList(),
                disallows.Value.Select(d => d.DefName).ToList());
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        private static string CanonicalKind(string kind)
        {
            string needle = (kind ?? string.Empty).Trim();
            if (string.Equals(needle, "stockpile", StringComparison.OrdinalIgnoreCase)) return "Stockpile";
            if (string.Equals(needle, "growing", StringComparison.OrdinalIgnoreCase)) return "Growing";
            return null;
        }

        private ResolveResult<List<DefInfo>> ResolveItems(string input, string side)
        {
            var defs = new List<DefInfo>();
            foreach (string part in (input ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (name.Length == 0) continue;
                ResolveResult<DefInfo> def = DefResolver.Resolve(name, _world.StorageFilterables, "stored item");
                if (!def.Success) return ResolveResult<List<DefInfo>>.Fail(def.Error);
                if (!defs.Any(d => string.Equals(d.DefName, def.Value.DefName, StringComparison.OrdinalIgnoreCase)))
                {
                    defs.Add(def.Value);
                }
            }
            return ResolveResult<List<DefInfo>>.Ok(defs);
        }

        /// <summary>區域解析：id 精確命中；label 精確或唯一包含，撞名即報錯。</summary>
        internal static ResolveResult<ZoneInfo> ResolveZone(string input, IReadOnlyList<ZoneInfo> zones)
        {
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return ResolveResult<ZoneInfo>.Fail("Error: zone is empty. Give an id or label.");
            }

            if (int.TryParse(needle, out int id))
            {
                ZoneInfo byId = zones.FirstOrDefault(z => z.Id == id);
                return byId != null
                    ? ResolveResult<ZoneInfo>.Ok(byId)
                    : ResolveResult<ZoneInfo>.Fail($"Error: no zone has id {id}. {ListAll(zones)}");
            }

            List<ZoneInfo> exact = zones.Where(z => string.Equals(z.Label, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return ResolveResult<ZoneInfo>.Ok(exact[0]);
            if (exact.Count > 1)
            {
                return ResolveResult<ZoneInfo>.Fail(
                    $"Error: \"{needle}\" matches several zones: {string.Join(", ", exact.Select(Describe))}. Use the id.");
            }

            List<ZoneInfo> contains = zones.Where(z => z.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (contains.Count == 1) return ResolveResult<ZoneInfo>.Ok(contains[0]);
            if (contains.Count > 1)
            {
                return ResolveResult<ZoneInfo>.Fail(
                    $"Error: \"{needle}\" matches several zones: {string.Join(", ", contains.Select(Describe))}. Use the id.");
            }

            return ResolveResult<ZoneInfo>.Fail($"Error: no zone matches \"{needle}\". {ListAll(zones)}");
        }

        private static string Describe(ZoneInfo z)
        {
            return $"[{z.Id}] {z.Label}";
        }

        private static string ListAll(IReadOnlyList<ZoneInfo> zones)
        {
            return zones.Count == 0
                ? "There are no zones."
                : "Zones: " + string.Join(", ", zones.Select(Describe)) + ".";
        }
    }
}
