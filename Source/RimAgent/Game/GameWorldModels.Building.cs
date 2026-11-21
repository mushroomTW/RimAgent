#nullable disable

using System;
using System.Collections.Generic;

namespace RimAgent.Game;


public sealed class BuildingCost
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class BuildingResearch
{
    public string Label { get; set; } = string.Empty;
    public bool Done { get; set; }
}

/// <summary>尺寸是朝北時的寬（x）與深（z）；朝東西時寬深對調。</summary>
public sealed class BuildingInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public bool IsTerrain { get; set; }
    public IReadOnlyList<BuildingCost> Costs { get; set; } = [];
    public int StuffCount { get; set; }
    public IReadOnlyList<string> StuffCategories { get; set; } = [];
    public string TerrainAffordance { get; set; } = string.Empty;
    /// <summary>承載需求隨選用的材料而變。</summary>
    public bool AffordanceFromStuff { get; set; }
    public IReadOnlyList<BuildingResearch> Research { get; set; } = [];
    public int ConstructionSkill { get; set; }
    public int ArtisticSkill { get; set; }
    public IReadOnlyList<string> PlaceWorkers { get; set; } = [];
    public bool ShipPart { get; set; }
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
    public (int X, int Z)? InteractionCell { get; set; }
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

public sealed class LayoutInfo
{
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
}

public enum LayoutPart
{
    Furniture,
    Wall,
    Floor
}

public sealed class LayoutPiece
{
    public string DefName { get; set; } = string.Empty;
    public LayoutPart Part { get; set; }
    public string StuffDefName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Z { get; set; }
    public int Rotation { get; set; }
}

public sealed class LayoutPlan
{
    public CellRectSpec Rect { get; set; }
    public IReadOnlyList<LayoutPiece> Pieces { get; set; } = [];
}

public static class DefLookupKind
{
    public const string Thing = "thing";
    public const string Terrain = "terrain";
    public const string Research = "research";
    public const string Hediff = "hediff";

    public static readonly IReadOnlyList<string> All = [Thing, Terrain, Research, Hediff];
}

public sealed class DefLookupInfo
{
    public string Kind { get; set; } = string.Empty;
    public string DefName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IReadOnlyList<DefStatEntry> Stats { get; set; } = [];
    public IReadOnlyList<string> Facts { get; set; } = [];
}

public sealed class DefStatEntry
{
    public string Category { get; set; } = string.Empty;
    public string CategoryLabel { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
