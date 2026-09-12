using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Tests
{
    /// <summary>
    /// 記憶體內的假遊戲世界。命令會真的改狀態（後續查詢看得到），並把每次呼叫記到 Calls 供斷言。
    /// </summary>
    internal sealed class FakeGameWorld : IGameWorld
    {
        public List<ColonistInfo> Colonists { get; } = new List<ColonistInfo>();
        public List<ThingInfo> Things { get; } = new List<ThingInfo>();
        public List<ThingInfo> Selection { get; } = new List<ThingInfo>();
        public List<ThreatInfo> Threats { get; } = new List<ThreatInfo>();
        public ColonyOverview Overview { get; set; } = new ColonyOverview { ColonyName = "New Arrivals", ColonistCount = 2, Year = 5500, Season = "Spring", DayOfSeason = 3, Hour = 14, Weather = "Clear", OutdoorTemperatureC = 21.4f, Wealth = 12000f };
        public ResourceSnapshot Resources { get; set; } = new ResourceSnapshot();
        public ResearchInfo Research { get; set; } = new ResearchInfo();
        public MapBounds Bounds { get; set; } = new MapBounds { Width = 100, Height = 100 };
        public List<string> Calls { get; } = new List<string>();

        /// <summary>藍圖放不下的格子與原因。</summary>
        public Dictionary<(int X, int Z), string> BlockedCells { get; } = new Dictionary<(int, int), string>();
        public HashSet<(int X, int Z)> UnwalkableCells { get; } = new HashSet<(int, int)>();
        public List<(string Def, int X, int Z, int Rotation, string Stuff)> Blueprints { get; } = new List<(string, int, int, int, string)>();

        public IReadOnlyList<DefInfo> WorkTypes { get; set; } = new[]
        {
            new DefInfo("Firefighter", "firefight"), new DefInfo("Patient", "patient"), new DefInfo("Doctor", "doctor"),
            new DefInfo("Cooking", "cook"), new DefInfo("Hunting", "hunt"), new DefInfo("Construction", "construct"),
            new DefInfo("Growing", "grow"), new DefInfo("Mining", "mine"), new DefInfo("PlantCutting", "plant cut"),
            new DefInfo("Hauling", "haul"), new DefInfo("Cleaning", "clean"), new DefInfo("Research", "research")
        };

        public IReadOnlyList<DefInfo> Buildables { get; set; } = new[]
        {
            new DefInfo("Wall", "wall"), new DefInfo("Door", "door"), new DefInfo("Bed", "bed"),
            new DefInfo("SolarGenerator", "solar generator"), new DefInfo("WindTurbine", "wind turbine"), new DefInfo("Sandbags", "sandbags")
        };

        public IReadOnlyList<DefInfo> Stuffs { get; set; } = new[]
        {
            new DefInfo("WoodLog", "wood"), new DefInfo("Steel", "steel"), new DefInfo("BlocksGranite", "granite blocks")
        };

        public IReadOnlyList<DefInfo> TimeAssignments { get; set; } = new[]
        {
            new DefInfo("Anything", "anything"), new DefInfo("Work", "work"), new DefInfo("Joy", "recreation"),
            new DefInfo("Sleep", "sleep"), new DefInfo("Meditate", "meditate")
        };

        public static ColonistInfo MakeColonist(int id, string first, string nick, string last, int x = 10, int z = 10)
        {
            var workTypes = new[] { "Firefighter", "Patient", "Doctor", "Cooking", "Hunting", "Construction", "Growing", "Mining", "PlantCutting", "Hauling", "Cleaning", "Research" };
            return new ColonistInfo
            {
                Id = id,
                Name = string.IsNullOrEmpty(nick) ? first : nick,
                Nickname = nick ?? string.Empty,
                FullName = string.IsNullOrEmpty(nick) ? $"{first} {last}" : $"{first} '{nick}' {last}",
                Age = 30,
                X = x,
                Z = z,
                HealthPercent = 1f,
                MoodPercent = 0.6f,
                MinorBreakThreshold = 0.35f,
                CurrentActivity = "Idle",
                Skills = new[]
                {
                    new SkillInfo { DefName = "Shooting", Label = "shooting", Level = 8, Passion = 2 },
                    new SkillInfo { DefName = "Medicine", Label = "medical", Level = 5, Passion = 1 },
                    new SkillInfo { DefName = "Plants", Label = "plants", Level = 0, Disabled = true }
                },
                WorkPriorities = workTypes.Select(w => new WorkPriorityInfo { WorkTypeDefName = w, Label = w.ToLowerInvariant(), Priority = 3 }).ToList(),
                Timetable = Enumerable.Repeat("Anything", 24).ToList()
            };
        }

        public static ThingInfo MakeThing(int id, string defName, string label, ThingKind kind, int x, int z, bool playerOwned = false, bool harvestable = false)
        {
            return new ThingInfo { Id = id, DefName = defName, Label = label, Kind = kind, X = x, Z = z, PlayerOwned = playerOwned, Harvestable = harvestable, Designations = new List<string>() };
        }

        public ColonyOverview GetOverview() => Overview;
        public IReadOnlyList<ColonistInfo> GetColonists() => Colonists;
        public ResourceSnapshot GetResources() => Resources;
        public IReadOnlyList<ThreatInfo> GetThreats() => Threats;
        public ResearchInfo GetResearch() => Research;
        public IReadOnlyList<ThingInfo> GetSelection() => Selection;
        public MapBounds GetMapBounds() => Bounds;

        public IReadOnlyList<ThingInfo> FindThings(ThingQuery query)
        {
            IEnumerable<ThingInfo> result = Things;
            if (query.Kind != ThingKind.Any) result = result.Where(t => t.Kind == query.Kind);
            if (!string.IsNullOrEmpty(query.DefName)) result = result.Where(t => string.Equals(t.DefName, query.DefName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(query.LabelContains))
            {
                result = result.Where(t => t.Label.IndexOf(query.LabelContains, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           t.DefName.IndexOf(query.LabelContains, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (query.Area.HasValue) result = result.Where(t => query.Area.Value.Contains(t.X, t.Z));
            return result.Take(query.Limit).ToList();
        }

        public CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority)
        {
            Calls.Add($"SetWorkPriority({pawnId},{workTypeDefName},{priority})");
            ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
            WorkPriorityInfo work = pawn?.WorkPriorities.FirstOrDefault(w => w.WorkTypeDefName == workTypeDefName);
            if (work == null) return CommandResult.Fail("pawn or work type not found");
            work.Priority = priority;
            return CommandResult.Ok();
        }

        public CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName)
        {
            Calls.Add($"SetTimeAssignment({pawnId},{hour},{assignmentDefName})");
            ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
            if (pawn == null) return CommandResult.Fail("pawn not found");
            var table = pawn.Timetable.ToList();
            table[hour] = assignmentDefName;
            pawn.Timetable = table;
            return CommandResult.Ok();
        }

        public CommandResult AddDesignationOnCell(int x, int z, string designationDefName)
        {
            Calls.Add($"AddDesignationOnCell({x},{z},{designationDefName})");
            ThingInfo thing = Things.FirstOrDefault(t => t.X == x && t.Z == z && t.Kind == ThingKind.Mineable);
            if (thing == null) return CommandResult.Fail("nothing mineable there");
            Mark(thing, designationDefName);
            return CommandResult.Ok();
        }

        public CommandResult AddDesignationOnThing(int thingId, string designationDefName)
        {
            Calls.Add($"AddDesignationOnThing({thingId},{designationDefName})");
            ThingInfo thing = Things.Concat(Selection).FirstOrDefault(t => t.Id == thingId);
            if (thing == null) return CommandResult.Fail("thing not found");
            Mark(thing, designationDefName);
            return CommandResult.Ok();
        }

        public CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName)
        {
            Calls.Add($"RemoveDesignationOnCell({x},{z},{designationDefName})");
            ThingInfo thing = Things.FirstOrDefault(t => t.X == x && t.Z == z && t.Designations.Contains(designationDefName));
            if (thing == null) return CommandResult.Fail("no such designation there");
            Unmark(thing, designationDefName);
            return CommandResult.Ok();
        }

        public CommandResult RemoveDesignationOnThing(int thingId, string designationDefName)
        {
            Calls.Add($"RemoveDesignationOnThing({thingId},{designationDefName})");
            ThingInfo thing = Things.Concat(Selection).FirstOrDefault(t => t.Id == thingId);
            if (thing == null || !thing.Designations.Contains(designationDefName)) return CommandResult.Fail("no such designation");
            Unmark(thing, designationDefName);
            return CommandResult.Ok();
        }

        public CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName)
        {
            Calls.Add($"TryPlaceBlueprint({buildableDefName},{x},{z},{rotation},{stuffDefName})");
            if (BlockedCells.TryGetValue((x, z), out string reason)) return CommandResult.Fail(reason);
            Blueprints.Add((buildableDefName, x, z, rotation, stuffDefName));
            return CommandResult.Ok();
        }

        public CommandResult SetDrafted(int pawnId, bool drafted)
        {
            Calls.Add($"SetDrafted({pawnId},{drafted})");
            ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
            if (pawn == null) return CommandResult.Fail("pawn not found");
            pawn.Drafted = drafted;
            return CommandResult.Ok();
        }

        public CommandResult OrderMoveTo(int pawnId, int x, int z)
        {
            Calls.Add($"OrderMoveTo({pawnId},{x},{z})");
            if (UnwalkableCells.Contains((x, z))) return CommandResult.Fail("cell is not walkable");
            return Colonists.Any(c => c.Id == pawnId) ? CommandResult.Ok() : CommandResult.Fail("pawn not found");
        }

        public CommandResult OrderAttack(int pawnId, int targetId)
        {
            Calls.Add($"OrderAttack({pawnId},{targetId})");
            bool targetExists = Threats.Any(t => t.Id == targetId) || Things.Any(t => t.Id == targetId);
            return targetExists ? CommandResult.Ok() : CommandResult.Fail("target not found");
        }

        private static void Mark(ThingInfo thing, string def)
        {
            var list = thing.Designations.ToList();
            if (!list.Contains(def)) list.Add(def);
            thing.Designations = list;
        }

        private static void Unmark(ThingInfo thing, string def)
        {
            thing.Designations = thing.Designations.Where(d => d != def).ToList();
        }
    }
}
