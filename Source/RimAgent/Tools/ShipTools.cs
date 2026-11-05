using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 飛船藍圖的佈局檢查。原版（ShipUtility.ShipBuildingsAttachedTo）只在零件完工後才算連通，
/// 這裡把藍圖與框架當成已完工來算，讓模型在花掉材料之前就看得到斷開的地方。
/// </summary>
public sealed class ShipTools
{
    private const string ComputerCoreDefName = "Ship_ComputerCore";

    private readonly IGameWorld _world;

    public ShipTools(IGameWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    [AgentTool("check_ship_layout", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Checks the starship layout on this map, counting blueprints and frames as if finished: which parts connect (only edge-to-edge contact counts, corners do not), the gap and direction between disconnected groups, required parts still missing from the group with the computer core, and blocked casket entry cells. Use after placing ship parts and before spending materials.")]
    public string CheckShipLayout()
    {
        IReadOnlyList<ShipPartInfo> parts = _world.GetShipParts();
        IReadOnlyList<ShipPartRequirement> required = _world.GetRequiredShipParts();
        if (parts.Count == 0)
        {
            return "No ship parts on this map. Required: " + string.Join(", ", required.Select(r => $"{r.Count} {r.Label}")) + ".";
        }

        List<List<ShipPartInfo>> groups = GroupByContact(parts);
        var sb = new StringBuilder();
        sb.Append($"{parts.Count} ship part(s): {CountIn(parts, ShipPartState.Built)} built, {CountIn(parts, ShipPartState.Frame)} under construction, {CountIn(parts, ShipPartState.Blueprint)} blueprint(s). ")
            .AppendLine("Launch counts only finished parts.");
        AppendGroups(sb, groups);

        List<string> problems = [];
        problems.AddRange(groups.Skip(1).Select((g, i) => DescribeGap(i + 2, g, groups[0])));
        problems.AddRange(required
            .Select(r => (r.Label, Missing: r.Count - groups[0].Count(p => p.DefName == r.DefName)))
            .Where(m => m.Missing > 0)
            .Select(m => $"Missing from group 1: {m.Missing} {m.Label}."));
        problems.AddRange(parts.Select(p => DescribeBlockedEntry(p, parts)).Where(text => text != null));

        if (problems.Count == 0) return sb.Append("Layout OK: every required part is connected and casket entry cells are free.").ToString();
        sb.AppendLine("Problems:");
        foreach (string problem in problems) sb.Append("- ").AppendLine(problem);
        return sb.ToString().TrimEnd();
    }

    private static int CountIn(IEnumerable<ShipPartInfo> parts, ShipPartState state) => parts.Count(p => p.State == state);

    /// <summary>依接觸分組；第 1 組是有電腦核心的那組（從核心發射），沒有核心就是最大組。</summary>
    private static List<List<ShipPartInfo>> GroupByContact(IReadOnlyList<ShipPartInfo> parts)
    {
        var remaining = new List<ShipPartInfo>(parts);
        var groups = new List<List<ShipPartInfo>>();
        while (remaining.Count > 0)
        {
            var group = new List<ShipPartInfo> { remaining[0] };
            remaining.RemoveAt(0);
            for (int i = 0; i < group.Count; i++)
            {
                List<ShipPartInfo> touching = remaining.Where(p => Touches(p.Rect, group[i].Rect)).ToList();
                group.AddRange(touching);
                remaining.RemoveAll(touching.Contains);
            }
            groups.Add(group);
        }
        return groups
            .OrderByDescending(g => g.Any(p => p.DefName == ComputerCoreDefName))
            .ThenByDescending(g => g.Count)
            .ToList();
    }

    /// <summary>兩矩形之間隔幾格；負數表示該軸的範圍重疊。</summary>
    private static (int X, int Z) Gap(CellRectSpec a, CellRectSpec b)
    {
        return (Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX) - 1, Math.Max(a.MinZ - b.MaxZ, b.MinZ - a.MaxZ) - 1);
    }

    /// <summary>同 GenAdj.CellsAdjacentCardinal：一軸緊貼、另一軸有重疊才算相連，只碰到角不算。</summary>
    private static bool Touches(CellRectSpec a, CellRectSpec b)
    {
        (int x, int z) = Gap(a, b);
        return (x == 0 && z < 0) || (z == 0 && x < 0);
    }

    private static void AppendGroups(StringBuilder sb, List<List<ShipPartInfo>> groups)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            sb.Append($"Group {i + 1}: ");
            sb.AppendLine(string.Join("; ", groups[i].Select(p => $"{p.Label} #{p.Id} {p.Rect}{StateSuffix(p.State)}")));
        }
    }

    private static string StateSuffix(ShipPartState state)
    {
        return state switch
        {
            ShipPartState.Blueprint => " (blueprint)",
            ShipPartState.Frame => " (frame)",
            _ => string.Empty
        };
    }

    /// <summary>找這組離第 1 組最近的一對零件，說明差多少、往哪移就會貼上。</summary>
    private static string DescribeGap(int number, List<ShipPartInfo> group, List<ShipPartInfo> main)
    {
        var closest = group
            .SelectMany(p => main.Select(m => (Part: p, Main: m, Gap: Gap(p.Rect, m.Rect))))
            .OrderBy(t => Math.Max(t.Gap.X, 0) + Math.Max(t.Gap.Z, 0))
            .First();
        (ShipPartInfo part, ShipPartInfo target, (int gapX, int gapZ)) = closest;
        string start = $"Group {number} is not connected: {part.Label} {part.Rect} and {target.Label} {target.Rect}";
        string shift = DescribeShift(part.Rect, target.Rect, gapX, gapZ);
        string how = part.State == ShipPartState.Built ? "deconstruct and rebuild" : "remove_blueprint, then place it again";
        if (gapX == 0 && gapZ == 0) return $"{start} touch only at a corner, which does not connect. Moving the former {shift} would connect them ({how}).";
        string distance = gapX >= 0 && gapZ >= 0
            ? $"are diagonal, {gapX} cell(s) apart along x and {gapZ} along z"
            : $"have {Math.Max(gapX, gapZ)} empty cell(s) between them";
        // 樑是 2x6，一格寬的縫塞不進去。
        string bridge = Math.Max(gapX, gapZ) >= 2 ? ", or bridge the gap with a ship beam (2x6)" : string.Empty;
        return $"{start} {distance}. Moving the former {shift} would connect them ({how}){bridge}.";
    }

    private static string DescribeShift(CellRectSpec part, CellRectSpec target, int gapX, int gapZ)
    {
        // 兩軸都不重疊時，一軸緊貼、另一軸要多移一格才會有重疊的邊。
        int moveX = gapX >= 0 && gapZ >= 0 ? gapX + 1 : Math.Max(gapX, 0);
        int moveZ = Math.Max(gapZ, 0);
        var steps = new List<string>();
        if (moveX > 0) steps.Add($"{moveX} {(part.MaxX < target.MinX ? "east" : "west")}");
        if (moveZ > 0) steps.Add($"{moveZ} {(part.MaxZ < target.MinZ ? "north" : "south")}");
        return string.Join(" and ", steps);
    }

    private static string DescribeBlockedEntry(ShipPartInfo part, IReadOnlyList<ShipPartInfo> parts)
    {
        if (part.InteractionCell is not { } cell) return null;
        // 藍圖與框架不擋路，世界那邊看不出來，完工後才會擋住，所以要比對其他零件。休眠艙可穿過，不算阻擋。
        ShipPartInfo cover = parts.FirstOrDefault(p => p != part && p.DefName != "Ship_CryptosleepCasket" && p.Rect.Contains(cell.X, cell.Z));
        string blocker = cover != null ? $"{cover.Label} {cover.Rect}" : part.InteractionBlockedBy;
        return blocker == null ? null : $"{part.Label} #{part.Id}: colonists enter from {ToolText.Cell(cell.X, cell.Z)}, which is blocked by {blocker}. Without entry the casket cannot be boarded.";
    }
}
