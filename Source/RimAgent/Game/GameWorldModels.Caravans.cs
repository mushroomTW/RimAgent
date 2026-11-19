using System;
using System.Collections.Generic;

namespace RimAgent.Game;

// IGameWorld 共用資料型別：交易、商隊與世界地圖。
// 與 GameWorldModels.cs 相同：刻意不含任何 Verse / RimWorld 型別。

/// <summary>商人庫存裡的一項貨物（不含貨幣本身，貨幣另見 <see cref="TraderInfo.Silver"/>）。</summary>
public sealed class TradeGoodsInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
    /// <summary>單件市價（僅供參考，實際成交價依交涉者與難度浮動）。</summary>
    public float MarketValue { get; set; }
}

/// <summary>買入賣出各一行：已解析到 defName 的品項與數量。</summary>
public sealed class TradeLine
{
    public string DefName { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>一次結清的交易請求：買入與賣出明細一次交割，驗證不過則整筆不做。</summary>
public sealed class TradeRequest
{
    /// <summary>見 <see cref="TraderInfo.Id"/>。</summary>
    public string TraderId { get; set; } = string.Empty;
    /// <summary>交涉者 pawnId（工具層已解析；未指名時填社交技能最高者）。</summary>
    public int NegotiatorPawnId { get; set; }
    /// <summary>向商人買入的品項。</summary>
    public IReadOnlyList<TradeLine> Buys { get; set; } = Array.Empty<TradeLine>();
    /// <summary>賣給商人的品項。</summary>
    public IReadOnlyList<TradeLine> Sells { get; set; } = Array.Empty<TradeLine>();
}

/// <summary>
/// 一位可交易的商人。Kind 區分「軌道商人」與「來訪商隊」，兩者絕不互相頂替：
/// 查軌道只看 Orbital，查來訪只看 Visitor。
/// </summary>
public sealed class TraderInfo
{
    /// <summary>軌道商人（在軌道上的貿易船）。</summary>
    public const string OrbitalKind = "Orbital";
    /// <summary>來訪商隊（親自到訪殖民地的商隊）。</summary>
    public const string VisitorKind = "Visitor";

    /// <summary>見 <see cref="OrbitalKind"/>／<see cref="VisitorKind"/>。</summary>
    public string Kind { get; set; } = OrbitalKind;
    /// <summary>查詢工具回傳的不透明編號，交易指令以此指名商人（存檔間穩定）。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>顯示用全名，僅供閱讀，指名商人請用 <see cref="Id"/>。</summary>
    public string Name { get; set; } = string.Empty;
    public string Faction { get; set; } = string.Empty;
    /// <summary>商人持有的銀兩（買入時殖民地能拿到的上限參考）。</summary>
    public int Silver { get; set; }
    public IReadOnlyList<TradeGoodsInfo> Goods { get; set; } = Array.Empty<TradeGoodsInfo>();
}

/// <summary>
/// 規劃或組織一支商隊：照原版組隊視窗（Dialog_FormCaravan）的選擇——成員、貨物、目的地格。
/// 成員一律用世界內的 pawn 編號（殖民者、殖民地動物、囚犯混在一起），貨物是物品 defName 與數量。
/// </summary>
public sealed class CaravanRequest
{
    public IReadOnlyList<int> PawnIds { get; set; } = Array.Empty<int>();
    /// <summary>從殖民地倉儲與家園區帶走的物品。</summary>
    public IReadOnlyList<TradeLine> Cargo { get; set; } = Array.Empty<TradeLine>();
    /// <summary>目的地格（地表層的格子編號）；-1 代表沒選。原版組隊要求選好路線才送得出去，規劃可以不選。</summary>
    public int DestinationTile { get; set; } = -1;
}

/// <summary>殖民地現在能裝上商隊的一種物品（原版組隊視窗「物品」頁的內容；已撤下打包的家具算成原家具，如睡袋）。</summary>
public sealed class CaravanCargoInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>倉儲與家園區裡可帶走的總數（不含被預約的）。</summary>
    public int Count { get; set; }
    public float MassEach { get; set; }
}

/// <summary>規劃中的一行貨物。</summary>
public sealed class CaravanPlanCargo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public float MassEach { get; set; }
}

/// <summary>
/// 原版組隊視窗在這組選擇下會顯示的數字與警告（負重、速度、存糧、覓食、能見度、到目的地的天數），
/// 與按下「送出」時會被擋下的原因。
/// </summary>
public sealed class CaravanPlanInfo
{
    /// <summary>請求本身有問題（成員或貨物對不到）；有值時其他欄位無意義。</summary>
    public string Error { get; set; } = string.Empty;
    public IReadOnlyList<CaravanMemberInfo> Members { get; set; } = Array.Empty<CaravanMemberInfo>();
    public IReadOnlyList<CaravanPlanCargo> Cargo { get; set; } = Array.Empty<CaravanPlanCargo>();
    /// <summary>貨物與成員隨身物品的總重（不含成員自己的體重，與原版視窗一致）。</summary>
    public float MassUsage { get; set; }
    public float MassCapacity { get; set; }
    public float TilesPerDay { get; set; }
    public int TicksPerMove { get; set; }
    /// <summary>存糧能吃幾天（有目的地時照路徑，含沿途覓食與夜間休息）。</summary>
    public float FoodDays { get; set; }
    /// <summary>食物多久內會腐壞（遊戲的 ApproxDaysUntilRot）。</summary>
    public float DaysUntilRot { get; set; }
    /// <summary>每天可覓得的食物種類與數量；沒有覓食能力時數量為 0。</summary>
    public string ForagedFoodLabel { get; set; } = string.Empty;
    public float ForagedPerDay { get; set; }
    /// <summary>能見度 0 到 1（越高越容易被敵對勢力發現）。</summary>
    public float Visibility { get; set; }
    /// <summary>目的地格；-1 代表沒選。</summary>
    public int DestinationTile { get; set; } = -1;
    public string DestinationLabel { get; set; } = string.Empty;
    public float DistanceTiles { get; set; }
    /// <summary>到目的地的預估 tick（遊戲的 CaravanArrivalTimeEstimator，含夜間休息）；0 代表未知。</summary>
    public int EtaTicks { get; set; }
    public bool DestinationReachable { get; set; }
    /// <summary>組隊視窗按「送出」會拒絕的原因（遊戲自己的訊息）；空表示送得出去。</summary>
    public IReadOnlyList<string> Blockers { get; set; } = Array.Empty<string>();
    /// <summary>組隊視窗會彈出「確定嗎」確認框的警告；不擋，但值得看。</summary>
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>一支正在集結、還沒離開地圖的商隊（LordJob_FormAndSendCaravan）。</summary>
public sealed class FormingCaravanInfo
{
    /// <summary>集結用的 Lord 編號，取消時用。</summary>
    public int Id { get; set; }
    /// <summary>遊戲顯示的階段（集結動物、搬運物品、等待、離開…）。</summary>
    public string Status { get; set; } = string.Empty;
    public IReadOnlyList<CaravanMemberInfo> Members { get; set; } = Array.Empty<CaravanMemberInfo>();
    /// <summary>要帶走的貨物，每項「名稱 x數量」。</summary>
    public IReadOnlyList<string> Cargo { get; set; } = Array.Empty<string>();
    public int ExitX { get; set; }
    public int ExitZ { get; set; }
}

/// <summary>玩家商隊的一名成員。</summary>
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
    /// <summary>colonist、slave、prisoner、animal、mech 或 other。</summary>
    public string Kind { get; set; } = ColonistKind;
    public bool Downed { get; set; }
    public bool InMentalState { get; set; }
    /// <summary>整體健康度 0 到 1（遊戲的 SummaryHealthPercent）。</summary>
    public float HealthPercent { get; set; } = 1f;
}

/// <summary>世界地圖上的一支玩家商隊。tile 一律是地表層的格子編號。</summary>
public sealed class CaravanInfo
{
    /// <summary>世界物件編號（WorldObject.ID）。</summary>
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Tile { get; set; }
    /// <summary>所在格的生態與地形白話描述。</summary>
    public string Terrain { get; set; } = string.Empty;
    public IReadOnlyList<CaravanMemberInfo> Members { get; set; } = Array.Empty<CaravanMemberInfo>();
    /// <summary>有目的地在走（含暫停中）。</summary>
    public bool Moving { get; set; }
    public bool Paused { get; set; }
    /// <summary>夜間休息中（原版 NightResting）。</summary>
    public bool Resting { get; set; }
    /// <summary>超載而動彈不得（負重大於容量）。</summary>
    public bool Immobilized { get; set; }
    public bool AllOwnersDowned { get; set; }
    public bool AllOwnersInMentalBreak { get; set; }
    /// <summary>目的地格；沒有目的地為 -1。</summary>
    public int DestinationTile { get; set; } = -1;
    public string DestinationLabel { get; set; } = string.Empty;
    /// <summary>抵達後要做的事（進入、交易、攻擊…），遊戲給的原文；單純移動為空。</summary>
    public string ArrivalAction { get; set; } = string.Empty;
    /// <summary>預計還要幾 tick 抵達（含夜間休息）；沒有目的地或路徑還沒算出來為 0。</summary>
    public int EtaTicks { get; set; }
    /// <summary>目前存糧撐得了幾天（遊戲的估算，600 為上限代表不需要或很充足）。</summary>
    public float FoodDays { get; set; }
    public float MassUsage { get; set; }
    public float MassCapacity { get; set; }
    /// <summary>走一格要幾 tick（越小越快）。</summary>
    public int TicksPerMove { get; set; }
    public bool CanSettle { get; set; }
    /// <summary>不能就地建立殖民地的原因，遊戲給的原文；CanSettle 為 true 時為空。</summary>
    public string SettleBlockedReason { get; set; } = string.Empty;
    public bool CanCamp { get; set; }
    public string CampBlockedReason { get; set; } = string.Empty;
    /// <summary>同一格上的其他世界物件，格式「標籤 [id]」。</summary>
    public IReadOnlyList<string> ObjectsHere { get; set; } = Array.Empty<string>();
}

/// <summary>任務與世界物件的關聯（任務的目標清單裡有該物件）。</summary>
public sealed class WorldObjectQuestLink
{
    public int QuestId { get; set; }
    public string QuestName { get; set; } = string.Empty;
    /// <summary>任務已接受、進行中；false 代表還在等玩家接受。</summary>
    public bool Active { get; set; }
}

/// <summary>世界地圖上的一個物件（定居點、據點、營地…）。</summary>
public sealed class WorldObjectInfo
{
    /// <summary>不在地表時的星球圖層（例如 Orbit）；在地表是空字串。</summary>
    public string TileLayer { get; set; } = string.Empty;
    public const string PlayerSettlementKind = "player settlement";
    public const string FactionSettlementKind = "faction settlement";
    public const string SiteKind = "site";
    public const string CampKind = "camp";
    public const string CaravanKind = "caravan";
    public const string OtherKind = "other";

    /// <summary>世界物件編號（WorldObject.ID）。</summary>
    public int Id { get; set; }
    /// <summary>player settlement、faction settlement、site、camp、caravan（其他派系的）或 other。</summary>
    public string Kind { get; set; } = OtherKind;
    public string Label { get; set; } = string.Empty;
    public string DefName { get; set; } = string.Empty;
    public int Tile { get; set; }
    public string Faction { get; set; } = string.Empty;
    public bool Hostile { get; set; }
    /// <summary>已經有生成好的地圖（可直接進入）。</summary>
    public bool HasMap { get; set; }
    /// <summary>據點或任務地點的補充說明（據點的組成、可否攻擊…）。</summary>
    public string Detail { get; set; } = string.Empty;
    /// <summary>從查詢起點到這裡的大約格數。</summary>
    public float DistanceTiles { get; set; }
    /// <summary>還剩幾 tick 消失（任務地點的期限）；沒有期限為 -1。</summary>
    public int ExpiresInTicks { get; set; } = -1;
    public IReadOnlyList<WorldObjectQuestLink> Quests { get; set; } = Array.Empty<WorldObjectQuestLink>();
}

/// <summary>世界物件清單與量距離的起點。</summary>
public sealed class WorldSurveyInfo
{
    public int OriginTile { get; set; }
    /// <summary>起點的白話名稱：商隊名或當前地圖名。</summary>
    public string OriginLabel { get; set; } = string.Empty;
    /// <summary>起點是商隊（行程時間用該商隊的速度）；false 則是當前地圖，時間以預設速度估算。</summary>
    public bool OriginIsCaravan { get; set; }
    public IReadOnlyList<WorldObjectInfo> Objects { get; set; } = Array.Empty<WorldObjectInfo>();
}

/// <summary>從起點走到某一格的行程估算（遊戲的 CaravanArrivalTimeEstimator，含夜間休息與道路）。</summary>
public sealed class TravelEstimateInfo
{
    public int Tile { get; set; }
    public bool Reachable { get; set; }
    /// <summary>大約要幾 tick；不可達或就在原地為 0。</summary>
    public int Ticks { get; set; }
    public float DistanceTiles { get; set; }
}

/// <summary>世界地圖上一格的地形資料。</summary>
public sealed class WorldTileInfo
{
    /// <summary>不在地表時的星球圖層（例如 Orbit）；在地表是空字串。</summary>
    public string TileLayer { get; set; } = string.Empty;
    /// <summary>非空代表這一格查不到（編號超出範圍…），內容是原因。</summary>
    public string Error { get; set; } = string.Empty;
    public int Tile { get; set; }
    public string Biome { get; set; } = string.Empty;
    public string Hilliness { get; set; } = string.Empty;
    /// <summary>年平均氣溫（攝氏）。</summary>
    public float TemperatureC { get; set; }
    public float MinTemperatureC { get; set; }
    public float MaxTemperatureC { get; set; }
    public float RainfallMm { get; set; }
    public float ElevationM { get; set; }
    public bool Impassable { get; set; }
    public bool WaterCovered { get; set; }
    public bool Coastal { get; set; }
    /// <summary>世界地貌名稱（山脈、海灣…）；沒有為空。</summary>
    public string Feature { get; set; } = string.Empty;
    public string Landmark { get; set; } = string.Empty;
    public IReadOnlyList<string> Mutators { get; set; } = Array.Empty<string>();
    /// <summary>這格目前能不能建立新殖民地（含玩家殖民地數量上限）。</summary>
    public bool CanSettle { get; set; }
    public string SettleBlockedReason { get; set; } = string.Empty;
    public IReadOnlyList<WorldObjectInfo> Objects { get; set; } = Array.Empty<WorldObjectInfo>();
}

/// <summary>某支商隊對某一格能下的命令，照原版右鍵選單（FloatMenuMakerWorld.ChoicesAtFor）產生。</summary>
public sealed class CaravanOptionsInfo
{
    /// <summary>非空代表選單做不出來（商隊不存在、格子不合法…），內容是原因。</summary>
    public string Error { get; set; } = string.Empty;
    public string CaravanName { get; set; } = string.Empty;
    /// <summary>1 起的序號與標籤；停用的選項原因寫在標籤裡。第 1 項固定是單純前往（沒有抵達動作）。</summary>
    public IReadOnlyList<FloatMenuOptionInfo> Options { get; set; } = Array.Empty<FloatMenuOptionInfo>();
}

/// <summary>商隊的行進控制。</summary>
public static class CaravanMovementAction
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Stop = "stop";
}

/// <summary>商隊在所在格建立新據點的方式。</summary>
public static class CaravanSettleAction
{
    /// <summary>建立新殖民地。</summary>
    public const string Settle = "settle";
    /// <summary>搭臨時營地（生成一張臨時地圖）。</summary>
    public const string Camp = "camp";
}
