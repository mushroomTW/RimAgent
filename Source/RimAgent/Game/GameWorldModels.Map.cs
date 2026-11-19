using System;
using System.Collections.Generic;

namespace RimAgent.Game;

// IGameWorld 共用資料型別：地圖、物品查詢與格子勘查。
// 與 GameWorldModels.cs 相同：刻意不含任何 Verse / RimWorld 型別。

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
    /// <summary>
    /// 也納入迷霧中的物件；預設 false（玩家看不到的東西不透露）。
    /// 只給原版允許對迷霧下指派的用途（挖礦：Designator_Mine 接受迷霧格）。
    /// </summary>
    public bool IncludeFogged { get; set; }
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

/// <summary>地面能承載的建築重量等級（原版 TerrainAffordance 的 Light／Medium／Heavy 取最強者）。</summary>
public enum GroundSupport
{
    /// <summary>連輕型建築都不行（深水等）。</summary>
    None,
    Light,
    Medium,
    /// <summary>船體零件、反應爐等大型建築需要每格都是 Heavy。</summary>
    Heavy
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
    /// <summary>地面（地基優先，否則地形）能承載的最重建築等級；迷霧格為 <see cref="GroundSupport.None"/>。</summary>
    public GroundSupport Support { get; set; }
    public bool Fogged { get; set; }
    /// <summary>null 代表不在任何區域內。</summary>
    public ZoneInfo Zone { get; set; }
    /// <summary>null 代表格子被牆或岩石填滿，不屬於任何房間。</summary>
    public RoomInfo Room { get; set; }
    public IReadOnlyList<ThingInfo> Things { get; set; } = Array.Empty<ThingInfo>();
}

/// <summary>
/// 全圖勘查時每格的地貌分類。只看遊戲屬性（是否天然岩石、是否水、肥沃度、是否人造），
/// 不看 defName，模組加的地形與建築會自動落進對的類別。
/// </summary>
public enum SurveyCell : byte
{
    /// <summary>迷霧中，內容不透露。</summary>
    Unknown,
    /// <summary>天然岩壁（含礦脈；礦脈另記在 <see cref="MapSurvey.Resource"/>）。</summary>
    Rock,
    /// <summary>其他建築（牆、家具、機器等），代表已有人造結構。</summary>
    Structure,
    DeepWater,
    ShallowWater,
    /// <summary>人造地板或地基。</summary>
    BuiltFloor,
    /// <summary>天然地面，肥沃度 120% 以上（肥沃土壤）。</summary>
    RichSoil,
    /// <summary>天然地面，肥沃度 70%-120%（一般土壤、礫石）。</summary>
    Soil,
    /// <summary>天然地面，肥沃度 70% 以下（沙地、冰、岩地等）。</summary>
    Barren
}

/// <summary>全圖勘查結果（get_map_overview 的資料來源）。格子陣列的索引為 z * Width + x。</summary>
public sealed class MapSurvey
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Biome { get; set; } = string.Empty;
    public SurveyCell[] Cells { get; set; } = Array.Empty<SurveyCell>();
    /// <summary>每格的礦脈：0 為無，n 代表 <see cref="ResourceLabels"/>[n - 1]。</summary>
    public short[] Resource { get; set; } = Array.Empty<short>();
    public IReadOnlyList<string> ResourceLabels { get; set; } = Array.Empty<string>();
    /// <summary>每格是否在厚岩頂（山體）下；迷霧格一律 false。</summary>
    public bool[] ThickRoof { get; set; } = Array.Empty<bool>();
    /// <summary>室內房間（不含室外與碰到地圖邊緣的）。</summary>
    public IReadOnlyList<RoomInfo> Rooms { get; set; } = Array.Empty<RoomInfo>();
    public IReadOnlyList<ZoneInfo> Zones { get; set; } = Array.Empty<ZoneInfo>();
    /// <summary>家園區的範圍；沒有家園區時為 null。</summary>
    public CellRectSpec? HomeArea { get; set; }
    public int HomeAreaCells { get; set; }
    /// <summary>已出生在地圖上的自由殖民者位置。</summary>
    public IReadOnlyList<(int X, int Z)> Colonists { get; set; } = Array.Empty<(int X, int Z)>();
    /// <summary>不在迷霧中、未倒地的敵對 pawn 位置。</summary>
    public IReadOnlyList<(int X, int Z)> Hostiles { get; set; } = Array.Empty<(int X, int Z)>();
}
