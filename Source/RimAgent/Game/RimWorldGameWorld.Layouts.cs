using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimAgent.Game;

/// <summary>
/// 房型：本模組 Defs 裡以 RimAgent_ 開頭的 PrefabDef。位置與旋轉沿用原版 PrefabUtility 的算法，
/// 但不呼叫 SpawnPrefab：它的藍圖模式會直接鋪地板、掛牆物件也不變藍圖，所以這裡只展開成清單，交給一般藍圖放置。
/// </summary>
internal sealed partial class RimWorldGameWorld
{
    /// <summary>只開放本模組的房型，原版與其他模組的 PrefabDef 多半是遺跡、地圖生成用的。</summary>
    private const string LayoutPrefix = "RimAgent_";

    /// <summary>Def 載入後不會變，第一次用到時算一次。</summary>
    private static IReadOnlyList<LayoutInfo> _layouts;

    public IReadOnlyList<LayoutInfo> Layouts => _layouts ??=
        DefDatabase<PrefabDef>.AllDefsListForReading
            .Where(d => d.defName.StartsWith(LayoutPrefix, StringComparison.Ordinal))
            .Select(d => new LayoutInfo
            {
                DefName = d.defName,
                Label = d.label ?? d.defName,
                Description = d.description ?? string.Empty,
                Width = d.size.x,
                Height = d.size.z
            })
            .ToList();

    public LayoutPlan GetLayoutPlan(string layoutDefName, int x, int z, int rotation)
    {
        PrefabDef prefab = DefDatabase<PrefabDef>.GetNamedSilentFail(layoutDefName);
        if (prefab == null || !prefab.defName.StartsWith(LayoutPrefix, StringComparison.Ordinal)) return null;

        var pos = new IntVec3(x, 0, z);
        var rot = new Rot4(rotation);
        var pieces = new List<LayoutPiece>();
        // 牆、門這類不能和地板共存的建築底下，地板藍圖放不上去，蓋好也看不到，所以不列。
        var noFloor = new HashSet<IntVec3>();
        // 要依附別的東西的（進料口要貼著營養膏機、壁掛燈要貼牆）排在後面，等它依附的東西先放好。
        foreach ((PrefabThingData data, IntVec3 cell, Rot4 thingRot) in PrefabUtility.GetThings(prefab, pos, rot)
                     .OrderBy(t => NeedsNeighbour(t.Item1.def) ? 1 : 0))
        {
            pieces.Add(new LayoutPiece
            {
                DefName = data.def.defName,
                Part = data.def.IsWall || data.def.IsDoor ? LayoutPart.Wall : LayoutPart.Furniture,
                StuffDefName = data.stuff?.defName ?? string.Empty,
                X = cell.x,
                Z = cell.z,
                Rotation = thingRot.AsInt
            });
            if (!data.def.CoexistsWithFloors) noFloor.UnionWith(GenAdj.OccupiedRect(cell, thingRot, data.def.size).Cells);
        }

        IntVec3 root = PrefabUtility.GetRoot(prefab, pos, rot);
        foreach ((PrefabTerrainData data, IntVec3 local) in prefab.GetTerrain())
        {
            IntVec3 cell = root + PrefabUtility.GetAdjustedLocalPosition(local, rot);
            if (noFloor.Contains(cell)) continue;
            pieces.Add(new LayoutPiece { DefName = data.def.defName, Part = LayoutPart.Floor, X = cell.x, Z = cell.z });
        }

        // 範圍和地板用同一套換算（GetRoot + GetAdjustedLocalPosition），偶數邊長旋轉時才不會差一格。
        CellRect rect = CellRect.FromLimits(root + PrefabUtility.GetAdjustedLocalPosition(IntVec3.Zero, rot),
            root + PrefabUtility.GetAdjustedLocalPosition(new IntVec3(prefab.size.x - 1, 0, prefab.size.z - 1), rot));
        return new LayoutPlan { Rect = new CellRectSpec(rect.minX, rect.minZ, rect.maxX, rect.maxZ), Pieces = pieces };
    }

    private static bool NeedsNeighbour(ThingDef def) =>
        def.building?.isAttachment == true || def.placeWorkers?.Contains(typeof(PlaceWorker_NextToHopperAccepter)) == true;
}
