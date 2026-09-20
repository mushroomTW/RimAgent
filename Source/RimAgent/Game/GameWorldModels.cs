using System;
using System.Collections.Generic;

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
        /// <summary>HostilePawn / Manhunter / Predator / Fire。</summary>
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
    public sealed class CommandResult
    {
        private CommandResult(bool success, string message)
        {
            Success = success;
            Message = message ?? string.Empty;
        }

        public bool Success { get; }
        public string Message { get; }

        public static CommandResult Ok(string message = "")
        {
            return new CommandResult(true, message);
        }

        public static CommandResult Fail(string message)
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
