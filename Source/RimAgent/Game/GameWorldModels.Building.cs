#nullable disable

using System;
using System.Collections.Generic;

namespace RimAgent.Game;

// IGameWorld 共用資料型別：建築、太空船零件、Def 查詢與房型。
// 與 GameWorldModels.cs 相同：刻意不含任何 Verse / RimWorld 型別。

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
    public IReadOnlyList<BuildingCost> Costs { get; set; } = [];
    /// <summary>需要材料（stuff）時的用量；0 表示不用選材料。</summary>
    public int StuffCount { get; set; }
    public IReadOnlyList<string> StuffCategories { get; set; } = [];
    /// <summary>地形承載需求的 defName（Light、Medium、Heavy…）；空字串表示沒有要求。</summary>
    public string TerrainAffordance { get; set; } = string.Empty;
    /// <summary>承載需求隨選用的材料而變。</summary>
    public bool AffordanceFromStuff { get; set; }
    public IReadOnlyList<BuildingResearch> Research { get; set; } = [];
    public int ConstructionSkill { get; set; }
    public int ArtisticSkill { get; set; }
    /// <summary>放置檢查（PlaceWorker）的類別名稱。</summary>
    public IReadOnlyList<string> PlaceWorkers { get; set; } = [];
    public bool ShipPart { get; set; }
    /// <summary>電力：正數耗電、負數發電（瓦）；0 表示沒有電力元件或不耗電。</summary>
    public float PowerWatts { get; set; }
    /// <summary>互動格相對中心的偏移（朝北時；隨旋轉轉動），例如工作台站的位置。</summary>
    public IReadOnlyList<(int X, int Z)> InteractionOffsets { get; set; } = [];
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

/// <summary>預先設計好的房型（本模組提供的 PrefabDef）。</summary>
public sealed class LayoutInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>房型裡的一件屬於哪一類，決定套用哪一種指定材料。</summary>
public enum LayoutPart
{
    /// <summary>牆與門以外的建築（家具、燈、工作台等）。</summary>
    Furniture,
    /// <summary>牆與門。</summary>
    Wall,
    /// <summary>地板（地形）。</summary>
    Floor
}

/// <summary>房型展開後的一件建築或地板；座標與朝向已套用房型的位置與旋轉。</summary>
public sealed class LayoutPiece
{
    public string DefName { get; set; } = string.Empty;
    public LayoutPart Part { get; set; }
    /// <summary>房型指定的材料；空字串表示用預設材料。</summary>
    public string StuffDefName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public int Rotation { get; set; }
}

/// <summary>房型放在某處時涵蓋的範圍與要放的每一件（建築在前、地板在後）。</summary>
public sealed class LayoutPlan
{
    public CellRectSpec Rect { get; set; }
    public IReadOnlyList<LayoutPiece> Pieces { get; set; } = [];
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
