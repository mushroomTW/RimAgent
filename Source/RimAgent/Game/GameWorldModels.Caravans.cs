using System;
using System.Collections.Generic;

namespace RimAgent.Game;


public sealed class TradeGoodsInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    /// <summary>單件市價（僅供參考，實際成交價依交涉者與難度浮動）。</summary>
    public float MarketValue { get; set; }
}

public sealed class TradeLine
{
    public string DefName { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class TradeRequest
{
    public string TraderId { get; set; } = string.Empty;
    public int NegotiatorPawnId { get; set; }
    public IReadOnlyList<TradeLine> Buys { get; set; } = [];
    public IReadOnlyList<TradeLine> Sells { get; set; } = [];
}

/// <summary>
/// 一位可交易的商人。Kind 區分「軌道商人」與「來訪商隊」，兩者絕不互相頂替：
/// 查軌道只看 Orbital，查來訪只看 Visitor。
/// </summary>
public sealed class TraderInfo
{
    public const string OrbitalKind = "Orbital";
    public const string VisitorKind = "Visitor";

    public string Kind { get; set; } = OrbitalKind;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Faction { get; set; } = string.Empty;
    public int Silver { get; set; }
    public IReadOnlyList<TradeGoodsInfo> Goods { get; set; } = [];
}

public sealed class CaravanRequest
{
    public IReadOnlyList<int> PawnIds { get; set; } = [];
    public IReadOnlyList<TradeLine> Cargo { get; set; } = [];
    /// <summary>目的地格（地表層的格子編號）；-1 代表沒選。原版組隊要求選好路線才送得出去，規劃可以不選。</summary>
    public int DestinationTile { get; set; } = -1;
}

/// <summary>殖民地現在能裝上商隊的一種物品（原版組隊視窗「物品」頁的內容；已撤下打包的家具算成原家具，如睡袋）。</summary>
public sealed class CaravanCargoInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public float MassEach { get; set; }
}

public sealed class CaravanPlanCargo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public float MassEach { get; set; }
}

public sealed class CaravanPlanInfo
{
    public string Error { get; set; } = string.Empty;
    public IReadOnlyList<CaravanMemberInfo> Members { get; set; } = [];
    public IReadOnlyList<CaravanPlanCargo> Cargo { get; set; } = [];
    /// <summary>貨物與成員隨身物品的總重（不含成員自己的體重，與原版視窗一致）。</summary>
    public float MassUsage { get; set; }
    public float MassCapacity { get; set; }
    public float TilesPerDay { get; set; }
    public int TicksPerMove { get; set; }
    /// <summary>存糧能吃幾天（有目的地時照路徑，含沿途覓食與夜間休息）。</summary>
    public float FoodDays { get; set; }
    public float DaysUntilRot { get; set; }
    public string ForagedFoodLabel { get; set; } = string.Empty;
    public float ForagedPerDay { get; set; }
    public float Visibility { get; set; }
    public int DestinationTile { get; set; } = -1;
    public string DestinationLabel { get; set; } = string.Empty;
    public float DistanceTiles { get; set; }
    public int EtaTicks { get; set; }
    public bool DestinationReachable { get; set; }
    public IReadOnlyList<string> Blockers { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
}

public sealed class FormingCaravanInfo
{
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<CaravanMemberInfo> Members { get; set; } = [];
    public IReadOnlyList<string> Cargo { get; set; } = [];
    public int ExitX { get; set; }
    public int ExitZ { get; set; }
}

public sealed class CaravanMemberInfo
{
    public const string ColonistKind = "colonist";
    public const string SlaveKind = "slave";
    public const string PrisonerKind = "prisoner";
    public const string AnimalKind = "animal";
    public const string MechKind = "mech";
    public const string OtherKind = "other";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = ColonistKind;
    public bool Downed { get; set; }
    public bool InMentalState { get; set; }
    public float HealthPercent { get; set; } = 1f;
}

public sealed class CaravanInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Tile { get; set; }
    public string Terrain { get; set; } = string.Empty;
    public IReadOnlyList<CaravanMemberInfo> Members { get; set; } = [];
    public bool Moving { get; set; }
    public bool Paused { get; set; }
    public bool Resting { get; set; }
    public bool Immobilized { get; set; }
    public bool AllOwnersDowned { get; set; }
    public bool AllOwnersInMentalBreak { get; set; }
    public int DestinationTile { get; set; } = -1;
    public string DestinationLabel { get; set; } = string.Empty;
    public string ArrivalAction { get; set; } = string.Empty;
    public int EtaTicks { get; set; }
    /// <summary>目前存糧撐得了幾天（遊戲的估算，600 為上限代表不需要或很充足）。</summary>
    public float FoodDays { get; set; }
    public float MassUsage { get; set; }
    public float MassCapacity { get; set; }
    public int TicksPerMove { get; set; }
    public bool CanSettle { get; set; }
    public string SettleBlockedReason { get; set; } = string.Empty;
    public bool CanCamp { get; set; }
    public string CampBlockedReason { get; set; } = string.Empty;
    public IReadOnlyList<string> ObjectsHere { get; set; } = [];
}

public sealed class WorldObjectQuestLink
{
    public int QuestId { get; set; }
    public string QuestName { get; set; } = string.Empty;
    public bool Active { get; set; }
}

public sealed class WorldObjectInfo
{
    public string TileLayer { get; set; } = string.Empty;
    public const string PlayerSettlementKind = "player settlement";
    public const string FactionSettlementKind = "faction settlement";
    public const string SiteKind = "site";
    public const string CampKind = "camp";
    public const string CaravanKind = "caravan";
    public const string OtherKind = "other";

    public int Id { get; set; }
    public string Kind { get; set; } = OtherKind;
    public string Label { get; set; } = string.Empty;
    public string DefName { get; set; } = string.Empty;
    public int Tile { get; set; }
    public string Faction { get; set; } = string.Empty;
    public bool Hostile { get; set; }
    public bool HasMap { get; set; }
    public string Detail { get; set; } = string.Empty;
    public float DistanceTiles { get; set; }
    public int ExpiresInTicks { get; set; } = -1;
    public IReadOnlyList<WorldObjectQuestLink> Quests { get; set; } = [];
}

public sealed class WorldSurveyInfo
{
    public int OriginTile { get; set; }
    public string OriginLabel { get; set; } = string.Empty;
    /// <summary>起點是商隊（行程時間用該商隊的速度）；false 則是當前地圖，時間以預設速度估算。</summary>
    public bool OriginIsCaravan { get; set; }
    public IReadOnlyList<WorldObjectInfo> Objects { get; set; } = [];
}

public sealed class TravelEstimateInfo
{
    public int Tile { get; set; }
    public bool Reachable { get; set; }
    /// <summary>大約要幾 tick；不可達或就在原地為 0。</summary>
    public int Ticks { get; set; }
    public float DistanceTiles { get; set; }
}

public sealed class WorldTileInfo
{
    public string TileLayer { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int Tile { get; set; }
    public string Biome { get; set; } = string.Empty;
    public string Hilliness { get; set; } = string.Empty;
    public float TemperatureC { get; set; }
    public float MinTemperatureC { get; set; }
    public float MaxTemperatureC { get; set; }
    public float RainfallMm { get; set; }
    public float ElevationM { get; set; }
    public bool Impassable { get; set; }
    public bool WaterCovered { get; set; }
    public bool Coastal { get; set; }
    public string Feature { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public IReadOnlyList<string> Mutators { get; set; } = [];
    public bool CanSettle { get; set; }
    public string SettleBlockedReason { get; set; } = string.Empty;
    public IReadOnlyList<WorldObjectInfo> Objects { get; set; } = [];
}

public sealed class CaravanOptionsInfo
{
    public string Error { get; set; } = string.Empty;
    public string CaravanName { get; set; } = string.Empty;
    /// <summary>1 起的序號與標籤；停用的選項原因寫在標籤裡。第 1 項固定是單純前往（沒有抵達動作）。</summary>
    public IReadOnlyList<FloatMenuOptionInfo> Options { get; set; } = [];
}

public static class CaravanMovementAction
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
}

public static class CaravanSettleAction
{
    public const string Settle = "settle";
    public const string Camp = "camp";
}
