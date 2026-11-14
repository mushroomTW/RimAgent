using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimAgent.Game;

/// <summary>
/// 讓 agent 掌握全圖的兩個資料來源：離屏俯視截圖（take_screenshot 給範圍時，渲染見 <see cref="MapShotCapture"/>）
/// 與全圖勘查（get_map_overview）。
/// </summary>
internal sealed partial class RimWorldGameWorld
{
    /// <summary>天然地面算「肥沃土壤」的肥沃度下限（原版肥沃土壤 140%、一般土壤 100%）。</summary>
    private const float RichSoilFertility = 1.2f;

    /// <summary>天然地面算「可耕地」的肥沃度下限（原版礫石、多石土壤 70%，沙地遠低於此）。</summary>
    private const float SoilFertility = 0.7f;

    public ScreenshotInfo RenderMapArea(CellRectSpec rect, string filename = null)
    {
        try
        {
            if (!WorldRendererUtility.DrawingMap)
            {
                return ScreenshotFailure("the world map is open, so no colony map is being drawn; map screenshots work only while a map is shown.");
            }
            MapShotLayout layout = MapShotLayout.For(rect);
            string fullPath = ReserveScreenshotPath(filename);
            MapShotCapture.Begin(Map, layout, fullPath);
            return new ScreenshotInfo
            {
                Success = true,
                FilePath = fullPath,
                Width = layout.ImageWidth,
                Height = layout.ImageHeight,
                Layout = layout
            };
        }
        catch (Exception ex)
        {
            Log.Error("RimAgent: RenderMapArea failed: " + ex);
            return ScreenshotFailure(ex.Message);
        }
    }

    /// <summary>在遊戲截圖資料夾找一個不重複的檔名並記下資料夾（供 <see cref="ReadScreenshotFile"/> 驗證路徑）。</summary>
    private string ReserveScreenshotPath(string filename)
    {
        string baseName = Tools.ObserveTools.ResolveScreenshotName(filename);
        string folder = GenFilePaths.ScreenshotFolderPath;
        _screenshotFolder = folder;
        string fullPath = Path.Combine(folder, baseName + ".png");
        int counter = 1;
        while (File.Exists(fullPath))
        {
            fullPath = Path.Combine(folder, $"{baseName}_{counter++}.png");
        }
        return fullPath;
    }

    private static ScreenshotInfo ScreenshotFailure(string error) => new() { Success = false, Error = error };

    public MapSurvey GetMapSurvey()
    {
        Map map = Map;
        int width = map.Size.x;
        int height = map.Size.z;
        int count = width * height;
        var cells = new SurveyCell[count];
        var resource = new short[count];
        var thickRoof = new bool[count];
        var resourceIndex = new Dictionary<ThingDef, short>();
        var resourceLabels = new List<string>();

        for (int i = 0; i < count; i++)
        {
            if (map.fogGrid.IsFogged(i)) continue;
            thickRoof[i] = map.roofGrid.RoofAt(i)?.isThickRoof == true;
            Building edifice = map.edificeGrid[i];
            BuildingProperties building = edifice?.def.building;
            if (building != null && building.isNaturalRock)
            {
                cells[i] = SurveyCell.Rock;
                if (building.isResourceRock)
                {
                    if (!resourceIndex.TryGetValue(edifice.def, out short index))
                    {
                        resourceLabels.Add(edifice.def.label);
                        index = (short)resourceLabels.Count;
                        resourceIndex[edifice.def] = index;
                    }
                    resource[i] = index;
                }
                continue;
            }
            if (edifice != null)
            {
                cells[i] = SurveyCell.Structure;
                continue;
            }
            cells[i] = ClassifyTerrain(map.terrainGrid.TerrainAt(i));
        }

        var rooms = new Dictionary<int, RoomInfo>();
        List<RoomInfo> indoor = map.regionGrid.AllRooms
            .Where(r => !r.PsychologicallyOutdoors && !r.TouchesMapEdge && r.CellCount > 0)
            .Select(r => ToRoom(r, rooms))
            .ToList();

        Area_Home home = map.areaManager.Home;
        CellRectSpec? homeRect = null;
        int homeCells = home?.TrueCount ?? 0;
        if (homeCells > 0) homeRect = Extents(home.ActiveCells);

        return new MapSurvey
        {
            Width = width,
            Height = height,
            Biome = map.Biome?.LabelCap.ToString() ?? string.Empty,
            Cells = cells,
            Resource = resource,
            ResourceLabels = resourceLabels,
            ThickRoof = thickRoof,
            Rooms = indoor,
            Zones = map.zoneManager.AllZones.Select(ToZoneWithExtents).ToList(),
            HomeArea = homeRect,
            HomeAreaCells = homeCells,
            Colonists = map.mapPawns.FreeColonistsSpawned.Select(p => (p.Position.x, p.Position.z)).ToList(),
            Hostiles = map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Downed && p.HostileTo(Faction.OfPlayer) && !p.Position.Fogged(map))
                .Select(p => (p.Position.x, p.Position.z))
                .ToList()
        };
    }

    /// <summary>
    /// 沒有建築的格子依地形分類：水看 Water 標籤與可否通行；人造地板要同時不是天然、且是鋪在上層的地板或地基
    /// （模組的天然地形忘了標 natural 時才不會被當成地板）；其餘看肥沃度。
    /// </summary>
    private static SurveyCell ClassifyTerrain(TerrainDef terrain)
    {
        if (terrain == null) return SurveyCell.Barren;
        if (terrain.IsWater) return terrain.passability == Traversability.Impassable ? SurveyCell.DeepWater : SurveyCell.ShallowWater;
        if (!terrain.natural && (terrain.layerable || terrain.isFoundation)) return SurveyCell.BuiltFloor;
        if (terrain.fertility >= RichSoilFertility) return SurveyCell.RichSoil;
        return terrain.fertility >= SoilFertility ? SurveyCell.Soil : SurveyCell.Barren;
    }

    private static CellRectSpec Extents(IEnumerable<IntVec3> cells)
    {
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        foreach (IntVec3 c in cells)
        {
            if (c.x < minX) minX = c.x;
            if (c.z < minZ) minZ = c.z;
            if (c.x > maxX) maxX = c.x;
            if (c.z > maxZ) maxZ = c.z;
        }
        return minX == int.MaxValue ? new CellRectSpec(0, 0, 0, 0) : new CellRectSpec(minX, minZ, maxX, maxZ);
    }
}
