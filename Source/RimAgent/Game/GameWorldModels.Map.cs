#nullable disable

using System;
using System.Collections.Generic;

namespace RimAgent.Game;


public sealed class MapInfo
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Tile { get; set; }
    public int ColonistCount { get; set; }
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
    /// <summary>佔地超過一格。</summary>
    public bool MultiCell { get; set; }
    public bool PlayerOwned { get; set; }
    public bool Harvestable { get; set; }
    public bool Wild { get; set; }
    public bool Forbidden { get; set; }
    public IReadOnlyList<string> Designations { get; set; } = [];
}

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
    public string DefName { get; set; } = string.Empty;
    public string LabelContains { get; set; } = string.Empty;
    public CellRectSpec? Area { get; set; }
    public int Limit { get; set; } = 50;
    /// <summary>
    /// 也納入迷霧中的物件；預設 false（玩家看不到的東西不透露）。
    /// 只給原版允許對迷霧下指派的用途（挖礦：Designator_Mine 接受迷霧格）。
    /// </summary>
    public bool IncludeFogged { get; set; }
}

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

public sealed class RoomInfo
{
    public int Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public int CellCount { get; set; }
    public bool Outdoors { get; set; }
    public float TemperatureC { get; set; }
    public CellRectSpec Extents { get; set; }
}

public enum GroundSupport
{
    None,
    Light,
    Medium,
    Heavy
}

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
    /// <summary>太靠近地圖邊緣，遊戲不准建造。</summary>
    public bool NoBuildEdge { get; set; }
    public ZoneInfo Zone { get; set; }
    public RoomInfo Room { get; set; }
    public IReadOnlyList<ThingInfo> Things { get; set; } = [];
}

public enum SurveyCell : byte
{
    Unknown,
    Rock,
    Structure,
    DeepWater,
    ShallowWater,
    BuiltFloor,
    RichSoil,
    Soil,
    Barren
}

public sealed class MapSurvey
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Biome { get; set; } = string.Empty;
    public SurveyCell[] Cells { get; set; } = [];
    /// <summary>每格的礦脈：0 為無，n 代表 <see cref="ResourceLabels"/>[n - 1]。</summary>
    public short[] Resource { get; set; } = [];
    public IReadOnlyList<string> ResourceLabels { get; set; } = [];
    public bool[] ThickRoof { get; set; } = [];
    public IReadOnlyList<RoomInfo> Rooms { get; set; } = [];
    public IReadOnlyList<ZoneInfo> Zones { get; set; } = [];
    public CellRectSpec? HomeArea { get; set; }
    public int HomeAreaCells { get; set; }
    public IReadOnlyList<(int X, int Z)> Colonists { get; set; } = [];
    public IReadOnlyList<(int X, int Z)> Hostiles { get; set; } = [];
}
