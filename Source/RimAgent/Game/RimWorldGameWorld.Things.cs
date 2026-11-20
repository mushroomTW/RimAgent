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
    public ThingSearchResult FindThings(ThingQuery query)
    {
        Map map = Map;
        bool hasDefName = !string.IsNullOrEmpty(query.DefName);
        bool hasNeedle = !string.IsNullOrEmpty(query.LabelContains);
        // 便宜的檢查排前面：座標是整數比較，Label 要走 GenLabel 配字串，全圖數萬個 Thing 逐一算會卡一幀。
        // 不再碰到 Limit 就停：listerThings 的順序與玩家無關，先到先得會讓離殖民者很遠的地圖生成物件擠掉近處的。
        var matches = CandidateThings(map, query.Kind)
            .Where(t => MatchesQuery(t, query, hasDefName, hasNeedle))
            .ToList();

        // 排序鍵：先放行（未禁用）再禁用，同組依距離殖民者平均位置的平方由近到遠；ToThing 較貴，只對留下的幾筆算。
        List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
        float cx = map.Size.x / 2f, cz = map.Size.z / 2f;
        if (colonists.Count > 0)
        {
            cx = colonists.Average(p => (float)p.Position.x);
            cz = colonists.Average(p => (float)p.Position.z);
        }
        List<ThingInfo> shown = [.. matches
            .Select(t => new { Thing = t, Forbidden = t.IsForbidden(Faction.OfPlayer), Dist = Sq(t.Position.x - cx) + Sq(t.Position.z - cz) })
            .OrderBy(e => e.Forbidden)
            .ThenBy(e => e.Dist)
            .Take(query.Limit)
            .Select(e => ToThing(e.Thing, map))];
        return new ThingSearchResult(shown, matches.Count);
    }

    private static float Sq(float v) => v * v;

    private static bool MatchesQuery(Thing t, ThingQuery query, bool hasDefName, bool hasNeedle)
    {
        if (!t.Spawned || (!query.IncludeFogged && t.Fogged())) return false;
        if (query.Area.HasValue && !query.Area.Value.Contains(t.Position.x, t.Position.z)) return false;
        ThingKind? kind = KindOf(t);
        if (kind == null || (query.Kind != ThingKind.Any && kind != query.Kind)) return false;
        if (hasDefName && !string.Equals(t.def.defName, query.DefName, StringComparison.OrdinalIgnoreCase)) return false;
        if (hasNeedle && !MatchesLabel(t, query.LabelContains)) return false;
        return true;
    }

    /// <summary>
    /// 依 kind 縮小掃描範圍：pawn 與植物各有現成清單（Plant 群組就是 category == Plant），
    /// 其餘才掃全圖（物品沒有恰好對應 category == Item 的群組，HaulableEver 會漏掉不可搬運的物品）。
    /// </summary>
    private static IEnumerable<Thing> CandidateThings(Map map, ThingKind kind)
    {
        return kind switch
        {
            ThingKind.Pawn => map.mapPawns.AllPawnsSpawned,
            ThingKind.Plant => map.listerThings.ThingsInGroup(ThingRequestGroup.Plant),
            _ => map.listerThings.AllThings,
        };
    }

    private static bool MatchesLabel(Thing t, string needle)
    {
        return t.def.defName.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || (t.def.label ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase)
               || t.Label.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ThingInfo> GetSelection()
    {
        Map map = Map;
        return [.. Find.Selector.SelectedObjects.OfType<Thing>()
            .Where(t => t.Spawned && t.Map == map && KindOf(t) != null)
            .Select(t => ToThing(t, map))];
    }

    public MapBounds GetMapBounds()
    {
        IntVec3 size = Map.Size;
        return new MapBounds { Width = size.x, Height = size.z };
    }

    public CellInfo GetCell(int x, int z)
    {
        return ToCell(new IntVec3(x, 0, z), Map, []);
    }

    public IReadOnlyList<CellInfo> GetCells(CellRectSpec rect)
    {
        Map map = Map;
        var result = new List<CellInfo>(rect.CellCount);
        // 同一個房間在矩形裡會出現很多次，RoomInfo 只建一份（外框要走遍房間所有格子）。
        var rooms = new Dictionary<int, RoomInfo>();
        for (int z = rect.MinZ; z <= rect.MaxZ; z++)
        {
            for (int x = rect.MinX; x <= rect.MaxX; x++)
            {
                result.Add(ToCell(new IntVec3(x, 0, z), map, rooms));
            }
        }
        return result;
    }

    private static CellInfo ToCell(IntVec3 c, Map map, Dictionary<int, RoomInfo> rooms)
    {
        TerrainDef terrain = c.GetTerrain(map);
        bool fogged = c.Fogged(map);
        Zone zone = c.GetZone(map);
        return new CellInfo
        {
            X = c.x,
            Z = c.z,
            TerrainDefName = terrain.defName,
            Terrain = terrain.label ?? terrain.defName,
            Fertility = map.fertilityGrid.FertilityAt(c),
            Roof = RoofLabel(c.GetRoof(map)),
            Walkable = c.Walkable(map),
            Support = fogged ? GroundSupport.None : SupportAt(c, map),
            Fogged = fogged,
            Zone = zone != null ? ToZone(zone) : null,
            Room = ToRoom(c.GetRoom(map), rooms),
            // 迷霧裡的東西玩家看不到，工具也不透露。藍圖不是 Building（KindOf 會濾掉），
            // 但它占著格子，規劃建造時得看得到，這裡當建築列出。
            Things = fogged
                ? (IReadOnlyList<ThingInfo>)[]
                : [.. c.GetThingList(map).Where(t => KindOf(t) != null || t is Blueprint).Select(t => ToThing(t, map))]
        };
    }

    /// <summary>與 GenConstruct.CanBuildOnTerrain 同一來源（IntVec3.GetAffordances：地基優先，否則地形）取最強的重量等級。</summary>
    private static GroundSupport SupportAt(IntVec3 c, Map map)
    {
        List<TerrainAffordanceDef> affordances = c.GetAffordances(map);
        if (affordances.Contains(TerrainAffordanceDefOf.Heavy)) return GroundSupport.Heavy;
        if (affordances.Contains(TerrainAffordanceDefOf.Medium)) return GroundSupport.Medium;
        if (affordances.Contains(TerrainAffordanceDefOf.Light)) return GroundSupport.Light;
        return GroundSupport.None;
    }

    private static string RoofLabel(RoofDef roof)
    {
        if (roof == null) return string.Empty;
        if (!roof.isNatural) return "constructed";
        return roof.isThickRoof ? "overhead mountain" : "natural";
    }

    private static RoomInfo ToRoom(Room room, Dictionary<int, RoomInfo> cache)
    {
        if (room == null) return null;
        if (cache.TryGetValue(room.ID, out RoomInfo cached)) return cached;
        CellRect extents = room.ExtentsClose;
        var info = new RoomInfo
        {
            Id = room.ID,
            Role = room.Role?.label ?? string.Empty,
            CellCount = room.CellCount,
            Outdoors = room.PsychologicallyOutdoors,
            TemperatureC = room.Temperature,
            Extents = new CellRectSpec(extents.minX, extents.minZ, extents.maxX, extents.maxZ)
        };
        cache[room.ID] = info;
        return info;
    }
}
