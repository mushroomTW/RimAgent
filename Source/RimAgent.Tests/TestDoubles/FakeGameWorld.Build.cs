using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    public CommandResult AddDesignationOnCell(int x, int z, string designationDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"AddDesignationOnCell({x},{z},{designationDefName})"));
        ThingInfo thing = Things.FirstOrDefault(t => t.X == x && t.Z == z && t.Kind == ThingKind.Mineable);
        if (thing == null) return CommandResult.Fail("nothing mineable there");
        Mark(thing, designationDefName);
        return CommandResult.Ok();
    }

    public CommandResult AddDesignationOnThing(int thingId, string designationDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"AddDesignationOnThing({thingId},{designationDefName})"));
        ThingInfo thing = Things.Concat(Selection).FirstOrDefault(t => t.Id == thingId);
        if (thing == null) return CommandResult.Fail("thing not found");
        Mark(thing, designationDefName);
        return CommandResult.Ok();
    }

    public CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"RemoveDesignationOnCell({x},{z},{designationDefName})"));
        ThingInfo thing = Things.FirstOrDefault(t => t.X == x && t.Z == z && t.Designations.Contains(designationDefName));
        if (thing == null) return CommandResult.Fail("no such designation there");
        Unmark(thing, designationDefName);
        return CommandResult.Ok();
    }

    public CommandResult RemoveDesignationOnThing(int thingId, string designationDefName)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"RemoveDesignationOnThing({thingId},{designationDefName})"));
        ThingInfo thing = Things.Concat(Selection).FirstOrDefault(t => t.Id == thingId);
        if (thing == null || !thing.Designations.Contains(designationDefName)) return CommandResult.Fail("no such designation");
        Unmark(thing, designationDefName);
        return CommandResult.Ok();
    }

    /// <summary>各建築的規格（以 defName 為鍵）；沒登記的視為不存在，GetBuildingInfo 回傳 null。</summary>
    public Dictionary<string, BuildingInfo> BuildingInfos { get; } = new Dictionary<string, BuildingInfo>(StringComparer.OrdinalIgnoreCase);

    public BuildingInfo GetBuildingInfo(string buildableDefName)
    {
        return BuildingInfos.TryGetValue(buildableDefName ?? string.Empty, out BuildingInfo info) ? info : null;
    }

    /// <summary>lookup_def 的資料（以 kind 分組）；目錄由這裡的 defName 與 Label 自動產生。</summary>
    public Dictionary<string, List<DefLookupInfo>> LookupInfos { get; } = new Dictionary<string, List<DefLookupInfo>>(StringComparer.OrdinalIgnoreCase);

    public void AddLookup(DefLookupInfo info)
    {
        if (!LookupInfos.TryGetValue(info.Kind, out List<DefLookupInfo> list)) LookupInfos[info.Kind] = list = new List<DefLookupInfo>();
        list.Add(info);
    }

    public IReadOnlyList<DefInfo> GetLookupCatalog(string kind)
    {
        return LookupInfos.TryGetValue(kind ?? string.Empty, out List<DefLookupInfo> list)
            ? [.. list.Select(i => new DefInfo(i.DefName, i.Label))]
            : new List<DefInfo>();
    }

    public DefLookupInfo LookupDef(string kind, string defName)
    {
        return LookupInfos.TryGetValue(kind ?? string.Empty, out List<DefLookupInfo> list) ? list.FirstOrDefault(i => i.DefName == defName) : null;
    }

    public CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName, bool checkOnly = false)
    {
        // 只檢查的呼叫不記進 Calls，既有測試對實際放置順序的斷言不受影響。
        if (!checkOnly) Calls.Add(string.Create(CultureInfo.InvariantCulture, $"TryPlaceBlueprint({buildableDefName},{x},{z},{rotation},{stuffDefName})"));
        if (BlockedCells.TryGetValue((x, z), out string reason)) return CommandResult.Fail(reason);
        if (BlockedStuff.TryGetValue((buildableDefName, stuffDefName ?? string.Empty), out string stuffReason)) return CommandResult.Fail(stuffReason);
        if (checkOnly) return CommandResult.Ok();
        Blueprints.Add((buildableDefName, x, z, rotation, stuffDefName));
        return CommandResult.Ok();
    }

    public List<LayoutInfo> LayoutList { get; } = new List<LayoutInfo>();

    public IReadOnlyList<LayoutInfo> Layouts => LayoutList;

    /// <summary>房型內容（以 defName 為鍵），座標相對房型左下角、朝北；GetLayoutPlan 只做平移，不模擬旋轉。</summary>
    public Dictionary<string, List<LayoutPiece>> LayoutPieces { get; } = new Dictionary<string, List<LayoutPiece>>(StringComparer.Ordinal);

    public LayoutPlan GetLayoutPlan(string layoutDefName, int x, int z, int rotation)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"GetLayoutPlan({layoutDefName},{x},{z},{rotation})"));
        LayoutInfo info = LayoutList.FirstOrDefault(l => l.DefName == layoutDefName);
        if (info == null || !LayoutPieces.TryGetValue(layoutDefName, out List<LayoutPiece> local)) return null;
        int minX = x - (info.Width - 1) / 2, minZ = z - (info.Height - 1) / 2;
        return new LayoutPlan
        {
            Rect = new CellRectSpec(minX, minZ, minX + info.Width - 1, minZ + info.Height - 1),
            Pieces = [.. local.Select(p => new LayoutPiece { DefName = p.DefName, Part = p.Part, StuffDefName = p.StuffDefName, X = minX + p.X, Z = minZ + p.Z, Rotation = p.Rotation })]
        };
    }

    public List<string> RemovableBlueprints { get; } = new List<string>();

    public CellRectSpec? LastRemovedArea { get; private set; }

    public IReadOnlyList<string> RemoveBlueprints(CellRectSpec rect)
    {
        Calls.Add($"RemoveBlueprints({rect})");
        LastRemovedArea = rect;
        return RemovableBlueprints;
    }

    public List<ShipPartInfo> ShipParts { get; } = new List<ShipPartInfo>();

    public IReadOnlyList<ShipPartInfo> GetShipParts() => ShipParts;

    public List<ShipPartRequirement> RequiredShipParts { get; } = new List<ShipPartRequirement>();

    public IReadOnlyList<ShipPartRequirement> GetRequiredShipParts() => RequiredShipParts;
}
