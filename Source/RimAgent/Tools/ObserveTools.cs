using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>唯讀工具：讀取殖民地狀態。所有輸出都附 id 與座標，供後續指令引用。</summary>
    public sealed class ObserveTools
    {
        private const int MaxResearchListed = 30;
        private readonly IGameWorld _world;

        public ObserveTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("get_colony_overview", ToolCategory.Observe)]
        [Description("One-line summary of the colony: population, date and time, weather, outdoor temperature, wealth, hostiles on the map and colonists close to a mental break. Call this first when asked about the general situation.")]
        public string GetColonyOverview()
        {
            ColonyOverview o = _world.GetOverview();
            return $"Colony: {o.ColonyName} | Colonists: {o.ColonistCount} | Date: year {o.Year}, {o.Season} day {o.DayOfSeason}, {o.Hour:00}:00" +
                   $" | Weather: {o.Weather} | Outdoor temp: {ToolText.Num(o.OutdoorTemperatureC)}°C | Wealth: {ToolText.Num(o.Wealth)}" +
                   $" | Hostiles on map: {o.HostileCount} | Colonists near mental break: {o.ColonistsNearMentalBreak}";
        }

        [AgentTool("list_colonists", ToolCategory.Observe)]
        [Description("Lists every colonist with id, name, age, position, health, mood, current activity, draft status and skills (* = minor passion, ** = major passion). Use the id or name in other tools.")]
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
                sb.Append(", mood ").Append(ToolText.Pct(c.MoodPercent))
                  .Append(" (break below ").Append(ToolText.Pct(c.MinorBreakThreshold)).Append(')');
                if (c.InMentalState) sb.Append(", MENTAL STATE: ").Append(c.MentalStateLabel);
                sb.Append(", doing: ").Append(string.IsNullOrEmpty(c.CurrentActivity) ? "nothing" : c.CurrentActivity)
                  .Append(", drafted: ").Append(c.Drafted ? "yes" : "no");
                sb.AppendLine();
                sb.Append("  skills: ").Append(string.Join(", ", c.Skills.Select(ToolText.Skill))).AppendLine();
            }
            return sb.ToString().Trim();
        }

        [AgentTool("get_resources", ToolCategory.Observe)]
        [Description("Lists stockpiled resources (counted items on the map) grouped by category, plus total human-edible nutrition. Useful for questions about food, materials, medicine and components.")]
        public string GetResources()
        {
            ResourceSnapshot snapshot = _world.GetResources();
            var sb = new StringBuilder();
            sb.Append("Total human-edible nutrition: ").Append(ToolText.Num(snapshot.TotalHumanEdibleNutrition))
              .Append(" (one colonist eats about 1.6 per day)").AppendLine();
            if (snapshot.Items.Count == 0)
            {
                sb.Append("No counted resources.");
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

        [AgentTool("list_threats", ToolCategory.Observe)]
        [Description("Lists active threats on the map: hostile pawns, manhunting animals, predators and fires, each with id, faction and position.")]
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

        [AgentTool("get_research_status", ToolCategory.Observe)]
        [Description("Shows the current research project with its progress, and the projects that can be started now.")]
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

        [AgentTool("find_things", ToolCategory.Observe)]
        [Description("Finds map objects by kind and optional name, optionally inside a rectangle. Returns id, defName, label, position and existing designations. Use the returned ids and positions in build and combat tools.")]
        public string FindThings(
            [Description("What to look for: any, mineable, plant, building, item or pawn.")] string kind,
            [Description("defName or part of the label to match, e.g. \"steel\" or \"tree\". Empty string matches everything.")] string name,
            [Description("Rectangle corner x. Pass -1 for all four coordinates to search the whole map.")] int x1,
            [Description("Rectangle corner z.")] int z1,
            [Description("Opposite corner x.")] int x2,
            [Description("Opposite corner z.")] int z2,
            [Description("Maximum number of results, 1-200. Use 20 unless you need more.")] int limit)
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
                ResolveResult<CellRectSpec> rect = TargetResolver.ResolveRect(x1, z1, x2, z2, _world.GetMapBounds());
                if (!rect.Success) return rect.Error;
                query.Area = rect.Value;
            }

            IReadOnlyList<ThingInfo> things = _world.FindThings(query);
            if (things.Count == 0)
            {
                string where = query.Area.HasValue ? $" in {query.Area.Value}" : " on the map";
                string what = string.IsNullOrEmpty(query.LabelContains) ? kind : $"{kind} matching \"{query.LabelContains}\"";
                return $"No {what} found{where}.";
            }

            var sb = new StringBuilder();
            sb.Append("Found ").Append(things.Count).Append(things.Count >= query.Limit ? " (limit reached)" : string.Empty).Append(':').AppendLine();
            foreach (ThingInfo t in things) sb.Append(ToolText.Thing(t)).AppendLine();
            return sb.ToString().Trim();
        }

        /// <summary>模型給的 kind 字串（大小寫不敏感）對到 <see cref="ThingKind"/>。</summary>
        internal static bool TryParseKind(string kind, out ThingKind parsed)
        {
            return Enum.TryParse((kind ?? string.Empty).Trim(), true, out parsed) && Enum.IsDefined(typeof(ThingKind), parsed);
        }
    }
}
