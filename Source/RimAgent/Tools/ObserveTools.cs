using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>唯讀工具：讀取殖民地狀態。所有輸出都附 id 與座標，供後續指令引用。</summary>
public sealed class ObserveTools
{
    private const int MaxResearchListed = 30;
    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;

    public ObserveTools(IGameWorld world, ConfirmationGate gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
    }

    [AgentTool("get_colony_overview", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Colony summary: population, date and time, time speed, weather, outdoor temperature, wealth, hostiles on the map, colonists close to a mental break, power (networks, net watts, stored battery energy) and the game's active alerts (food, rescue, bedroom, power and so on). Start here for any general question and on every routine inspection.")]
    public string GetColonyOverview()
    {
        return FormatOverview(_world);
    }

    /// <summary>殖民地概況的文字；wait_for_time 等完也回這一份，模型不必再查一次。</summary>
    internal static string FormatOverview(IGameWorld world)
    {
        ColonyOverview o = world.GetOverview();
        string speed = TimeTools.SpeedLabel(world.GetTimeSpeed().Current, world.TimeSpeeds);
        PowerInfo power = world.GetPower();
        IReadOnlyList<AlertInfo> alerts = world.GetAlerts();
        return $"Colony: {o.ColonyName} | Colonists: {o.ColonistCount} | Date: year {o.Year}, {o.Season} day {o.DayOfSeason}, {o.Hour:00}:00" +
               $" | Time speed: {speed} | Weather: {o.Weather} | Outdoor temp: {ToolText.Num(o.OutdoorTemperatureC)}°C | Wealth: {ToolText.Num(o.Wealth)}" +
               $" | Hostiles on map: {o.HostileCount} | Colonists near mental break: {o.ColonistsNearMentalBreak}\n" +
               $"Power: {power.NetworkCount} network(s), net watts {ToolText.Num(power.NetWatts)}, stored battery energy {ToolText.Num(power.StoredEnergy)}\n" +
               (o.Factions.Count == 0 ? string.Empty : "Factions: " + string.Join("; ", o.Factions) + "\n") +
               "Alerts: " + (alerts.Count == 0 ? "none" : string.Join("; ", alerts.Select(a => "[" + a.Priority + "] " + a.Label)));
    }

    [AgentTool("list_colonists", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Lists every colonist: id, name, age, cell, health, mood, current activity, draft status, equipment and skills (* minor passion, ** major passion). Other tools take the id or name.")]
    public string ListColonists()
    {
        IReadOnlyList<ColonistInfo> colonists = _world.GetColonists();
        if (colonists.Count == 0) return "There are no colonists.";

        var sb = new StringBuilder();
        foreach (ColonistInfo c in colonists)
        {
            sb.Append(ToolText.Colonist(c)).Append(" (").Append(c.FullName).Append("), age ").Append(c.Age)
              .Append(", at ").Append(ToolText.Cell(c.X, c.Z))
              .Append(", health ").Append(ToolText.Pct(c.HealthPercent));
            if (c.Downed) sb.Append(" DOWNED");
            if (!string.IsNullOrEmpty(c.RoyalStatus)) sb.Append(", ").Append(c.RoyalStatus);
            sb.Append(", mood ").Append(ToolText.Pct(c.MoodPercent))
              .Append(" (break below ").Append(ToolText.Pct(c.MinorBreakThreshold)).Append(')');
            if (c.InMentalState) sb.Append(", MENTAL STATE: ").Append(c.MentalStateLabel);
            sb.Append(", doing: ").Append(string.IsNullOrEmpty(c.CurrentActivity) ? "nothing" : c.CurrentActivity)
              .Append(", drafted: ").Append(c.Drafted ? "yes" : "no");
            if (!string.IsNullOrEmpty(c.Equipment)) sb.Append(", equipped: ").Append(c.Equipment);
            sb.Append(ToolText.Unreadable(c)).AppendLine();
            sb.Append("  skills: ").Append(string.Join(", ", c.Skills.Select(ToolText.Skill))).AppendLine();
        }
        return sb.ToString().Trim();
    }

    [AgentTool("get_colonist_wellbeing", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Shows one colonist's needs, equipment and active mood thoughts. Reach for it when a colonist's mood, hunger, rest or recreation is low and you need the cause. Does not cover injuries or diseases; those are in get_health (medical tools).")]
    public string GetColonistWellbeing([Description("Colonist name, nickname or id.")] string colonist)
    {
        Result<ColonistInfo> pawn = TargetResolver.ResolveColonist(colonist, _world.GetColonists());
        if (!pawn.Success) return pawn.Error;
        ColonistInfo c = pawn.Value;
        string needs = c.Needs.Count == 0 ? "none available" : string.Join(", ", c.Needs.Select(n => n.Label + " " + ToolText.Pct(n.Percent)));
        string thoughts = c.Thoughts.Count == 0 ? "none reported" : string.Join("; ", c.Thoughts);
        return ToolText.Colonist(c) + " | needs: " + needs +
               " | equipment: " + (string.IsNullOrEmpty(c.Equipment) ? "none" : c.Equipment) +
               " | thoughts: " + thoughts + ToolText.Unreadable(c);
    }

    [AgentTool("get_resources", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Lists resources on the map grouped by category, plus total human-edible nutrition. Answers questions about food, materials, medicine and components. Only items lying in storage (stockpile zones, shelves) are counted, and not spoiled or fogged ones; loose items on the ground are not, find those with find_things kind=item.")]
    public string GetResources()
    {
        ResourceSnapshot snapshot = _world.GetResources();
        var sb = new StringBuilder();
        sb.Append("Total human-edible nutrition: ").Append(ToolText.Num(snapshot.TotalHumanEdibleNutrition))
          .Append(" (one colonist eats about 1.6 per day)").AppendLine();
        if (snapshot.Items.Count == 0)
        {
            sb.Append("Nothing is in storage yet: no stockpile or shelf holds any counted items. Loose items on the ground are not counted here; find them with find_things kind=item.");
            return sb.ToString();
        }

        foreach (IGrouping<string, ResourceInfo> group in snapshot.Items
                     .GroupBy(r => string.IsNullOrEmpty(r.Category) ? "Other" : r.Category)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(group.Key).Append(": ")
              .Append(string.Join(", ", group.OrderByDescending(r => r.Count).Select(r => $"{r.Label} ({r.DefName}) x{r.Count}")))
              .AppendLine();
        }
        return sb.ToString().Trim();
    }

    [AgentTool("list_recent_letters", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Lists the most recent in-game event letters. Explains what happened before the colony's state changed.")]
    public string ListRecentLetters()
    {
        IReadOnlyList<LetterInfo> letters = _world.GetRecentLetters();
        if (letters.Count == 0) return "No recent event letters.";
        return string.Join("\n", letters.Select(l => l.Label));
    }

    [AgentTool("list_threats", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Lists active threats on the map: hostile pawns, manhunting animals, predators and fires, each with id, faction and cell. order_attack takes these ids.")]
    public string ListThreats()
    {
        IReadOnlyList<ThreatInfo> threats = _world.GetThreats();
        if (threats.Count == 0) return "No threats on the map.";

        var sb = new StringBuilder();
        sb.Append(threats.Count).Append(" threat(s):").AppendLine();
        foreach (ThreatInfo t in threats)
        {
            sb.Append('[').Append(t.Id).Append("] ").Append(t.Label).Append(" (").Append(t.Kind);
            if (!string.IsNullOrEmpty(t.Faction)) sb.Append(", ").Append(t.Faction);
            sb.Append(") at ").Append(ToolText.Cell(t.X, t.Z));
            if (t.Downed) sb.Append(" downed");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    [AgentTool("get_research_status", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Shows the current research project with its progress and the projects available to start now.")]
    public string GetResearchStatus()
    {
        ResearchInfo r = _world.GetResearch();
        var sb = new StringBuilder();
        if (r.Current == null)
        {
            sb.Append("Current research: none.").AppendLine();
        }
        else
        {
            sb.Append("Current research: ").Append(r.Current.Label).Append(" (").Append(r.Current.DefName).Append(") ")
              .Append(ToolText.Pct(r.Current.ProgressPercent)).Append(" of ").Append(ToolText.Num(r.Current.Cost)).AppendLine();
        }

        if (r.Available.Count == 0)
        {
            sb.Append("Available to start: none.");
        }
        else
        {
            sb.Append("Available to start (").Append(r.Available.Count).Append("): ")
              .Append(string.Join(", ", r.Available.Take(MaxResearchListed).Select(p => $"{p.Label} ({p.DefName}, cost {ToolText.Num(p.Cost)})")));
            if (r.Available.Count > MaxResearchListed) sb.Append(", ...");
        }
        return sb.ToString().Trim();
    }

    [AgentTool("set_research", ToolCategory.Observe, ToolRisk.Safe)]
    [Description("Switches the active research project; progress on the previous project is kept. Reports the previous and new project.")]
    public string SetResearch(
        [Description("Research name or defName, e.g. \"Electricity\"; \"none\" stops research.")] string project)
    {
        string want = (project ?? string.Empty).Trim();
        ResearchInfo before = _world.GetResearch();
        string previous = before.Current == null ? "none" : $"{before.Current.Label} ({before.Current.DefName})";

        string defName = want;
        if (!IsStopWord(want))
        {
            Result<DefInfo> match = DefResolver.Resolve(want, _world.ResearchProjects, "research project");
            if (!match.Success) return (DefResolver.IsNoMatch(match) ? FinishedResearchError(want) : null) ?? match.Error;
            defName = match.Value.DefName;
        }

        string blocked = ToolText.Blocked(_gate, $"would switch research {previous} -> {(IsStopWord(want) ? "none" : defName)}.");
        if (blocked != null) return blocked;

        CommandResult result = _world.SetResearch(defName);
        if (!result.Success) return ToolText.Error(result.Message);

        // 世界層已保證冪等（重選目前專案直接成功），這裡讀最新狀態回報。
        ResearchInfo after = _world.GetResearch();
        string current = after.Current == null ? "none" : $"{after.Current.Label} ({after.Current.DefName})";
        string suffix = string.IsNullOrEmpty(result.Message) ? string.Empty : " " + result.Message;
        return $"Research {previous} -> {current}.{suffix}";
    }

    /// <summary>未完成目錄對不到時，若是已研究完的專案就明講，免得模型以為名稱寫錯而一直換著猜。</summary>
    private string FinishedResearchError(string want)
    {
        Result<DefInfo> finished = DefResolver.Resolve(want, _world.FinishedResearchProjects, "research project");
        if (!finished.Success) return null;
        return ToolText.Error($"research {finished.Value.Label} ({finished.Value.DefName}) is already finished. " +
            "Pick an unfinished project from get_research_status.");
    }

    [AgentTool("get_maps", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Lists every map in the save: id, name, tile, colonist count and which one is current. All other tools work on the current map; switch with set_current_map.")]
    public string GetMaps()
    {
        IReadOnlyList<MapInfo> maps = _world.GetMaps();
        if (maps.Count == 0) return "There are no maps.";

        var sb = new StringBuilder();
        foreach (MapInfo m in maps)
        {
            sb.Append('[').Append(m.Id).Append("] ").Append(m.Label)
              .Append(", tile ").Append(m.Tile)
              .Append(", colonists ").Append(m.ColonistCount);
            if (m.IsCurrent) sb.Append(" (current)");
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    [AgentTool("set_current_map", ToolCategory.Observe, ToolRisk.Safe)]
    [Description("Makes another map the current one for later tool calls, like the player switching maps. Pawns and items stay where they are.")]
    public string SetCurrentMap(
        [Description("Map id from get_maps, or part of its name.")] string map)
    {
        Result<MapInfo> match = ResolveMap(map, _world.GetMaps());
        if (!match.Success) return match.Error;

        string blocked = ToolText.Blocked(_gate, $"would switch current map to [{match.Value.Id}] {match.Value.Label}.");
        if (blocked != null) return blocked;

        CommandResult result = _world.SetCurrentMap(match.Value.Id);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message;
    }

    /// <summary>停下研發的關鍵字（大小寫不敏感）；世界層認得同樣的字。</summary>
    internal static bool IsStopWord(string input)
    {
        return input.Length == 0
            || string.Equals(input, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input, "stop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用 id 或名稱找地圖：整數先對 id，否則對名稱做精確→包含比對。</summary>
    internal static Result<MapInfo> ResolveMap(string input, IReadOnlyList<MapInfo> maps)
    {
        string needle = (input ?? string.Empty).Trim();
        if (needle.Length == 0) return Result<MapInfo>.Fail("Error: map name is empty.");
        if (int.TryParse(needle, out int id))
        {
            MapInfo byId = maps.FirstOrDefault(m => m.Id == id);
            if (byId != null) return Result<MapInfo>.Ok(byId);
            return Result<MapInfo>.Fail($"Error: no map has id {id}.");
        }
        MapInfo exact = maps.FirstOrDefault(m => string.Equals(m.Label, needle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return Result<MapInfo>.Ok(exact);
        List<MapInfo> contains = [.. maps.Where(m => m.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)];
        if (contains.Count == 1) return Result<MapInfo>.Ok(contains[0]);
        if (contains.Count > 1)
        {
            return Result<MapInfo>.Fail($"Error: \"{needle}\" matches several maps: " +
                string.Join(", ", contains.Select(m => $"[{m.Id}] {m.Label}")) + ". Use the map id.");
        }
        return Result<MapInfo>.Fail($"Error: no map matches \"{needle}\".");
    }

    [AgentTool("find_things", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Finds map objects by kind, optional name and optional rectangle. Returns id, defName, label, cell and existing marks; designate_area, place_blueprint and order_attack take these ids and cells. When there are more matches than the limit, results are ordered allowed (not forbidden) before forbidden, then nearest to the colonists first, and the reply says how many matched in total.")]
    public string FindThings(
        [Description("What to look for: any, mineable, plant, building, item or pawn.")] string kind,
        [Description("defName or part of the label to match, e.g. \"steel\" or \"tree\". Empty string matches everything.")] string name,
        [Description("Rectangle corner x; pass -1 for all four corners to search the whole map.")] int x1,
        [Description("Rectangle corner z.")] int z1,
        [Description("Opposite corner x.")] int x2,
        [Description("Opposite corner z.")] int z2,
        [Description("Maximum results, 1-200; 0 means 20.")] int limit)
    {
        if (!TryParseKind(kind, out ThingKind parsedKind))
        {
            return ToolText.Error($"unknown kind \"{kind}\". Use any, mineable, plant, building, item or pawn.");
        }

        var query = new ThingQuery
        {
            Kind = parsedKind,
            LabelContains = name ?? string.Empty,
            Limit = Math.Max(1, Math.Min(200, limit <= 0 ? 20 : limit))
        };

        if (x1 >= 0 && z1 >= 0 && x2 >= 0 && z2 >= 0)
        {
            Result<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
            if (!rect.Success) return rect.Error;
            query.Area = rect.Value;
        }

        ThingSearchResult things = _world.FindThings(query);
        if (things.Count == 0)
        {
            string where = query.Area.HasValue ? $" in {query.Area.Value}" : " on the map";
            string what = string.IsNullOrEmpty(query.LabelContains) ? kind : $"{kind} matching \"{query.LabelContains}\"";
            return $"No {what} found{where}.";
        }

        var sb = new StringBuilder();
        if (things.TotalMatches > things.Count)
        {
            sb.Append("Found ").Append(things.TotalMatches).Append(", showing the ").Append(things.Count)
              .Append(" nearest the colonists (allowed first, then forbidden). Narrow the name or rectangle to see the rest:").AppendLine();
        }
        else
        {
            sb.Append("Found ").Append(things.Count).Append(':').AppendLine();
        }
        foreach (ThingInfo t in things) sb.Append(ToolText.Thing(t)).AppendLine();
        return sb.ToString().Trim();
    }

    /// <summary>模型給的 kind 字串（大小寫不敏感）對到 <see cref="ThingKind"/>。</summary>
    internal static bool TryParseKind(string kind, out ThingKind parsed)
    {
        return Enum.TryParse((kind ?? string.Empty).Trim(), true, out parsed) && Enum.IsDefined(typeof(ThingKind), parsed);
    }

    /// <summary>inspect_area 一次最多看的格數（50x50）；再大就是在掃全圖，該改用 find_things。</summary>
    internal const int MaxInspectedCells = 2500;
    private const int MaxObstaclesListed = 10;
    private const float FertileThreshold = 0.7f;

    /// <summary>單格明細：inspect_area 兩個角落同一格時用。</summary>
    private string DescribeCell(int x, int z)
    {
        string outside = TargetResolver.CheckCell(x, z, _world.GetMapBounds());
        if (outside != null) return outside;

        CellInfo c = _world.GetCell(x, z);
        var sb = new StringBuilder();
        sb.Append("Cell ").Append(ToolText.Cell(c.X, c.Z)).Append(": ").Append(c.Terrain).Append(" (").Append(c.TerrainDefName).Append(')')
          .Append(", fertility ").Append(ToolText.Pct(c.Fertility))
          .Append(", roof: ").Append(string.IsNullOrEmpty(c.Roof) ? "none" : c.Roof)
          .Append(c.Walkable ? ", walkable" : ", NOT walkable");
        if (c.Fogged) sb.Append(", unexplored (fogged)");
        sb.AppendLine();
        sb.Append("Zone: ").Append(c.Zone == null ? "none" : $"[{c.Zone.Id}] {c.Zone.Label} ({c.Zone.Kind}, {c.Zone.CellCount} cells)").AppendLine();
        sb.Append("Room: ").Append(c.Room == null ? "none (cell is filled by a wall or rock)" : Room(c.Room)).AppendLine();
        sb.Append("Objects on cell: ");
        if (c.Things.Count == 0) sb.Append("none");
        else sb.Append(string.Join("; ", c.Things.Select(ToolText.Thing)));
        return sb.ToString().Trim();
    }

    private static string Room(RoomInfo r)
    {
        return $"#{r.Id} {r.Role}, {r.CellCount} cells, extents {r.Extents}, {ToolText.Num(r.TemperatureC)}°C, " +
               (r.Outdoors ? "outdoors" : "indoors");
    }

    [AgentTool("inspect_area", ToolCategory.Observe, ToolRisk.Query)]
    [Description("Inspects the map before placing blueprints or a zone. For one cell (both corners the same) it describes terrain and fertility, roof, walkability, its zone, the room around it (id, role, size, extents, temperature, indoors or outdoors) and the objects on it. For a rectangle of up to 50 cells per side (2500 cells; corners are inclusive, so (0,0)-(49,49) is the largest square) it summarises the terrain, fertile and roofed cell counts, cells that are not walkable, obstacles (buildings, rock, trees, items), and the zones and rooms overlapping it.")]
    public string InspectArea(
        [Description("Rectangle corner x.")] int x1,
        [Description("Rectangle corner z.")] int z1,
        [Description("Opposite corner x.")] int x2,
        [Description("Opposite corner z.")] int z2)
    {
        if (x1 == x2 && z1 == z2) return DescribeCell(x1, z1);
        Result<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
        if (!rect.Success) return rect.Error;
        if (rect.Value.CellCount > MaxInspectedCells)
        {
            return ToolText.Error($"rectangle {rect.Value} has {rect.Value.CellCount} cells; the limit is {MaxInspectedCells} cells: {MapGridTools.RectLimitHint}. Inspect a smaller area or use find_things.");
        }

        IReadOnlyList<CellInfo> cells = _world.GetCells(rect.Value);
        var sb = new StringBuilder();
        sb.Append("Area ").Append(rect.Value).Append(": ").Append(cells.Count).Append(" cells");
        sb.Append(" | terrain: ").Append(string.Join(", ", cells
            .GroupBy(c => c.Terrain)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}")));
        sb.Append(" | fertile (>=").Append(ToolText.Pct(FertileThreshold)).Append("): ").Append(cells.Count(c => c.Fertility >= FertileThreshold));
        int roofed = cells.Count(c => !string.IsNullOrEmpty(c.Roof));
        sb.Append(" | roofed: ").Append(roofed);
        int mountain = cells.Count(c => c.Roof == "overhead mountain");
        if (mountain > 0) sb.Append(" (overhead mountain ").Append(mountain).Append(')');
        sb.Append(" | not walkable: ").Append(cells.Count(c => !c.Walkable));
        int fogged = cells.Count(c => c.Fogged);
        if (fogged > 0) sb.Append(" | unexplored: ").Append(fogged);
        sb.AppendLine();

        // 擋路的東西：建築、岩石、植物、物品；殖民者與動物會走開，不算障礙。
        List<ThingInfo> obstacles = [.. cells.SelectMany(c => c.Things)
            .Where(t => t.Kind != ThingKind.Pawn)
            .GroupBy(t => t.Id).Select(g => g.First())];
        sb.Append("Obstacles: ");
        if (obstacles.Count == 0)
        {
            sb.Append("none");
        }
        else
        {
            sb.Append(obstacles.Count).Append(" — ")
              .Append(string.Join("; ", obstacles.Take(MaxObstaclesListed).Select(ToolText.Thing)));
            if (obstacles.Count > MaxObstaclesListed) sb.Append("; and ").Append(obstacles.Count - MaxObstaclesListed).Append(" more");
        }
        sb.AppendLine();

        sb.Append("Zones: ");
        List<IGrouping<int, CellInfo>> zones = [.. cells.Where(c => c.Zone != null).GroupBy(c => c.Zone.Id)];
        sb.Append(zones.Count == 0
            ? "none"
            : string.Join("; ", zones.Select(g => $"[{g.Key}] {g.First().Zone.Label} ({g.First().Zone.Kind}, {g.Count()} cells inside)")));
        sb.AppendLine();

        sb.Append("Rooms: ");
        List<IGrouping<int, CellInfo>> rooms = [.. cells.Where(c => c.Room != null).GroupBy(c => c.Room.Id)];
        sb.Append(rooms.Count == 0
            ? "none"
            : string.Join("; ", rooms.Select(g => Room(g.First().Room) + $" ({g.Count()} cells inside)")));
        return sb.ToString().Trim();
    }

    [AgentTool("take_screenshot", ToolCategory.Observe, ToolRisk.Safe)]
    [Description("Saves a screenshot of the current game view to disk for the player and returns the file path. The image is for the player; you cannot view it.")]
    public string TakeScreenshot(
        [Description("File name without directory or .png; empty uses a timestamped name.")] string name = null)
    {
        string safeName = ResolveScreenshotName(name);
        string blocked = ToolText.Blocked(_gate, $"would take screenshot to disk as \"{safeName}.png\".");
        if (blocked != null) return blocked;

        ScreenshotInfo info = _world.TakeScreenshot(safeName);
        if (!info.Success)
        {
            return ToolText.Error(string.IsNullOrEmpty(info.Error) ? "failed to take screenshot." : info.Error);
        }
        return $"Screenshot saved: {info.FilePath} ({info.Width}x{info.Height})";
    }

    internal static string ResolveScreenshotName(string input)
    {
        string name = (input ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            return "RimAgent_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }

        int lastSep = name.LastIndexOfAny(['/', '\\']);
        if (lastSep >= 0)
        {
            name = name.Substring(lastSep + 1);
        }

        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4);
        }

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Trim();
        return name.Replace("_", string.Empty).Length == 0
            ? "RimAgent_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
            : name;
    }
}
