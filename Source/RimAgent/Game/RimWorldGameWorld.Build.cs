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
    public CommandResult AddDesignationOnCell(int x, int z, string designationDefName)
    {
        Map map = Map;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
        DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
        if (def == null) return CommandResult.Fail($"unknown designation {designationDefName}");
        if (def.targetType != Verse.TargetType.Cell) return CommandResult.Fail($"designation {designationDefName} applies to objects, not cells. Use the object id instead");
        if (map.designationManager.DesignationAt(cell, def) != null) return CommandResult.Ok();
        // 與 Designator_Mine.CanDesignateCell 一致：迷霧格直接接受（挖進未探索的山體是正常玩法，也不透露裡面有什麼）。
        if (def.defName == MineDefName && !cell.Fogged(map) && cell.GetFirstMineable(map) == null)
            return CommandResult.Fail("nothing to mine there");

        map.designationManager.AddDesignation(new Designation(cell, def));
        return CommandResult.Ok();
    }

    public CommandResult AddDesignationOnThing(int thingId, string designationDefName)
    {
        Map map = Map;
        Thing thing = FindThing(thingId);
        if (thing == null) return CommandResult.Fail("object not found on the map");
        DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
        if (def == null) return CommandResult.Fail($"unknown designation {designationDefName}");
        if (def.targetType != Verse.TargetType.Thing) return CommandResult.Fail($"designation {designationDefName} applies to cells, not objects. Use the cell coordinates instead");
        if (map.designationManager.DesignationOn(thing, def) != null) return CommandResult.Ok();

        string blocked = WhyNotDesignatable(thing, def);
        if (blocked != null) return CommandResult.Fail(blocked);

        map.designationManager.AddDesignation(new Designation(thing, def));
        return CommandResult.Ok();
    }

    public CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName)
    {
        Map map = Map;
        var cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
        DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
        Designation existing = def == null ? null : map.designationManager.DesignationAt(cell, def);
        if (existing == null) return CommandResult.Fail("no such designation there");
        map.designationManager.RemoveDesignation(existing);
        return CommandResult.Ok();
    }

    public CommandResult RemoveDesignationOnThing(int thingId, string designationDefName)
    {
        Map map = Map;
        Thing thing = FindThing(thingId);
        if (thing == null) return CommandResult.Fail("object not found on the map");
        DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
        Designation existing = null;
        if (def != null) existing = map.designationManager.DesignationOn(thing, def);
        if (existing == null) return CommandResult.Fail("no such designation on it");
        map.designationManager.RemoveDesignation(existing);
        return CommandResult.Ok();
    }

    public CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName, bool checkOnly = false)
    {
        Map map = Map;
        BuildableDef def = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(buildableDefName)
                           ?? DefDatabase<TerrainDef>.GetNamedSilentFail(buildableDefName);
        if (def == null) return CommandResult.Fail($"unknown buildable {buildableDefName}");
        if (!def.BuildableByPlayer) return CommandResult.Fail($"{def.label} cannot be built by the player");
        if (!def.IsResearchFinished) return CommandResult.Fail($"{def.label} requires research that is not finished");

        ThingDef stuff = null;
        if (def.MadeFromStuff)
        {
            stuff = string.IsNullOrEmpty(stuffDefName)
                ? GenStuff.DefaultStuffFor(def)
                : DefDatabase<ThingDef>.GetNamedSilentFail(stuffDefName);
            if (stuff == null) return CommandResult.Fail($"unknown material {stuffDefName}");
            if (!GenStuff.AllowedStuffsFor(def).Contains(stuff)) return CommandResult.Fail($"{def.label} cannot be made of {stuff.label}");
        }

        var cell = new IntVec3(x, 0, z);
        var rot = new Rot4(rotation);
        AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, godMode: false, thingToIgnore: null, thing: null, stuff);
        CommandResult reportFail = HandleBlueprintReport(def, report, cell, rot, map, stuff);
        if (reportFail != null) return reportFail;

        if (checkOnly) return CommandResult.Ok();
        GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
        return CommandResult.Ok();
    }

    private static CommandResult HandleBlueprintReport(BuildableDef def, AcceptanceReport report, IntVec3 cell, Rot4 rot, Map map, ThingDef stuff)
    {
        if (!report.Accepted)
        {
            string reason = string.IsNullOrEmpty(report.Reason) ? "cannot place there" : report.Reason;
            if (reason == "SpaceAlreadyOccupied".Translate().ToString()) reason += DescribeBlockers(def, cell, rot, map, stuff);
            return CommandResult.Fail(reason);
        }
        return null;
    }

    private const int MaxBlockersListed = 4;

    /// <summary>
    /// 「空間已被占用」時列出擋住的東西（名稱、id、佔用範圍）。判準與 CanPlaceBlueprintAt 的
    /// SpaceAlreadyOccupied 迴圈相同（CanPlaceBlueprintOver）；多格物件在每格都有登記，以 id 去重。
    /// 地板（TerrainDef）另有地基例外，這裡不處理，直接不列。
    /// </summary>
    private static string DescribeBlockers(BuildableDef def, IntVec3 center, Rot4 rot, Map map, ThingDef stuff)
    {
        if (def is not ThingDef) return string.Empty;
        var seen = new HashSet<int>();
        var parts = new List<string>();
        int extra = 0;
        foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, def.Size))
        {
            if (!c.InBounds(map)) continue;
            ProcessBlockersAtCell(c, map, def, stuff, seen, parts, ref extra);
        }
        if (parts.Count == 0) return string.Empty;
        return " (blocked by: " + string.Join("; ", parts) + (extra > 0 ? string.Create(CultureInfo.InvariantCulture, $"; +{extra} more") : string.Empty) + ")";
    }

    private static void ProcessBlockersAtCell(IntVec3 c, Map map, BuildableDef def, ThingDef stuff, HashSet<int> seen, List<string> parts, ref int extra)
    {
        List<Thing> things = c.GetThingList(map);
        for (int i = 0; i < things.Count; i++)
        {
            Thing t = things[i];
            if (GenConstruct.CanPlaceBlueprintOver(def, t.def, stuff, t.Stuff) || !seen.Add(t.thingIDNumber)) continue;
            if (parts.Count >= MaxBlockersListed)
            {
                extra++;
                continue;
            }
            CellRect r = t.OccupiedRect();
            string where = r.Width == 1 && r.Height == 1
                ? string.Create(CultureInfo.InvariantCulture, $"({r.minX},{r.minZ})")
                : string.Create(CultureInfo.InvariantCulture, $"({r.minX},{r.minZ})-({r.maxX},{r.maxZ})");
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{t.LabelNoCount} #{t.thingIDNumber} {where}"));
        }
    }

    public IReadOnlyList<string> RemoveBlueprints(CellRectSpec rect)
    {
        Map map = Map;
        var removed = new List<string>();
        CellRect area = CellRect.FromLimits(rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ).ClipInsideMap(map);
        List<Thing> targets = [.. area.Cells
            .SelectMany(c => c.GetThingList(map))
            .Where(t => (t is Blueprint || t is Frame) && t.Faction == Faction.OfPlayer)
            .Distinct()];
        foreach (Thing t in targets)
        {
            CellRect r = t.OccupiedRect();
            removed.Add(string.Create(CultureInfo.InvariantCulture, $"{t.LabelNoCount} ({r.minX},{r.minZ})-({r.maxX},{r.maxZ})"));
            t.Destroy(DestroyMode.Cancel);
        }
        return removed;
    }

    public IReadOnlyList<ShipPartInfo> GetShipParts()
    {
        Map map = Map;
        var parts = new List<ShipPartInfo>();
        foreach (Thing t in map.listerThings.AllThings)
        {
            // 藍圖與框架的 def 是自己的，真正要蓋的東西在 entityDefToBuild。
            ThingDef def = t is Blueprint || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def;
            if (def?.building == null || !def.building.shipPart) continue;
            CellRect r = t.OccupiedRect();
                ShipPartState state;
                if (t is Blueprint) state = ShipPartState.Blueprint;
                else if (t is Frame) state = ShipPartState.Frame;
                else state = ShipPartState.Built;
            var info = new ShipPartInfo
            {
                Id = t.thingIDNumber,
                DefName = def.defName,
                Label = def.label ?? def.defName,
                State = state,
                Rect = new CellRectSpec(r.minX, r.minZ, r.maxX, r.maxZ)
            };
            if (def.hasInteractionCell)
            {
                IntVec3 cell = ThingUtility.InteractionCellWhenAt(def, t.Position, t.Rotation, map);
                info.InteractionCell = (cell.x, cell.z);
                info.InteractionBlockedBy = InteractionBlocker(cell, map);
            }
            parts.Add(info);
        }
        return parts;
    }

    private static string InteractionBlocker(IntVec3 cell, Map map)
    {
        if (!cell.InBounds(map)) return "the map edge";
        if (!cell.Impassable(map)) return null;
        Thing blocker = cell.GetThingList(map).FirstOrDefault(t => t.def.passability == Traversability.Impassable);
        return blocker?.LabelNoCount ?? cell.GetTerrain(map).label;
    }

    public IReadOnlyList<ShipPartRequirement> GetRequiredShipParts()
    {
        return [.. ShipUtility.RequiredParts().Select(p => new ShipPartRequirement { DefName = p.Key.defName, Label = p.Key.label ?? p.Key.defName, Count = p.Value })];
    }

    public BuildingInfo GetBuildingInfo(string buildableDefName)
    {
        BuildableDef def = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(buildableDefName)
                           ?? DefDatabase<TerrainDef>.GetNamedSilentFail(buildableDefName);
        if (def == null) return null;
        var thing = def as ThingDef;
        IntVec2 size = def.Size;
        CompProperties_Power power = thing?.GetCompProperties<CompProperties_Power>();
        var interaction = new List<(int X, int Z)>();
        if (thing != null && thing.hasInteractionCell) interaction.Add((thing.interactionCellOffset.x, thing.interactionCellOffset.z));
        if (thing?.multipleInteractionCellOffsets != null) interaction.AddRange(thing.multipleInteractionCellOffsets.Select(o => (o.x, o.z)));

        return new BuildingInfo
        {
            DefName = def.defName,
            Label = def.label ?? def.defName,
            Width = size.x,
            Height = size.z,
            IsTerrain = thing == null,
            Costs = [.. (def.CostList ?? []).Select(c => new BuildingCost { Label = c.thingDef.label ?? c.thingDef.defName, Count = c.count })],
            StuffCount = def.MadeFromStuff ? def.CostStuffCount : 0,
            StuffCategories = def.MadeFromStuff ? def.stuffCategories.Select(s => s.label ?? s.defName).ToList() : [],
            TerrainAffordance = def.terrainAffordanceNeeded?.defName ?? string.Empty,
            AffordanceFromStuff = def.useStuffTerrainAffordance,
            Research = [.. (def.researchPrerequisites ?? []).Select(r => new BuildingResearch { Label = r.label ?? r.defName, Done = Current.ProgramState == ProgramState.Playing && r.IsFinished })],
            ConstructionSkill = def.constructionSkillPrerequisite,
            ArtisticSkill = def.artisticSkillPrerequisite,
            PlaceWorkers = [.. (def.placeWorkers ?? []).Select(t => t.Name)],
            ShipPart = thing?.building != null && thing.building.shipPart,
            PowerWatts = power != null ? power.PowerConsumption : 0f,
            InteractionOffsets = interaction
        };
    }

    private static string WhyNotDesignatable(Thing thing, DesignationDef def)
    {
        switch (def.defName)
        {
            case "CutPlant":
            case "HarvestPlant":
                return thing is Plant ? null : $"{thing.LabelCap} is not a plant";
            case "Deconstruct":
                if (thing is not Building) return $"{thing.LabelCap} is not a building";
                if (thing.Faction != Faction.OfPlayer) return $"{thing.LabelCap} does not belong to the colony";
                if (thing.def.building != null && !thing.def.building.IsDeconstructible) return $"{thing.LabelCap} cannot be deconstructed";
                return null;
            case "Haul":
                return thing.def.EverHaulable ? null : $"{thing.LabelCap} cannot be hauled";
            case "Hunt":
                return thing is Pawn pawn && IsWildAnimal(pawn)
                    ? null : $"{thing.LabelCap} is not a huntable wild animal";
            default:
                return null;
        }
    }
}
