using System;
using System.Collections.Generic;
using System.Linq;
using DevTools.Testing;
using RimAgent.Game;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

internal sealed partial class MapWorldTests
{
    /// <summary>
    /// 世界物件清單對照原版：同一批物件（不含玩家商隊與路線規劃點）、種類與敵對判定、到起點的距離、
    /// 定居點的地圖狀態；起點格的地形資料對照原版 Tile，被自己的殖民地占著所以不能建立殖民地。
    /// </summary>
    [Test]
    public void WorldSurveyAndTileMatchVanilla()
    {
        if (TestHelpers.SkipWithoutMap(nameof(WorldSurveyAndTileMatchVanilla))) return;
        var failures = new List<string>();
        _world.TakeNewGameErrors();
        Map map = Find.CurrentMap;
        PlanetTile origin = map.Tile;

        WorldSurveyInfo survey = _world.GetWorldSurvey(-1);
        if (survey.OriginTile != origin.tileId) failures.Add($"origin tile {survey.OriginTile}, vanilla {origin.tileId}");
        if (survey.OriginIsCaravan) failures.Add("a survey without a caravan claims its origin is a caravan");
        var expected = Find.WorldObjects.AllWorldObjects
            .Where(o => o.Spawned && o.Tile.Valid && o.Tile.Layer == origin.Layer && o is not RoutePlannerWaypoint && !(o is Caravan { IsPlayerControlled: true }))
            .ToDictionary(o => o.ID);
        CheckSurveyObjects(survey, expected, origin, failures);
        CheckMissingObjects(expected, survey, failures);
        CheckHomeSettlementInSurvey(survey, failures);

        IReadOnlyList<TravelEstimateInfo> estimates = _world.GetTravelEstimates(-1, survey.Objects.Take(5).Select(o => o.Tile).ToList());
        CheckTravelEstimates(estimates, origin, failures);

        WorldTileInfo tile = _world.GetWorldTile(origin.tileId, -1);
        RimWorld.Planet.Tile vanillaTile = Find.WorldGrid[origin];
        CheckWorldTile(tile, vanillaTile, failures);

        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "world survey and tile");
    }

    /// <summary>把調查回傳的每個世界物件與原版定義逐一比對。</summary>
    private static void CheckSurveyObjects(WorldSurveyInfo survey, Dictionary<int, WorldObject> expected, PlanetTile origin, List<string> failures)
    {
        foreach (WorldObjectInfo info in survey.Objects)
            CheckOneSurveyObject(expected, origin, info, failures);
    }

    /// <summary>比對單一調查物件：是否存在、佔格格、距離、敵對、定居點與地點旗標。</summary>
    private static void CheckOneSurveyObject(Dictionary<int, WorldObject> expected, PlanetTile origin, WorldObjectInfo info, List<string> failures)
    {
        if (!expected.TryGetValue(info.Id, out WorldObject obj))
        {
            failures.Add($"[{info.Id}] {info.Label} is not a vanilla world object of this layer");
            return;
        }
        if (info.Tile != obj.Tile.tileId) failures.Add($"{obj.LabelCap}: tile {info.Tile}, vanilla {obj.Tile.tileId}");
        float distance = Find.WorldGrid.ApproxDistanceInTiles(origin, obj.Tile);
        if (Math.Abs(info.DistanceTiles - distance) > 0.01f) failures.Add($"{obj.LabelCap}: distance {info.DistanceTiles}, vanilla {distance}");
        bool hostile = obj.Faction != null && obj.Faction != Faction.OfPlayer && obj.Faction.HostileTo(Faction.OfPlayer);
        if (info.Hostile != hostile) failures.Add($"{obj.LabelCap}: hostile {info.Hostile}, vanilla {hostile}");
        CheckSettlement(obj, info, failures);
        if (obj is Site && info.Kind != WorldObjectInfo.SiteKind) failures.Add($"{obj.LabelCap}: a site listed as {info.Kind}");
    }

    /// <summary>定居點的種類與地圖旗標是否與原版一致。</summary>
    private static void CheckSettlement(WorldObject obj, WorldObjectInfo info, List<string> failures)
    {
        if (obj is Settlement settlement)
        {
            string kind = settlement.Faction == Faction.OfPlayer ? WorldObjectInfo.PlayerSettlementKind : WorldObjectInfo.FactionSettlementKind;
            if (info.Kind != kind) failures.Add($"{obj.LabelCap}: kind {info.Kind}, expected {kind}");
            if (info.HasMap != settlement.HasMap) failures.Add($"{obj.LabelCap}: HasMap {info.HasMap}, vanilla {settlement.HasMap}");
        }
    }

    /// <summary>原版有但調查清單缺失的世界物件。</summary>
    private static void CheckMissingObjects(Dictionary<int, WorldObject> expected, WorldSurveyInfo survey, List<string> failures)
    {
        foreach (WorldObject obj in expected.Values.Where(o => survey.Objects.All(i => i.Id != o.ID)))
            failures.Add($"{obj.LabelCap} [{obj.ID}] is missing from the survey");
    }

    /// <summary>自家殖民地是否出現在調查清單中。</summary>
    private static void CheckHomeSettlementInSurvey(WorldSurveyInfo survey, List<string> failures)
    {
        if (!survey.Objects.Any(o => o.Kind == WorldObjectInfo.PlayerSettlementKind && o.HasMap))
            failures.Add("the home settlement with a map is not in the survey");
    }

    /// <summary>旅行時間估算是否與原版可達性一致。</summary>
    private static void CheckTravelEstimates(IReadOnlyList<TravelEstimateInfo> estimates, PlanetTile origin, List<string> failures)
    {
        foreach (TravelEstimateInfo e in estimates)
        {
            PlanetTile to = new PlanetTile(e.Tile, origin.Layer);
            bool reachable = Find.WorldReachability.CanReach(origin, to);
            if (e.Reachable != reachable) failures.Add($"tile {e.Tile}: reachable {e.Reachable}, vanilla {reachable}");
            if (e.Reachable && to != origin && e.Ticks <= 0) failures.Add($"tile {e.Tile}: reachable but no travel time");
        }
    }

    /// <summary>起點磚塊資訊與原版 Tile 的比對，以及超出世界範圍的磚塊應被拒絕。</summary>
    private void CheckWorldTile(WorldTileInfo tile, RimWorld.Planet.Tile vanillaTile, List<string> failures)
    {
        if (!string.IsNullOrEmpty(tile.Error)) failures.Add("home tile: " + tile.Error);
        if (tile.Biome != vanillaTile.PrimaryBiome.LabelCap) failures.Add($"biome {tile.Biome}, vanilla {vanillaTile.PrimaryBiome.LabelCap}");
        if (tile.Hilliness != vanillaTile.hilliness.GetLabelCap()) failures.Add($"hilliness {tile.Hilliness}, vanilla {vanillaTile.hilliness.GetLabelCap()}");
        if (Math.Abs(tile.TemperatureC - vanillaTile.temperature) > 0.01f) failures.Add($"temperature {tile.TemperatureC}, vanilla {vanillaTile.temperature}");
        if (tile.CanSettle || string.IsNullOrEmpty(tile.SettleBlockedReason)) failures.Add("the home tile claims a colony could be founded there, or gives no reason");
        if (!tile.Objects.Any(o => o.Kind == WorldObjectInfo.PlayerSettlementKind)) failures.Add("the home settlement is not on its own tile");
        if (string.IsNullOrEmpty(_world.GetWorldTile(int.MaxValue, -1).Error)) failures.Add("a tile id outside the world was accepted");
    }
}
