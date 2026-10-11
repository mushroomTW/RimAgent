using System;
using System.Collections.Generic;
using RimAgent.Tools;

namespace RimAgent.Game
{
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

    /// <summary>組織一支商隊外出：指定的殖民者離開地圖往羅盤方向走。</summary>
    public sealed class CaravanRequest
    {
        public IReadOnlyList<int> PawnIds { get; set; } = Array.Empty<int>();
        /// <summary>羅盤方向（N、NE、E、SE、S、SW、W、NW，大小寫不敏感）。</summary>
        public string Heading { get; set; } = string.Empty;
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
}
