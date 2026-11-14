using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 全圖語意摘要：把勘查資料整理成地貌比例、基地、房間、區域、礦脈、水域、肥沃土壤與岩體，
/// 每項附範圍與方位，讓模型一次掌握全圖，再用截圖或 inspect_area 往下看。
/// 分群與排版只用 <see cref="MapSurvey"/>，不碰遊戲，可單元測試。
/// </summary>
public sealed class MapOverviewTools
{
    private const int MaxRooms = 30;
    /// <summary>小於此格數的室內空間（廢墟殘牆圍出的一兩格）只計數不列出，免得淹沒真正的房間。</summary>
    private const int MinRoomCells = 4;
    private const int MaxZones = 20;
    private const int MaxOreVeins = 15;
    private const int MaxTerrainPatches = 6;
    private const int MinWaterCells = 20;
    private const int MinRichSoilCells = 20;
    private const int MinRockMassCells = 200;

    private readonly IGameWorld _world;

    public MapOverviewTools(IGameWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <summary>一個相連的格子群（四方向相連）。</summary>
    internal sealed class Cluster
    {
        public int Key;
        public int Cells;
        public int MinX = int.MaxValue, MinZ = int.MaxValue, MaxX = int.MinValue, MaxZ = int.MinValue;
        public long SumX, SumZ;

        public CellRectSpec Extents => new(MinX, MinZ, MaxX, MaxZ);
        public int CenterX => (int)(SumX / Cells);
        public int CenterZ => (int)(SumZ / Cells);
    }

    [AgentTool("get_map_overview", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Summarises the whole current map in one call: size and biome, how much is explored, the share of rock, soil, rich soil, water and built-up ground, the home area and where colonists and visible hostiles are, every indoor room (role, extents, size, temperature), zones, ore veins, water bodies, rich soil patches and large rock masses, each with its extents and compass direction (north is high z). Only explored cells count; fog is not revealed. Use it first to orient yourself, then take_screenshot with a rectangle to look at an area, and inspect_area or find_things for exact details.")]
    public string GetMapOverview()
    {
        return Format(_world.GetMapSurvey());
    }

    internal static string Format(MapSurvey survey)
    {
        int width = survey.Width;
        int height = survey.Height;
        int total = width * height;
        var counts = new int[Enum.GetValues(typeof(SurveyCell)).Length];
        int thickRoof = 0;
        for (int i = 0; i < total; i++)
        {
            counts[(int)survey.Cells[i]]++;
            if (survey.ThickRoof[i]) thickRoof++;
        }
        int revealed = total - counts[(int)SurveyCell.Unknown];

        var sb = new StringBuilder();
        sb.Append("Map ").Append(width).Append('x').Append(height);
        if (!string.IsNullOrEmpty(survey.Biome)) sb.Append(", ").Append(survey.Biome);
        sb.Append(". Explored ").Append(Percent(revealed, total)).Append(" of the map; the rest is unexplored fog.").AppendLine();
        sb.Append("Directions: x runs west to east, z south to north; \"north-west\" etc. means the third of the map the area's centre lies in.").AppendLine();

        if (revealed > 0)
        {
            var parts = new List<string>();
            AddShare(parts, "natural rock", counts[(int)SurveyCell.Rock], revealed);
            AddShare(parts, "soil", counts[(int)SurveyCell.Soil], revealed);
            AddShare(parts, "rich soil", counts[(int)SurveyCell.RichSoil], revealed);
            AddShare(parts, "barren ground (sand, ice, bare stone)", counts[(int)SurveyCell.Barren], revealed);
            AddShare(parts, "shallow water", counts[(int)SurveyCell.ShallowWater], revealed);
            AddShare(parts, "deep water", counts[(int)SurveyCell.DeepWater], revealed);
            AddShare(parts, "built floor", counts[(int)SurveyCell.BuiltFloor], revealed);
            AddShare(parts, "buildings", counts[(int)SurveyCell.Structure], revealed);
            sb.Append("Explored ground: ").Append(string.Join(", ", parts)).Append('.');
            if (thickRoof > 0) sb.Append(" Thick mountain roof covers ").Append(Percent(thickRoof, revealed)).Append(" of it.");
            sb.AppendLine();
        }

        sb.AppendLine();
        AppendBase(sb, survey);
        AppendRooms(sb, survey);
        AppendZones(sb, survey);

        List<Cluster> ores = FindClusters(survey, i => survey.Resource[i]);
        AppendClusters(sb, survey, "Ore veins", ores, MaxOreVeins, c => survey.ResourceLabels[c.Key - 1]);

        List<Cluster> water = FindClusters(survey, i => IsWater(survey.Cells[i]) ? 1 : 0).Where(c => c.Cells >= MinWaterCells).ToList();
        AppendClusters(sb, survey, $"Water bodies ({MinWaterCells}+ cells)", water, MaxTerrainPatches, c => "water");

        List<Cluster> rich = FindClusters(survey, i => survey.Cells[i] == SurveyCell.RichSoil ? 1 : 0).Where(c => c.Cells >= MinRichSoilCells).ToList();
        AppendClusters(sb, survey, $"Rich soil patches ({MinRichSoilCells}+ cells)", rich, MaxTerrainPatches, c => "rich soil");

        List<Cluster> rock = FindClusters(survey, i => survey.Cells[i] == SurveyCell.Rock ? 1 : 0).Where(c => c.Cells >= MinRockMassCells).ToList();
        AppendClusters(sb, survey, $"Large rock masses ({MinRockMassCells}+ explored cells)", rock, MaxTerrainPatches, c => "rock");

        sb.Append("Look closer with take_screenshot and a rectangle; use inspect_area and find_things for exact cells and ids.");
        return sb.ToString();
    }

    private static void AppendBase(StringBuilder sb, MapSurvey survey)
    {
        if (survey.HomeArea is CellRectSpec home)
        {
            sb.Append("Home area: ").Append(home).Append(", ").Append(CellCount(survey.HomeAreaCells)).Append(", ")
              .Append(Direction(survey, (home.MinX + home.MaxX) / 2, (home.MinZ + home.MaxZ) / 2)).Append('.').AppendLine();
        }
        else
        {
            sb.Append("Home area: none.").AppendLine();
        }
        AppendPeople(sb, survey, "Colonists", survey.Colonists);
        AppendPeople(sb, survey, "Visible hostiles", survey.Hostiles);
        sb.AppendLine();
    }

    private static void AppendPeople(StringBuilder sb, MapSurvey survey, string title, IReadOnlyList<(int X, int Z)> people)
    {
        sb.Append(title).Append(": ");
        if (people.Count == 0)
        {
            sb.Append("none.").AppendLine();
            return;
        }
        int minX = people.Min(p => p.X), maxX = people.Max(p => p.X);
        int minZ = people.Min(p => p.Z), maxZ = people.Max(p => p.Z);
        int cx = (int)people.Average(p => p.X), cz = (int)people.Average(p => p.Z);
        sb.Append(people.Count).Append(" within ").Append(new CellRectSpec(minX, minZ, maxX, maxZ))
          .Append(", centred near (").Append(cx).Append(',').Append(cz).Append(") ").Append(Direction(survey, cx, cz)).Append('.').AppendLine();
    }

    private static void AppendRooms(StringBuilder sb, MapSurvey survey)
    {
        List<RoomInfo> rooms = survey.Rooms.Where(r => r.CellCount >= MinRoomCells).ToList();
        int tiny = survey.Rooms.Count - rooms.Count;
        sb.Append("Indoor rooms (").Append(rooms.Count);
        if (tiny > 0) sb.Append("; ").Append(tiny).Append(" enclosed spaces under ").Append(MinRoomCells).Append(" cells left out");
        sb.Append(')');
        if (rooms.Count == 0)
        {
            sb.Append(": none.").AppendLine().AppendLine();
            return;
        }
        sb.Append(':').AppendLine();
        foreach (RoomInfo room in rooms.OrderBy(r => RoleOf(r), StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).Take(MaxRooms))
        {
            CellRectSpec e = room.Extents;
            sb.Append("- ").Append(RoleOf(room)).Append(" #").Append(room.Id).Append(": ").Append(e)
              .Append(", ").Append(CellCount(room.CellCount)).Append(", ").Append(room.TemperatureC.ToString("0")).Append("°C").AppendLine();
        }
        if (rooms.Count > MaxRooms) sb.Append("- ...and ").Append(rooms.Count - MaxRooms).Append(" more.").AppendLine();
        sb.AppendLine();
    }

    private static string RoleOf(RoomInfo room) => string.IsNullOrEmpty(room.Role) ? "room" : room.Role;

    private static void AppendZones(StringBuilder sb, MapSurvey survey)
    {
        sb.Append("Zones (").Append(survey.Zones.Count).Append(')');
        if (survey.Zones.Count == 0)
        {
            sb.Append(": none.").AppendLine().AppendLine();
            return;
        }
        sb.Append(':').AppendLine();
        foreach (ZoneInfo zone in survey.Zones.Take(MaxZones))
        {
            sb.Append("- ").Append(zone.Kind).Append(" \"").Append(zone.Label).Append("\" #").Append(zone.Id).Append(": ")
              .Append(zone.Extents).Append(", ").Append(CellCount(zone.CellCount)).AppendLine();
        }
        if (survey.Zones.Count > MaxZones) sb.Append("- ...and ").Append(survey.Zones.Count - MaxZones).Append(" more.").AppendLine();
        sb.AppendLine();
    }

    private static void AppendClusters(StringBuilder sb, MapSurvey survey, string title, List<Cluster> clusters, int max, Func<Cluster, string> label)
    {
        sb.Append(title).Append(" (").Append(clusters.Count).Append(')');
        if (clusters.Count == 0)
        {
            sb.Append(": none explored.").AppendLine().AppendLine();
            return;
        }
        sb.Append(':').AppendLine();
        foreach (Cluster c in clusters.OrderByDescending(c => c.Cells).Take(max))
        {
            sb.Append("- ").Append(label(c)).Append(": ").Append(CellCount(c.Cells)).Append(", ").Append(c.Extents)
              .Append(", ").Append(Direction(survey, c.CenterX, c.CenterZ)).AppendLine();
        }
        if (clusters.Count > max) sb.Append("- ...and ").Append(clusters.Count - max).Append(" smaller.").AppendLine();
        sb.AppendLine();
    }

    /// <summary>四方向洪水填充，把 key 相同（且非 0）的相連格子分成一群。key 先整張算好，填充時只查陣列。</summary>
    internal static List<Cluster> FindClusters(MapSurvey survey, Func<int, int> keyOf)
    {
        int width = survey.Width;
        int total = width * survey.Height;
        var keys = new int[total];
        for (int i = 0; i < total; i++) keys[i] = keyOf(i);

        var visited = new bool[total];
        var clusters = new List<Cluster>();
        var stack = new Stack<int>();
        for (int start = 0; start < total; start++)
        {
            int key = keys[start];
            if (key == 0 || visited[start]) continue;
            var cluster = new Cluster { Key = key };
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % width, z = i / width;
                cluster.Cells++;
                cluster.SumX += x;
                cluster.SumZ += z;
                if (x < cluster.MinX) cluster.MinX = x;
                if (x > cluster.MaxX) cluster.MaxX = x;
                if (z < cluster.MinZ) cluster.MinZ = z;
                if (z > cluster.MaxZ) cluster.MaxZ = z;
                if (x > 0) TryPush(i - 1, key, keys, visited, stack);
                if (x < width - 1) TryPush(i + 1, key, keys, visited, stack);
                if (z > 0) TryPush(i - width, key, keys, visited, stack);
                if (i + width < total) TryPush(i + width, key, keys, visited, stack);
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    private static void TryPush(int n, int key, int[] keys, bool[] visited, Stack<int> stack)
    {
        if (visited[n] || keys[n] != key) return;
        visited[n] = true;
        stack.Push(n);
    }

    private static bool IsWater(SurveyCell cell) => cell == SurveyCell.ShallowWater || cell == SurveyCell.DeepWater;

    /// <summary>把地圖切成 3x3，回傳中心點所在的方位（north-west、centre 等）。</summary>
    internal static string Direction(MapSurvey survey, int x, int z)
    {
        int col = Math.Min(2, Math.Max(0, x * 3 / Math.Max(1, survey.Width)));
        int row = Math.Min(2, Math.Max(0, z * 3 / Math.Max(1, survey.Height)));
        string ns = row == 2 ? "north" : row == 0 ? "south" : string.Empty;
        string ew = col == 2 ? "east" : col == 0 ? "west" : string.Empty;
        if (ns.Length == 0 && ew.Length == 0) return "centre";
        if (ns.Length == 0) return ew;
        return ew.Length == 0 ? ns : ns + "-" + ew;
    }

    private static string CellCount(int n) => n == 1 ? "1 cell" : n + " cells";

    private static void AddShare(List<string> parts, string label, int count, int total)
    {
        if (count > 0) parts.Add(label + " " + Percent(count, total));
    }

    private static string Percent(int part, int total)
    {
        if (total <= 0) return "0%";
        double p = 100.0 * part / total;
        return p > 0 && p < 1 ? "<1%" : Math.Round(p).ToString("0") + "%";
    }
}
