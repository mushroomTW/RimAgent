using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    private static PlanetTile ResolveOrigin(int originCaravanId, out Caravan? caravan, out string label)
    {
        caravan = originCaravanId >= 0 ? FindPlayerCaravan(originCaravanId) : null;
        if (caravan != null)
        {
            label = caravan.LabelCap;
            return caravan.Tile;
        }
        Map? map = Find.CurrentMap;
        if (map == null)
        {
            label = "no map";
            return PlanetTile.Invalid;
        }
        label = map.Parent?.LabelCap ?? ("Map " + map.uniqueID.ToString(CultureInfo.InvariantCulture));
        return map.Tile;
    }

    private static PlanetTile ToPlanetTile(int id, PlanetTile origin)
    {
        return origin.Valid ? new PlanetTile(id, origin.Layer) : new PlanetTile(id);
    }

    private static bool TileInWorld(PlanetTile tile)
    {
        return tile.Valid && Find.WorldGrid.InBounds(tile);
    }

    private static string DescribeWorldTile(PlanetTile tile)
    {
        if (!TileInWorld(tile)) return "no tile";
        WorldObject? obj = Find.WorldObjects.ObjectsAt(tile).FirstOrDefault(o => o is not Caravan);
        string text = "tile " + tile.tileId.ToString(CultureInfo.InvariantCulture) + " (" + tile.Tile.PrimaryBiome.LabelCap + ")";
        return obj == null ? text : obj.LabelCap + " [" + obj.ID.ToString(CultureInfo.InvariantCulture) + "], " + text;
    }

    public WorldSurveyInfo GetWorldSurvey(int originCaravanId)
    {
        PlanetTile origin = ResolveOrigin(originCaravanId, out Caravan? fromCaravan, out string label);
        Dictionary<int, List<WorldObjectQuestLink>> quests = QuestLinksByWorldObject();
        var objects = new List<WorldObjectInfo>();
        foreach (WorldObject obj in Find.WorldObjects.AllWorldObjects)
        {
            if (!obj.Spawned || !obj.Tile.Valid) continue;
            if (origin.Valid && obj.Tile.Layer != origin.Layer) continue;
            if (obj is RoutePlannerWaypoint) continue;
            if (obj is Caravan own && own.IsPlayerControlled) continue;
            objects.Add(ToWorldObjectInfo(obj, origin, quests));
        }
        return new WorldSurveyInfo
        {
            OriginTile = origin.Valid ? origin.tileId : -1,
            OriginLabel = label,
            OriginIsCaravan = fromCaravan != null,
            Objects = objects
        };
    }

    private static WorldObjectInfo ToWorldObjectInfo(WorldObject obj, PlanetTile origin, Dictionary<int, List<WorldObjectQuestLink>> quests)
    {
        Faction faction = obj.Faction;
        string kind = WorldObjectInfo.OtherKind;
        if (obj is Settlement) kind = faction == Faction.OfPlayer ? WorldObjectInfo.PlayerSettlementKind : WorldObjectInfo.FactionSettlementKind;
        else if (obj is Site) kind = WorldObjectInfo.SiteKind;
        else if (obj is Camp) kind = WorldObjectInfo.CampKind;
        else if (obj is Caravan) kind = WorldObjectInfo.CaravanKind;
        var info = new WorldObjectInfo
        {
            Id = obj.ID,
            Kind = kind,
            Label = obj.LabelCap,
            DefName = obj.def.defName,
            Tile = obj.Tile.tileId,
            TileLayer = obj.Tile.Layer == Find.WorldGrid.Surface ? string.Empty : obj.Tile.Layer.Def.defName,
            Faction = faction?.Name ?? string.Empty,
            Hostile = faction != null && faction != Faction.OfPlayer && faction.HostileTo(Faction.OfPlayer),
            HasMap = obj is MapParent mapParent && mapParent.HasMap,
            DistanceTiles = origin.Valid ? Find.WorldGrid.ApproxDistanceInTiles(origin, obj.Tile) : 0f,
            Quests = quests.TryGetValue(obj.ID, out List<WorldObjectQuestLink>? links) ? links : []
        };
        if (obj is Site site)
        {
            info.ExpiresInTicks = site.WorldObjectTimeoutTicksLeft;
            if (site.sitePartsKnown)
                info.Detail = string.Join(", ", site.parts.Select(p => p.def.label).Distinct(StringComparer.Ordinal).ToArray());
        }
        return info;
    }

    /// <summary>世界物件編號 → 把它列為目標的任務（待接受與進行中）；任務目標清單來自各任務段落的 QuestLookTargets。</summary>
    private static Dictionary<int, List<WorldObjectQuestLink>> QuestLinksByWorldObject()
    {
        var links = new Dictionary<int, List<WorldObjectQuestLink>>();
        foreach (Quest quest in Find.QuestManager.QuestsListForReading
            .Where(q => !q.hidden && !q.hiddenInUI && (q.State == QuestState.NotYetAccepted || q.State == QuestState.Ongoing)))
        {
            try
            {
                foreach (int id in quest.QuestLookTargets.Where(t => t.HasWorldObject).Select(t => t.WorldObject).Where(o => o != null).Select(obj => obj.ID).Distinct())
                {
                    if (!links.TryGetValue(id, out List<WorldObjectQuestLink>? list)) links[id] = list = [];
                    list.Add(new WorldObjectQuestLink { QuestId = quest.id, QuestName = PlainText(quest.name), Active = quest.State == QuestState.Ongoing });
                }
            }
            catch (Exception ex)
            {
                Log.Warning("RimAgent: could not read the world targets of quest " + quest.id.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
            }
        }
        return links;
    }

    public IReadOnlyList<TravelEstimateInfo> GetTravelEstimates(int originCaravanId, IReadOnlyList<int> tiles)
    {
        PlanetTile origin = ResolveOrigin(originCaravanId, out Caravan? fromCaravan, out string _);
        var result = new List<TravelEstimateInfo>();
        foreach (int id in tiles)
        {
            PlanetTile to = ToPlanetTile(id, origin);
            var estimate = new TravelEstimateInfo { Tile = id };
            if (origin.Valid && TileInWorld(to) && to.Layer == origin.Layer)
            {
                estimate.DistanceTiles = Find.WorldGrid.ApproxDistanceInTiles(origin, to);
                estimate.Reachable = fromCaravan != null ? fromCaravan.CanReach(to) : Find.WorldReachability.CanReach(origin, to);
                // 沒有商隊時傳 null：遊戲以預設速度（每格 3300 tick）估算。
                if (estimate.Reachable && origin != to)
                    estimate.Ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(origin, to, fromCaravan);
            }
            result.Add(estimate);
        }
        return result;
    }

    public WorldTileInfo GetWorldTile(int tile, int originCaravanId)
    {
        PlanetTile origin = ResolveOrigin(originCaravanId, out Caravan? _, out string _);
        PlanetTile pt = ToPlanetTile(tile, origin);
        if (!TileInWorld(pt))
            return new WorldTileInfo { Tile = tile, Error = string.Create(CultureInfo.InvariantCulture, $"tile {tile} does not exist on this layer of the world (valid ids are 0 to {pt.Layer.TilesCount - 1})") };
        RimWorld.Planet.Tile data = Find.WorldGrid[pt];
        var reason = new System.Text.StringBuilder();
        bool canSettle = TileFinder.IsValidTileForNewSettlement(pt, reason);
        if (canSettle && SettleUtility.PlayerSettlementsCountLimitReached)
        {
            canSettle = false;
            string settleFailReason;
            if (Prefs.MaxNumberOfPlayerSettlements > 1)
            {
                settleFailReason = "CommandSettleFailReachedMaximumNumberOfBases".Translate().ToString();
            }
            else
            {
                settleFailReason = "CommandSettleFailAlreadyHaveBase".Translate().ToString();
            }
            reason.Append(settleFailReason);
        }
        Dictionary<int, List<WorldObjectQuestLink>> quests = QuestLinksByWorldObject();
        Landmark? landmark = data.Landmark;
        string landmarkText;
        if (landmark == null) landmarkText = string.Empty;
        else if (landmark.name.NullOrEmpty()) landmarkText = landmark.def.label;
        else landmarkText = landmark.name;
        return new WorldTileInfo
        {
            Tile = tile,
            TileLayer = pt.Layer == Find.WorldGrid.Surface ? string.Empty : pt.Layer.Def.defName,
            Biome = data.PrimaryBiome.LabelCap,
            Hilliness = data.hilliness.GetLabelCap(),
            TemperatureC = data.temperature,
            MinTemperatureC = data.MinTemperature,
            MaxTemperatureC = data.MaxTemperature,
            RainfallMm = data.rainfall,
            ElevationM = data.elevation,
            Impassable = Find.World.Impassable(pt),
            WaterCovered = data.WaterCovered,
            Coastal = data.IsCoastal,
            Feature = data.feature?.name ?? string.Empty,
            Landmark = landmarkText,
            Mutators = [.. data.Mutators.Select(m => m.label)],
            CanSettle = canSettle,
            SettleBlockedReason = canSettle ? string.Empty : PlainText(reason.ToString()),
            Objects = [.. Find.WorldObjects.ObjectsAt(pt).Select(o => ToWorldObjectInfo(o, origin, quests))]
        };
    }
}
