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
        public IReadOnlyList<SkillInfo> Skills { get; set; } = Array.Empty<SkillInfo>();
        public IReadOnlyList<WorkPriorityInfo> WorkPriorities { get; set; } = Array.Empty<WorkPriorityInfo>();
        /// <summary>24 個小時各自的 TimeAssignment defName（索引即小時）。</summary>
        public IReadOnlyList<string> Timetable { get; set; } = Array.Empty<string>();
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

    /// <summary>買賣各一行：已解析到 defName 的品項與數量。</summary>
    public sealed class TradeLine
    {
        public string DefName { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>一次結清的交易請求：買賣明細一次交割，驗證不過則整筆不做。</summary>
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
}
