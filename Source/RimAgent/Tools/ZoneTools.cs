using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>區域：查詢、新增儲藏／種植區、刪除、儲藏篩選。</summary>
public sealed class ZoneTools
{
    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;

    public ZoneTools(IGameWorld world, ConfirmationGate gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
    }

    [AgentTool("list_zones", ToolCategory.Zone, ToolRisk.Query)]
    [Description("Lists stockpile and growing zones with id, label and cell count. Check it before delete_zone or set_zone_filter.")]
    public string ListZones()
    {
        List<ZoneInfo> zones = [.. _world.GetZones()];
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

    [AgentTool("add_zone", ToolCategory.Zone, ToolRisk.Safe)]
    [Description("Creates a stockpile or growing zone over a rectangle. Like dragging in the game, cells that cannot hold the zone (other zones, rock, buildings, fog, infertile ground for growing) are skipped and reported; separated groups of valid cells become separate zones.")]
    public string AddZone(
        [Description("Zone kind: \"stockpile\" or \"growing\".")] string kind,
        [Description("Rectangle corner x.")] int x1,
        [Description("Rectangle corner z.")] int z1,
        [Description("Opposite corner x.")] int x2,
        [Description("Opposite corner z.")] int z2,
        [Description("Plant to grow: name or defName, e.g. \"rice\" or \"Plant_Rice\"; growing zones only. Empty keeps the game's default.")] string plant)
    {
        string canonical = CanonicalKind(kind);
        if (canonical == null) return ToolText.Error($"unknown zone kind \"{kind}\". Use stockpile or growing.");
        if (!string.IsNullOrWhiteSpace(plant) && canonical != "Growing")
        {
            return ToolText.Error("only growing zones take a plant.");
        }

        Result<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
        if (!rect.Success) return rect.Error;

        string plantDef = string.Empty;
        if (!string.IsNullOrWhiteSpace(plant))
        {
            Result<DefInfo> match = DefResolver.Resolve(plant, _world.GrowablePlants, "growable plant");
            if (!match.Success) return match.Error;
            plantDef = match.Value.DefName;
        }

        string growing = plantDef.Length > 0 ? $" growing {plantDef}" : string.Empty;
        string blocked = ToolText.Blocked(_gate, $"would add a {canonical.ToLowerInvariant()} zone over {rect.Value}{growing}.");
        if (blocked != null) return blocked;

        CommandResult result = _world.AddZone(canonical, rect.Value, plantDef);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message;
    }

    [AgentTool("delete_zone", ToolCategory.Zone, ToolRisk.Confirm)]
    [Description("Deletes a zone by id or label. Items in a deleted stockpile stay on the ground.")]
    public string DeleteZone(
        [Description("Zone id in square brackets from list_zones, or its label.")] string zone)
    {
        Result<ZoneInfo> target = ResolveZone(zone, _world.GetZones());
        if (!target.Success) return target.Error;

        string blocked = ToolText.Blocked(_gate, $"would delete zone [{target.Value.Id}] {target.Value.Label} ({target.Value.CellCount} cells).");
        if (blocked != null) return blocked;

        CommandResult result = _world.DeleteZone(target.Value.Id);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message;
    }

    [AgentTool("set_zone_filter", ToolCategory.Zone, ToolRisk.Safe)]
    [Description("Allows and/or disallows items in a stockpile's storage filter, e.g. allow \"corn, rice\", disallow \"steel\". Name at least one item on either side.")]
    public string SetZoneFilter(
        [Description("Zone id in square brackets from list_zones, or its label.")] string zone,
        [Description("Items to allow, comma-separated; empty allows nothing new.")] string allow,
        [Description("Items to disallow, comma-separated; empty disallows nothing.")] string disallow)
    {
        Result<ZoneInfo> target = ResolveZone(zone, _world.GetZones());
        if (!target.Success) return target.Error;

        Result<List<DefInfo>> allows = ResolveItems(allow);
        if (!allows.Success) return allows.Error;
        Result<List<DefInfo>> disallows = ResolveItems(disallow);
        if (!disallows.Success) return disallows.Error;
        if (allows.Value.Count == 0 && disallows.Value.Count == 0)
        {
            return ToolText.Error("nothing to allow or disallow. Name at least one item, e.g. allow \"corn\".");
        }

        var parts = new List<string>();
        if (allows.Value.Count > 0) parts.Add("allow " + string.Join(", ", allows.Value.Select(d => d.Label)));
        if (disallows.Value.Count > 0) parts.Add("disallow " + string.Join(", ", disallows.Value.Select(d => d.Label)));
        string blocked = ToolText.Blocked(_gate, $"would change zone [{target.Value.Id}] {target.Value.Label}'s filter: {string.Join("; ", parts)}.");
        if (blocked != null) return blocked;

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

    private Result<List<DefInfo>> ResolveItems(string input)
    {
        var defs = new List<DefInfo>();
        foreach (string part in (input ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.Trim();
            if (name.Length == 0) continue;
            Result<DefInfo> def = DefResolver.Resolve(name, _world.StorageFilterables, "stored item");
            if (!def.Success) return Result<List<DefInfo>>.Fail(def.Error);
            if (!defs.Any(d => string.Equals(d.DefName, def.Value.DefName, StringComparison.OrdinalIgnoreCase)))
            {
                defs.Add(def.Value);
            }
        }
        return Result<List<DefInfo>>.Ok(defs);
    }

    /// <summary>區域解析：id 精確命中；label 精確或唯一包含，撞名即報錯。</summary>
    internal static Result<ZoneInfo> ResolveZone(string input, IReadOnlyList<ZoneInfo> zones)
    {
        return TargetResolver.ResolveEntity(
            input,
            zones,
            z => z.Id,
            z => new[] { z.Label },
            z => $"[{z.Id}] {z.Label}",
            "zone");
    }
}
