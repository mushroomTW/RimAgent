using System;
using System.Collections.Generic;
using RimAgent.Tools;

namespace RimAgent.Game;

// IGameWorld 兩側共用的純資料型別。刻意不含任何 Verse / RimWorld 型別，
// 讓工具邏輯與測試都不需要載入遊戲組件。

/// <summary>殖民地一眼看盡的摘要。</summary>
public sealed class ColonyOverview
{
    public string ColonyName { get; set; } = string.Empty;
    public int ColonistCount { get; set; }
    public int Year { get; set; }
    public string Season { get; set; } = string.Empty;
    public int DayOfSeason { get; set; }
    public int Hour { get; set; }
    public string Weather { get; set; } = string.Empty;
    public float OutdoorTemperatureC { get; set; }
    public float Wealth { get; set; }
    public int HostileCount { get; set; }
    /// <summary>心情已低於輕微崩潰門檻的殖民者人數。</summary>
    public int ColonistsNearMentalBreak { get; set; }
    /// <summary>看得到的派系與對玩家的好感。</summary>
    public IReadOnlyList<string> Factions { get; set; } = Array.Empty<string>();
}

public sealed class SkillInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Level { get; set; }
    /// <summary>0 = 無熱情，1 = 小熱情，2 = 大熱情。</summary>
    public int Passion { get; set; }
    public bool Disabled { get; set; }
}

public sealed class WorkPriorityInfo
{
    public string WorkTypeDefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>0 = 不做；1 最高 … 4 最低。</summary>
    public int Priority { get; set; }
    public bool Disabled { get; set; }
}

public sealed class ColonistInfo
{
    /// <summary>帝國頭銜與榮譽；沒有是空字串。</summary>
    public string RoyalStatus { get; set; } = string.Empty;
    public int Id { get; set; }
    /// <summary>顯示用短名（綽號優先）。</summary>
    public string Name { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public int Age { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public float HealthPercent { get; set; }
    public bool Downed { get; set; }
    public float MoodPercent { get; set; }
    public float MinorBreakThreshold { get; set; }
    public bool InMentalState { get; set; }
    public string MentalStateLabel { get; set; } = string.Empty;
    public string CurrentActivity { get; set; } = string.Empty;
    public bool Drafted { get; set; }
    /// <summary>醫療等級（MedicalCareCategory 名稱，如 Best）。</summary>
    public string MedicalCare { get; set; } = string.Empty;
    public string Equipment { get; set; } = string.Empty;
    public IReadOnlyList<NeedInfo> Needs { get; set; } = Array.Empty<NeedInfo>();
    public IReadOnlyList<string> Thoughts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<SkillInfo> Skills { get; set; } = Array.Empty<SkillInfo>();
    public IReadOnlyList<WorkPriorityInfo> WorkPriorities { get; set; } = Array.Empty<WorkPriorityInfo>();
    /// <summary>24 個小時各自的 TimeAssignment defName（索引即小時）。</summary>
    public IReadOnlyList<string> Timetable { get; set; } = Array.Empty<string>();
    /// <summary>因其他模組丟例外而讀不到的段落（如 <see cref="WorkPrioritiesPart"/>）；這些欄位是空的，不代表「沒有」。</summary>
    public IReadOnlyList<string> UnreadableParts { get; set; } = Array.Empty<string>();

    public const string WorkPrioritiesPart = "work priorities";
}

/// <summary>殖民者需求的遊戲原始讀數；百分比為 0 到 1。</summary>
public sealed class NeedInfo
{
    public string Label { get; set; } = string.Empty;
    public float Percent { get; set; }
}

public sealed class ResourceInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Category { get; set; } = string.Empty;
}

public sealed class ResourceSnapshot
{
    public IReadOnlyList<ResourceInfo> Items { get; set; } = Array.Empty<ResourceInfo>();
    public float TotalHumanEdibleNutrition { get; set; }
}

public sealed class AlertInfo
{
    public string Label { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
}

public sealed class LetterInfo
{
    public string Label { get; set; } = string.Empty;
}

/// <summary>等玩家回應的抉擇：還沒處理的抉擇信件，或開著的對話框（id 以 letter-／dialog- 開頭）。</summary>
public sealed class PendingChoiceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>信件還剩幾 tick 過期；不會過期為 null。</summary>
    public int? ExpiresInTicks { get; set; }
    public IReadOnlyList<ChoiceOptionInfo> Options { get; set; } = Array.Empty<ChoiceOptionInfo>();
    /// <summary>回答時要附的輸入格式（世界格子、地圖目標、選取項目）；不用輸入是空字串。</summary>
    public string InputHint { get; set; } = string.Empty;
}

/// <summary>選取物件時畫面下方的指令按鈕（gizmo）。</summary>
public sealed class GizmoInfo
{
    /// <summary>action（按一下）、toggle（開關）、target（要在地圖上選目標）或 unsupported（要從選單挑或需要其他輸入）。</summary>
    public const string ActionKind = "action";
    public const string ToggleKind = "toggle";
    /// <summary>按下後要在地圖上選目標（物件或格子）；可接受的目標見 <see cref="TargetHint"/>。</summary>
    public const string TargetKind = "target";
    public const string UnsupportedKind = "unsupported";

    /// <summary>1 起的序號，照遊戲裡的排列。</summary>
    public int Number { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Kind { get; set; } = ActionKind;
    /// <summary>開關目前是否開著（只對 toggle 有意義）。</summary>
    public bool On { get; set; }
    /// <summary>target 按鈕可接受的目標種類（遊戲的 TargetingParameters 白話），例如「a cell, a colonist」。</summary>
    public string TargetHint { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
}

/// <summary>命令指向的地圖目標：一個物件（ThingId）或一個格子（X、Z）。</summary>
public sealed class MapTarget
{
    public int ThingId { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public bool IsCell { get; set; }

    public static MapTarget OfThing(int thingId)
    {
        return new MapTarget { ThingId = thingId };
    }

    public static MapTarget OfCell(int x, int z)
    {
        return new MapTarget { IsCell = true, X = x, Z = z };
    }

    public override string ToString()
    {
        return IsCell ? "cell (" + X + "," + Z + ")" : "[" + ThingId + "]";
    }
}

/// <summary>右鍵選單（FloatMenu）的一個選項。</summary>
public sealed class FloatMenuOptionInfo
{
    /// <summary>1 起的序號，照原版選單排序（優先度高者在前，停用者在最後）。</summary>
    public int Number { get; set; }
    /// <summary>選項文字；停用的選項原因就寫在文字裡（原版慣例，例如「Use X (no path)」）。</summary>
    public string Label { get; set; } = string.Empty;
    public bool Disabled { get; set; }
}

/// <summary>某位殖民者對某個目標按右鍵會得到的選單。</summary>
public sealed class FloatMenuInfo
{
    /// <summary>非空代表選單做不出來（殖民者無法下令、目標不存在…），內容是遊戲給的原因。</summary>
    public string Error { get; set; } = string.Empty;
    public string PawnName { get; set; } = string.Empty;
    /// <summary>目標的白話描述（物件標籤或格子座標）。</summary>
    public string TargetLabel { get; set; } = string.Empty;
    /// <summary>殖民者目前是否徵召：徵召與否會換一批選項。</summary>
    public bool Drafted { get; set; }
    public IReadOnlyList<FloatMenuOptionInfo> Options { get; set; } = Array.Empty<FloatMenuOptionInfo>();
}

public sealed class ChoiceOptionInfo
{
    /// <summary>1 起的選項序號（資訊卡連結不列出，序號仍照遊戲裡的位置）。</summary>
    public int Number { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public string DisabledReason { get; set; } = string.Empty;
}

/// <summary>任務分頁裡的一個任務：可接受（含被拒絕的）或進行中。</summary>
public sealed class QuestInfo
{
    public const string AvailableState = "available";
    public const string ActiveState = "active";

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>available（還沒接受）或 active（已接受、進行中）。</summary>
    public string State { get; set; } = AvailableState;
    /// <summary>玩家在任務分頁按過拒絕（可再取消）；只對 available 有意義。</summary>
    public bool Dismissed { get; set; }
    /// <summary>純文字描述（標籤已去除，含各任務段落附加的說明）。</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>還剩幾 tick 過期（只對 available）；沒有期限為 null。</summary>
    public int? ExpiresInTicks { get; set; }
    /// <summary>進行中任務的完成期限，遊戲給的原文（例如「剩 3 天」）；沒有則為空。</summary>
    public IReadOnlyList<string> Deadlines { get; set; } = Array.Empty<string>();
    /// <summary>已接受者的名字（只對 active，且該任務有接受者）。</summary>
    public string AccepterName { get; set; } = string.Empty;
    /// <summary>遊戲目前是否允許接受（只對 available）。</summary>
    public bool CanAccept { get; set; } = true;
    /// <summary>接受條件不滿足的原因，遊戲給的原文；CanAccept 為 false 時至少有一筆。</summary>
    public IReadOnlyList<string> UnmetRequirements { get; set; } = Array.Empty<string>();
    /// <summary>獎勵選項；只有一組也算一個選項。沒有獎勵選擇的任務為空。</summary>
    public IReadOnlyList<QuestRewardInfo> RewardOptions { get; set; } = Array.Empty<QuestRewardInfo>();
    /// <summary>接受時要指名接受者（沒有獎勵選項的任務看這個；有選項的任務以各選項的 RequiresAccepter 為準）。</summary>
    public bool RequiresAccepter { get; set; }
    /// <summary>目前能當接受者的殖民者（只在任務需要接受者時列出）。</summary>
    public IReadOnlyList<QuestAccepterInfo> Accepters { get; set; } = Array.Empty<QuestAccepterInfo>();
}

/// <summary>任務的一組獎勵選項（接受時選其一）。</summary>
public sealed class QuestRewardInfo
{
    /// <summary>1 起的序號。</summary>
    public int Number { get; set; }
    public IReadOnlyList<string> Rewards { get; set; } = Array.Empty<string>();
    /// <summary>選了這一組之後，接受時是否要指名接受者。</summary>
    public bool RequiresAccepter { get; set; }
}

/// <summary>可以接受某個任務的殖民者。</summary>
public sealed class QuestAccepterInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>遊戲在確認框提醒的風險（個性、社交能力等），沒有則為空。</summary>
    public string Warning { get; set; } = string.Empty;
}

public sealed class PowerInfo
{
    public int NetworkCount { get; set; }
    public float NetWatts { get; set; }
    public float StoredEnergy { get; set; }
}

public sealed class ThreatInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <summary>HostilePawn / HostileAnimal / Manhunter / Mechanoid / Entity / Fire。</summary>
    public string Kind { get; set; } = string.Empty;
    public string Faction { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public bool Downed { get; set; }
}

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

/// <summary>一張可接帳單的工作台（遊戲內有 BillStack 的建築）。</summary>
public sealed class WorkTableInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public int BillCount { get; set; }
}

/// <summary>工作台上的一張生產帳單。Index 是當次列表序號（1 起），刪單前重查。</summary>
public sealed class BillInfo
{
    public int Index { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <summary>重複方式顯示，如 3x、until 10、forever。</summary>
    public string Repeat { get; set; } = string.Empty;
}

/// <summary>地圖上的一個區域（儲藏區、種植區等）。</summary>
public sealed class ZoneInfo
{
    /// <summary>ZoneManager 配發的編號（查詢工具回傳的不透明編號，指名區域用它）。</summary>
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    /// <summary>Stockpile / Growing 等。</summary>
    public string Kind { get; set; } = string.Empty;
    public int CellCount { get; set; }
}

/// <summary>地圖上的一隻動物（殖民地或野生）。</summary>
public sealed class AnimalInfo
{
    public int Id { get; set; }
    /// <summary>顯示名（命名動物為名字，否則為物種）。</summary>
    public string Name { get; set; } = string.Empty;
    public string SpeciesDefName { get; set; } = string.Empty;
    public string Species { get; set; } = string.Empty;
    /// <summary>是否野生（非殖民地所有）。</summary>
    public bool Wild { get; set; }
    /// <summary>活動區域；空字串代表無限制。</summary>
    public string Area { get; set; } = string.Empty;
    public float HealthPercent { get; set; } = 1f;
    public bool Downed { get; set; }
}

/// <summary>一位殖民地囚犯。</summary>
public sealed class PrisonerInfo
{
    public int Id { get; set; }
    /// <summary>顯示用短名（綽號優先）。</summary>
    public string Name { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    /// <summary>剩餘抗拒值；負值代表無需勸降（可直接招募）。</summary>
    public float Resistance { get; set; } = -1f;
    public bool Recruitable { get; set; }
    /// <summary>目前互動模式 defName（如 AttemptRecruit）。</summary>
    public string InteractionMode { get; set; } = string.Empty;
    public string InteractionModeLabel { get; set; } = string.Empty;
    public float HealthPercent { get; set; } = 1f;
    public bool Downed { get; set; }
}

/// <summary>排一台手術：把一張 Bill_Medical 掛進殖民者的 BillStack，等醫生執行。</summary>
public sealed class SurgeryRequest
{
    public int PawnId { get; set; }
    public string RecipeDefName { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串由實作端選第一個適用部位（不需部位的手術忽略）。</summary>
    public string BodyPartLabel { get; set; } = string.Empty;
}

/// <summary>一條健康異常：傷口、疾病或缺失部位。</summary>
public sealed class HealthConditionInfo
{
    /// <summary>外傷（割傷、槍傷、燒傷等）。</summary>
    public const string InjuryKind = "Injury";
    /// <summary>缺失的身體部位。</summary>
    public const string MissingKind = "Missing";
    /// <summary>其他（疾病、慢性病、植入物副作用等）。</summary>
    public const string ConditionKind = "Condition";

    public string Label { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串代表全身。</summary>
    public string Part { get; set; } = string.Empty;
    public float Severity { get; set; }
    /// <summary>見 <see cref="InjuryKind"/>／<see cref="MissingKind"/>／<see cref="ConditionKind"/>。</summary>
    public string Kind { get; set; } = ConditionKind;
    public bool Bleeding { get; set; }
    public bool Tended { get; set; }
    public bool NeedsTending { get; set; }
}

/// <summary>已排上但還沒動的手術。</summary>
public sealed class SurgeryBillInfo
{
    public string Label { get; set; } = string.Empty;
    /// <summary>部位顯示名；空字串代表全身。</summary>
    public string Part { get; set; } = string.Empty;
    public bool Suspended { get; set; }
}

/// <summary>一位殖民者的健康狀況。</summary>
public sealed class ColonistHealthInfo
{
    public int PawnId { get; set; }
    public IReadOnlyList<HealthConditionInfo> Conditions { get; set; } = Array.Empty<HealthConditionInfo>();
    public IReadOnlyList<SurgeryBillInfo> PendingSurgeries { get; set; } = Array.Empty<SurgeryBillInfo>();
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

/// <summary>遊戲時間流速：目前速度與可切換的速度目錄。</summary>
public sealed class TimeSpeedInfo
{
    /// <summary>目前速度 defName（Paused／Normal／Fast／Superfast）。</summary>
    public string Current { get; set; } = "Normal";
    /// <summary>可切換的速度目錄。</summary>
    public IReadOnlyList<DefInfo> Available { get; set; } = Array.Empty<DefInfo>();
}

/// <summary>玩家可按鍵切到的四檔速度目錄；Ultrafast 只留給開發模式。正式與假世界共用同一份。</summary>
public static class TimeSpeedDirectory
{
    public static readonly IReadOnlyList<DefInfo> Default = new[]
    {
        new DefInfo("Paused", "Paused"), new DefInfo("Normal", "Normal"),
        new DefInfo("Fast", "Fast"), new DefInfo("Superfast", "Superfast")
    };
}

public sealed class ResearchProjectInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public float ProgressPercent { get; set; }
    public float Cost { get; set; }
}

public sealed class ResearchInfo
{
    /// <summary>null 代表目前沒有進行中的專案。</summary>
    public ResearchProjectInfo Current { get; set; }
    public IReadOnlyList<ResearchProjectInfo> Available { get; set; } = Array.Empty<ResearchProjectInfo>();
}

/// <summary>存檔裡的一張地圖。Id 為遊戲配發的編號（查詢工具回傳的不透明編號，切換地圖用它）。</summary>
public sealed class MapInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Tile { get; set; }
    public int ColonistCount { get; set; }
    /// <summary>是否為工具目前操作的地圖（Find.CurrentMap）。</summary>
    public bool IsCurrent { get; set; }
}

public enum ThingKind
{
    Any,
    Mineable,
    Plant,
    Building,
    Item,
    Pawn
}

public sealed class ThingInfo
{
    public int Id { get; set; }
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public ThingKind Kind { get; set; }
    public int X { get; set; }
    public int Z { get; set; }
    public int StackCount { get; set; } = 1;
    public bool PlayerOwned { get; set; }
    /// <summary>植物是否已可收成。</summary>
    public bool Harvestable { get; set; }
    public bool Wild { get; set; }
    /// <summary>殖民者是否被禁止碰它（禁用的物品不會被搬運或使用）。</summary>
    public bool Forbidden { get; set; }
    /// <summary>目前掛在這個物件（或其所在格）上的標記 defName。</summary>
    public IReadOnlyList<string> Designations { get; set; } = Array.Empty<string>();
}

/// <summary>
/// FindThings 的結果：只含排在最前面、不超過 Limit 筆的物件；TotalMatches 是符合條件的總數，
/// 超過 Limit 時工具要告訴模型還有多少沒列出。排序規則見 <see cref="IGameWorld.FindThings"/>。
/// </summary>
public sealed class ThingSearchResult : IReadOnlyList<ThingInfo>
{
    private readonly IReadOnlyList<ThingInfo> _things;

    public ThingSearchResult(IReadOnlyList<ThingInfo> things, int totalMatches)
    {
        _things = things ?? throw new ArgumentNullException(nameof(things));
        TotalMatches = Math.Max(totalMatches, things.Count);
    }

    public int TotalMatches { get; }
    public int Count => _things.Count;
    public ThingInfo this[int index] => _things[index];
    public IEnumerator<ThingInfo> GetEnumerator() => _things.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class ThingQuery
{
    public ThingKind Kind { get; set; } = ThingKind.Any;
    /// <summary>精確比對 defName（大小寫不敏感）；空字串代表不限。</summary>
    public string DefName { get; set; } = string.Empty;
    /// <summary>label 包含比對（大小寫不敏感）；空字串代表不限。</summary>
    public string LabelContains { get; set; } = string.Empty;
    public CellRectSpec? Area { get; set; }
    public int Limit { get; set; } = 50;
}

/// <summary>地圖座標矩形（含邊界）。以 FromCorners 建立時會自動正規化角落順序。</summary>
public readonly struct CellRectSpec
{
    public CellRectSpec(int minX, int minZ, int maxX, int maxZ)
    {
        MinX = minX;
        MinZ = minZ;
        MaxX = maxX;
        MaxZ = maxZ;
    }

    public int MinX { get; }
    public int MinZ { get; }
    public int MaxX { get; }
    public int MaxZ { get; }
    public int CellCount => (MaxX - MinX + 1) * (MaxZ - MinZ + 1);

    public static CellRectSpec FromCorners(int x1, int z1, int x2, int z2)
    {
        return new CellRectSpec(Math.Min(x1, x2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(z1, z2));
    }

    public bool Contains(int x, int z)
    {
        return x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    }

    public override string ToString()
    {
        return $"({MinX},{MinZ})-({MaxX},{MaxZ})";
    }
}

public sealed class MapBounds
{
    public int Width { get; set; }
    public int Height { get; set; }

    public bool Contains(int x, int z)
    {
        return x >= 0 && z >= 0 && x < Width && z < Height;
    }
}

/// <summary>一格所屬的房間（遊戲的 Room：牆與門圍出的連通區域；室外也是一個房間）。</summary>
public sealed class RoomInfo
{
    public int Id { get; set; }
    /// <summary>遊戲判定的用途（Bedroom、Kitchen、None…）。</summary>
    public string Role { get; set; } = string.Empty;
    public int CellCount { get; set; }
    public bool Outdoors { get; set; }
    public float TemperatureC { get; set; }
    /// <summary>房間所有格子的外框（含邊界）。</summary>
    public CellRectSpec Extents { get; set; }
}

/// <summary>一格地面的狀態：地形、屋頂、可否行走、所屬區域與房間、格上的物件。</summary>
public sealed class CellInfo
{
    public int X { get; set; }
    public int Z { get; set; }
    public string TerrainDefName { get; set; } = string.Empty;
    public string Terrain { get; set; } = string.Empty;
    /// <summary>0 不可種植；1 為一般土壤。</summary>
    public float Fertility { get; set; }
    /// <summary>屋頂：空字串無屋頂；constructed／natural（薄岩頂）／overhead mountain（厚岩頂，不可拆）。</summary>
    public string Roof { get; set; } = string.Empty;
    public bool Walkable { get; set; }
    public bool Fogged { get; set; }
    /// <summary>null 代表不在任何區域內。</summary>
    public ZoneInfo Zone { get; set; }
    /// <summary>null 代表格子被牆或岩石填滿，不屬於任何房間。</summary>
    public RoomInfo Room { get; set; }
    public IReadOnlyList<ThingInfo> Things { get; set; } = Array.Empty<ThingInfo>();
}

/// <summary>字元網格上一格的 pawn：同格有多個時由世界層挑出現在的優先序（敵對 > 殖民者 > 其他）。</summary>
public enum GridPawn
{
    None,
    Other,
    Colonist,
    Hostile
}

/// <summary>字元網格上一格的建築類：列舉值越大越優先（同格有多筆時取最大）。不含天然岩石，岩石另有欄位。</summary>
public enum GridBuilding
{
    None,
    Blueprint,
    Frame,
    Other,
    /// <summary>可穿過、遮蔽率至少一半的掩體（沙包、路障）。</summary>
    Cover,
    Turret,
    Door,
    Wall
}

public enum GridTerrain
{
    Ground,
    Floor,
    ShallowWater,
    DeepWater
}

/// <summary>地形能承載的建築重量等級（取 Heavy／Medium／Light 中最強的一個）；None 代表三種都不行（深水等）。</summary>
public enum GridAffordance
{
    None,
    Light,
    Medium,
    Heavy
}

public enum GridPlant
{
    None,
    Other,
    Tree
}

public enum GridZone
{
    None,
    Stockpile,
    Growing
}

public enum GridRoof
{
    None,
    Constructed,
    Natural,
    Mountain
}

/// <summary>
/// 字元網格的一格：世界層只分類每個圖層的狀態，圖層之間的優先序與字元由工具層決定。
/// 迷霧中的格子只有 Fogged 有意義，其他欄位一律是預設值（不透露玩家看不到的東西）。
/// </summary>
public sealed class MapGridCell
{
    public bool Fogged { get; set; }
    public GridPawn Pawn { get; set; }
    public GridBuilding Building { get; set; }
    public bool HasItem { get; set; }
    /// <summary>天然岩石或礦脈（可開採的牆）。</summary>
    public bool Rock { get; set; }
    public GridTerrain Terrain { get; set; }
    public GridPlant Plant { get; set; }
    public GridZone Zone { get; set; }
    public GridRoof Roof { get; set; }
    /// <summary>build 圖層：格子已被佔走——格上有建築、藍圖、框架（地板與電線類除外）或天然岩石，蓋別的建築前得先移除或換地方。</summary>
    public bool Blocked { get; set; }
    /// <summary>build 圖層：地形（含地基）承載的最強建築重量等級。</summary>
    public GridAffordance BuildSupport { get; set; }
}

/// <summary>建築造價的一項材料。</summary>
public sealed class BuildingCost
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>建築的一項研發前置與是否已完成。</summary>
public sealed class BuildingResearch
{
    public string Label { get; set; } = string.Empty;
    public bool Done { get; set; }
}

/// <summary>
/// 一個可建造項目（建築或地板）的規格：尺寸、造價、前置條件與放置限制。
/// 尺寸是朝北時的寬（x）與深（z）；朝東西時寬深對調。
/// </summary>
public sealed class BuildingInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    /// <summary>地板類（TerrainDef）；沒有建築專屬的欄位。</summary>
    public bool IsTerrain { get; set; }
    public IReadOnlyList<BuildingCost> Costs { get; set; } = Array.Empty<BuildingCost>();
    /// <summary>需要材料（stuff）時的用量；0 表示不用選材料。</summary>
    public int StuffCount { get; set; }
    public IReadOnlyList<string> StuffCategories { get; set; } = Array.Empty<string>();
    /// <summary>地形承載需求的 defName（Light、Medium、Heavy…）；空字串表示沒有要求。</summary>
    public string TerrainAffordance { get; set; } = string.Empty;
    /// <summary>承載需求隨選用的材料而變。</summary>
    public bool AffordanceFromStuff { get; set; }
    public IReadOnlyList<BuildingResearch> Research { get; set; } = Array.Empty<BuildingResearch>();
    public int ConstructionSkill { get; set; }
    public int ArtisticSkill { get; set; }
    /// <summary>放置檢查（PlaceWorker）的類別名稱。</summary>
    public IReadOnlyList<string> PlaceWorkers { get; set; } = Array.Empty<string>();
    public bool ShipPart { get; set; }
    /// <summary>電力：正數耗電、負數發電（瓦）；0 表示沒有電力元件或不耗電。</summary>
    public float PowerWatts { get; set; }
    /// <summary>互動格相對中心的偏移（朝北時；隨旋轉轉動），例如工作台站的位置。</summary>
    public IReadOnlyList<(int X, int Z)> InteractionOffsets { get; set; } = Array.Empty<(int X, int Z)>();
}

public enum ShipPartState
{
    Built,
    Frame,
    Blueprint
}

public sealed class ShipPartInfo
{
    public int Id { get; set; }
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public ShipPartState State { get; set; }
    public CellRectSpec Rect { get; set; }
    /// <summary>殖民者操作的站位（休眠艙從這格進入）；沒有就是 null。</summary>
    public (int X, int Z)? InteractionCell { get; set; }
    /// <summary>站位目前被什麼擋住（地圖邊界或不可通行的東西）；沒擋住是 null。</summary>
    public string InteractionBlockedBy { get; set; }
}

public sealed class ShipPartRequirement
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class DefInfo
{
    public DefInfo()
    {
    }

    public DefInfo(string defName, string label)
    {
        DefName = defName;
        Label = label;
    }

    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

/// <summary>lookup_def 可查的 Def 種類。</summary>
public static class DefLookupKind
{
    public const string Thing = "thing";
    public const string Terrain = "terrain";
    public const string Research = "research";
    public const string Hediff = "hediff";

    public static readonly IReadOnlyList<string> All = [Thing, Terrain, Research, Hediff];
}

/// <summary>一個 Def 的資訊卡內容（同遊戲「i」資訊卡）加上種類專屬的事實（取得方式、研究狀態等）。</summary>
public sealed class DefLookupInfo
{
    public string Kind { get; set; } = string.Empty;
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>資訊卡數值，已依遊戲顯示順序排好（類別順序 → 類別內優先度）。</summary>
    public IReadOnlyList<DefStatEntry> Stats { get; set; } = [];
    /// <summary>種類專屬事實，每則一行（如製作配方、收成來源、研究前置與解鎖項目）。</summary>
    public IReadOnlyList<string> Facts { get; set; } = [];
}

public sealed class DefStatEntry
{
    /// <summary>StatCategoryDef 的 defName（detail 參數可用它或 CategoryLabel 篩選）。</summary>
    public string Category { get; set; } = string.Empty;
    public string CategoryLabel { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>命令型操作的結果。失敗時 Message 說明原因，供工具轉述給模型。</summary>
public sealed class CommandResult : Result<string>
{
    private CommandResult(bool success, string message) : base(success, message, message)
    {
    }

    public string Message => Value ?? string.Empty;

    public static new CommandResult Ok(string message = "")
    {
        return new CommandResult(true, message);
    }

    public static new CommandResult Fail(string message)
    {
        return new CommandResult(false, message);
    }
}

/// <summary>遊戲截圖結果資料。</summary>
public sealed class ScreenshotInfo
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>存讀檔工具需要的這一局狀態。</summary>
public sealed class SaveContext
{
    /// <summary>這一局的 RimAgent 殖民地代號（寫在每份存檔裡，用來認出同一局的 autosave）。</summary>
    public string ColonyId { get; set; } = string.Empty;
    public int Tick { get; set; }
    /// <summary>死亡模式（只有一份會被覆寫的存檔）：不能存讀檔重來。</summary>
    public bool Permadeath { get; set; }
    /// <summary>現在不能存檔的原因（選地塊、對話框禁止存檔、過場中）；可以存時為 null。</summary>
    public string SavingBlockedReason { get; set; }
}

/// <summary>一份可能屬於這一局的存檔：RimAgent 檢查點或遊戲 autosave。</summary>
public sealed class SaveSlotInfo
{
    /// <summary>存檔名（不含副檔名），讀檔用。</summary>
    public string Name { get; set; } = string.Empty;
    public bool Checkpoint { get; set; }
    /// <summary>存檔裡記的殖民地代號；沒有代號（本功能之前的存檔）為空字串。</summary>
    public string ColonyId { get; set; } = string.Empty;
    /// <summary>存檔時的遊戲 tick。</summary>
    public int Tick { get; set; }
    /// <summary>檔案最後寫入時間（UTC ticks）：autosave 會輪流覆寫，名稱加上它才是同一份存檔。</summary>
    public long WrittenUtcTicks { get; set; }
}
