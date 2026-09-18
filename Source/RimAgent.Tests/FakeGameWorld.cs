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
        public List<TraderInfo> Traders { get; } = new List<TraderInfo>();
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

        public IReadOnlyList<DefInfo> TradeGoods { get; set; } = new[]
        {
            new DefInfo("Steel", "steel"), new DefInfo("Silver", "silver"),
            new DefInfo("ComponentSpacer", "spacer component"), new DefInfo("MedicineUltratech", "ultratech medicine"),
            new DefInfo("HerbalMedicine", "herbal medicine"), new DefInfo("Corn", "corn")
        };

        /// <summary>假結算的單件價格（正式端用遊戲公式）。買入賣出同價；缺省為 1。</summary>
        public Dictionary<string, float> Prices { get; } = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DefInfo> MedicalCareLevels { get; set; } = new[]
        {
            new DefInfo("NoCare", "no care"), new DefInfo("NoMeds", "doctor care only, no medicine"),
            new DefInfo("HerbalOrWorse", "herbal medicine or worse"), new DefInfo("NormalOrWorse", "industrial medicine or worse"),
            new DefInfo("Best", "best")
        };

        public IReadOnlyList<DefInfo> SurgeryRecipes { get; set; } = new[]
        {
            new DefInfo("RemoveBodyPart", "remove body part"),
            new DefInfo("InstallArtificialBodyPart", "install artificial body part"),
            new DefInfo("AdministerAnesthetic", "administer anesthetic")
        };

        /// <summary>研究還沒完成的手術 defName（正式端用遊戲研究狀態判斷）。</summary>
        public HashSet<string> LockedSurgeries { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<PrisonerInfo> Prisoners { get; } = new List<PrisonerInfo>();

        public IReadOnlyList<DefInfo> PrisonerInteractionModes { get; set; } = new[]
        {
            new DefInfo("MaintainOnly", "maintain only"), new DefInfo("AttemptRecruit", "recruit"),
            new DefInfo("ReduceResistance", "reduce resistance"), new DefInfo("Release", "release"),
            new DefInfo("Execution", "execution")
        };

        public List<AnimalInfo> Animals { get; } = new List<AnimalInfo>();

        public IReadOnlyList<DefInfo> AnimalTraining { get; set; } = new[]
        {
            new DefInfo("Obedience", "obedience"), new DefInfo("Release", "release"),
            new DefInfo("Rescue", "rescue"), new DefInfo("Haul", "haul")
        };

        public IReadOnlyList<DefInfo> AnimalAreas { get; set; } = new[]
        {
            new DefInfo("Barn", "Barn"), new DefInfo("Pasture", "Pasture")
        };

        /// <summary>已標記馴養的動物 id（正式端讀 designation）。</summary>
        public HashSet<int> TameMarked { get; } = new HashSet<int>();
        /// <summary>開啟中的訓練，key 為 pawnId:defName（正式端讀 training tracker）。</summary>
        public HashSet<string> TrainingOn { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<ZoneInfo> Zones { get; } = new List<ZoneInfo>();

        public List<MapInfo> Maps { get; } = new List<MapInfo>
        {
            new MapInfo { Id = 1, Label = "New Arrivals", Tile = 12345, ColonistCount = 2, IsCurrent = true }
        };

        /// <summary>目前操作的地圖 id（正式端為 Find.CurrentMap）。</summary>
        public int CurrentMapId { get; set; } = 1;

        /// <summary>目前時間流速 defName（正式端讀 TickManager）。</summary>
        public string CurrentTimeSpeed { get; set; } = "Normal";

        public IReadOnlyList<DefInfo> TimeSpeeds { get; set; } = TimeSpeedDirectory.Default;

        public TimeSpeedInfo GetTimeSpeed()
        {
            return new TimeSpeedInfo { Current = CurrentTimeSpeed, Available = TimeSpeeds };
        }

        public CommandResult SetTimeSpeed(string speedDefName)
        {
            Calls.Add($"SetTimeSpeed({speedDefName})");
            DefInfo match = TimeSpeeds.FirstOrDefault(d => string.Equals(d.DefName, speedDefName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return CommandResult.Fail($"unknown time speed {speedDefName}");
            CurrentTimeSpeed = match.DefName;
            return CommandResult.Ok($"Time speed set to {match.DefName}.");
        }

        public ScreenshotInfo ScreenshotResult { get; set; } = new ScreenshotInfo
        {
            Success = true,
            FilePath = "C:/RimWorld/Screenshots/screenshot.png",
            Width = 1920,
            Height = 1080
        };

        public ScreenshotInfo TakeScreenshot(string filename = null)
        {
            Calls.Add($"TakeScreenshot({filename ?? "null"})");
            if (ScreenshotResult != null && !ScreenshotResult.Success)
            {
                return ScreenshotResult;
            }

            string name = string.IsNullOrWhiteSpace(filename) ? "screenshot" : filename;
            return new ScreenshotInfo
            {
                Success = ScreenshotResult?.Success ?? true,
                FilePath = $"C:/RimWorld/Screenshots/{name}.png",
                Width = ScreenshotResult?.Width ?? 1920,
                Height = ScreenshotResult?.Height ?? 1080,
                Error = ScreenshotResult?.Error ?? string.Empty
            };
        }

        /// <summary>已完成的研究 defName（正式端用遊戲研究狀態判斷）。</summary>
        public HashSet<string> FinishedResearch { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<DefInfo> StorageFilterables { get; set; } = new[]
        {
            new DefInfo("Steel", "steel"), new DefInfo("WoodLog", "wood"), new DefInfo("Corn", "corn"),
            new DefInfo("Rice", "rice"), new DefInfo("MedicineUltratech", "ultratech medicine"),
            new DefInfo("ComponentSpacer", "spacer component")
        };

        /// <summary>各儲藏區明確禁收的 defName（正式端讀 ThingFilter）。</summary>
        public Dictionary<int, HashSet<string>> ZoneDisallowed { get; } = new Dictionary<int, HashSet<string>>();

        public List<WorkTableInfo> WorkTables { get; } = new List<WorkTableInfo>();

        /// <summary>各工作台的帳單（正式端讀 BillStack）。</summary>
        public Dictionary<int, List<BillInfo>> TableBills { get; } = new Dictionary<int, List<BillInfo>>();

        public IReadOnlyList<DefInfo> BillRecipes { get; set; } = new[]
        {
            new DefInfo("MakeComponent", "component"), new DefInfo("MakeShortBow", "short bow"),
            new DefInfo("ButcherCreature", "butcher creature")
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
                Timetable = Enumerable.Repeat("Anything", 24).ToList(),
                MedicalCare = "NoMeds"
            };
        }

        public static ThingInfo MakeThing(int id, string defName, string label, ThingKind kind, int x, int z, bool playerOwned = false, bool harvestable = false)
        {
            return new ThingInfo { Id = id, DefName = defName, Label = label, Kind = kind, X = x, Z = z, PlayerOwned = playerOwned, Harvestable = harvestable, Designations = new List<string>() };
        }

        public ColonyOverview GetOverview() => Overview;
        public IReadOnlyList<ColonistInfo> GetColonists() => Colonists;
        public IReadOnlyList<PrisonerInfo> GetPrisoners() => Prisoners;
        public IReadOnlyList<AnimalInfo> GetAnimals() => Animals;
        public IReadOnlyList<ZoneInfo> GetZones() => Zones;
        public IReadOnlyList<WorkTableInfo> GetWorkTables() => WorkTables;
        public IReadOnlyList<BillInfo> GetBills(int tableId)
        {
            return TableBills.TryGetValue(tableId, out List<BillInfo> bills) ? bills : (IReadOnlyList<BillInfo>)Array.Empty<BillInfo>();
        }
        public List<ColonistHealthInfo> Health { get; } = new List<ColonistHealthInfo>();
        public IReadOnlyList<ColonistHealthInfo> GetHealth() => Health;
        public ResourceSnapshot GetResources() => Resources;
        public IReadOnlyList<ThreatInfo> GetThreats() => Threats;
        public IReadOnlyList<TraderInfo> GetTraders() => Traders;
        public ResearchInfo GetResearch() => Research;

        public IReadOnlyList<DefInfo> ResearchProjects
        {
            get
            {
                var projects = new List<DefInfo>();
                if (Research.Current != null) projects.Add(new DefInfo(Research.Current.DefName, Research.Current.Label));
                foreach (ResearchProjectInfo p in Research.Available)
                {
                    if (projects.Any(d => string.Equals(d.DefName, p.DefName, StringComparison.OrdinalIgnoreCase))) continue;
                    projects.Add(new DefInfo(p.DefName, p.Label));
                }
                return projects;
            }
        }

        public CommandResult SetResearch(string researchDefName)
        {
            Calls.Add($"SetResearch({researchDefName})");
            string want = (researchDefName ?? string.Empty).Trim();
            if (want.Length == 0 || string.Equals(want, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(want, "stop", StringComparison.OrdinalIgnoreCase))
            {
                if (Research.Current == null) return CommandResult.Ok("No research is running.");
                string stopped = Research.Current.Label;
                Research = new ResearchInfo { Available = Research.Available };
                return CommandResult.Ok($"Stopped research {stopped}.");
            }
            if (Research.Current != null && string.Equals(Research.Current.DefName, want, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Ok($"Research {Research.Current.Label} is already running.");
            ResearchProjectInfo project = Research.Available.FirstOrDefault(p => string.Equals(p.DefName, want, StringComparison.OrdinalIgnoreCase));
            if (project == null)
            {
                if (FinishedResearch.Contains(want)) return CommandResult.Fail($"{want} is already finished");
                return CommandResult.Fail($"unknown research {want}");
            }
            Research = new ResearchInfo { Current = project, Available = Research.Available };
            return CommandResult.Ok($"Research switched to {project.Label}.");
        }

        public IReadOnlyList<MapInfo> GetMaps()
        {
            return Maps.Select(m => new MapInfo
            {
                Id = m.Id,
                Label = m.Label,
                Tile = m.Tile,
                ColonistCount = m.ColonistCount,
                IsCurrent = m.Id == CurrentMapId
            }).ToList();
        }

        public CommandResult SetCurrentMap(int mapId)
        {
            Calls.Add($"SetCurrentMap({mapId})");
            MapInfo map = Maps.FirstOrDefault(m => m.Id == mapId);
            if (map == null) return CommandResult.Fail($"no map has id {mapId}");
            if (mapId == CurrentMapId) return CommandResult.Ok($"{map.Label} is already the current map.");
            CurrentMapId = mapId;
            return CommandResult.Ok($"Switched to {map.Label} [{mapId}]. Later tools now operate on this map.");
        }
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

        public CommandResult SetMedicalCare(int pawnId, string careDefName)
        {
            Calls.Add($"SetMedicalCare({pawnId},{careDefName})");
            ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == pawnId);
            if (pawn == null) return CommandResult.Fail("pawn not found");
            if (!MedicalCareLevels.Any(d => string.Equals(d.DefName, careDefName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"unknown medical care level {careDefName}");
            pawn.MedicalCare = careDefName;
            return CommandResult.Ok();
        }

        private static readonly Dictionary<string, string> CaravanHeadings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "N", "north" }, { "NORTH", "north" },
            { "NE", "north-east" }, { "NORTHEAST", "north-east" }, { "NORTH-EAST", "north-east" },
            { "E", "east" }, { "EAST", "east" },
            { "SE", "south-east" }, { "SOUTHEAST", "south-east" }, { "SOUTH-EAST", "south-east" },
            { "S", "south" }, { "SOUTH", "south" },
            { "SW", "south-west" }, { "SOUTHWEST", "south-west" }, { "SOUTH-WEST", "south-west" },
            { "W", "west" }, { "WEST", "west" },
            { "NW", "north-west" }, { "NORTHWEST", "north-west" }, { "NORTH-WEST", "north-west" }
        };

        public CommandResult FormCaravan(CaravanRequest request)
        {
            Calls.Add($"FormCaravan({string.Join("+", (request?.PawnIds ?? Array.Empty<int>()).ToArray())},{request?.Heading})");
            if (request == null) return CommandResult.Fail("no caravan requested");
            var ids = (request.PawnIds ?? Array.Empty<int>()).ToList();
            if (ids.Count == 0) return CommandResult.Fail("no colonists named. Give at least one member, e.g. \"Ali, Bob\".");
            string display;
            if (!CaravanHeadings.TryGetValue((request.Heading ?? string.Empty).Trim(), out display))
                return CommandResult.Fail($"unknown heading \"{request.Heading}\". Use N, NE, E, SE, S, SW, W or NW.");
            var members = new List<ColonistInfo>();
            foreach (int id in ids)
            {
                ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == id);
                if (pawn == null) return CommandResult.Fail($"no colonist has id {id} (prisoners and visitors cannot form a caravan)");
                if (pawn.Downed) return CommandResult.Fail($"{pawn.Name} is downed and cannot join a caravan");
                if (pawn.Drafted) return CommandResult.Fail($"{pawn.Name} is drafted; undraft them first");
                members.Add(pawn);
            }
            return CommandResult.Ok($"Formed a caravan with {string.Join(", ", members.Select(m => m.Name).ToArray())}, heading {display}.");
        }

        public CommandResult ScheduleSurgery(SurgeryRequest request)
        {
            Calls.Add($"ScheduleSurgery({request?.PawnId},{request?.RecipeDefName},{request?.BodyPartLabel})");
            if (request == null) return CommandResult.Fail("no surgery requested");
            ColonistInfo pawn = Colonists.FirstOrDefault(c => c.Id == request.PawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            DefInfo recipe = SurgeryRecipes.FirstOrDefault(d => string.Equals(d.DefName, request.RecipeDefName, StringComparison.OrdinalIgnoreCase));
            if (recipe == null) return CommandResult.Fail($"unknown surgery {request.RecipeDefName}");
            if (LockedSurgeries.Contains(recipe.DefName)) return CommandResult.Fail($"{recipe.Label} needs more research first");
            ColonistHealthInfo health = Health.FirstOrDefault(h => h.PawnId == request.PawnId);
            if (health == null)
            {
                health = new ColonistHealthInfo { PawnId = request.PawnId };
                Health.Add(health);
            }
            string part = request.BodyPartLabel ?? string.Empty;
            var pending = health.PendingSurgeries.ToList();
            if (pending.Any(b => string.Equals(b.Label, recipe.Label, StringComparison.OrdinalIgnoreCase)
                && string.Equals(b.Part ?? string.Empty, part, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"{recipe.Label} is already scheduled on {pawn.Name}");
            pending.Add(new SurgeryBillInfo { Label = recipe.Label, Part = part });
            health.PendingSurgeries = pending;
            return CommandResult.Ok($"Scheduled {recipe.Label} on {pawn.Name}" + (part.Length > 0 ? $" ({part})" : string.Empty) + ".");
        }

        public CommandResult SetPrisonerInteraction(int pawnId, string modeDefName)
        {
            Calls.Add($"SetPrisonerInteraction({pawnId},{modeDefName})");
            PrisonerInfo pawn = Prisoners.FirstOrDefault(p => p.Id == pawnId);
            if (pawn == null) return CommandResult.Fail("prisoner not found");
            DefInfo mode = PrisonerInteractionModes.FirstOrDefault(d => string.Equals(d.DefName, modeDefName, StringComparison.OrdinalIgnoreCase));
            if (mode == null) return CommandResult.Fail($"unknown prisoner interaction mode {modeDefName}");
            pawn.InteractionMode = mode.DefName;
            pawn.InteractionModeLabel = mode.Label;
            return CommandResult.Ok();
        }

        public CommandResult ReleasePrisoner(int pawnId)
        {
            Calls.Add($"ReleasePrisoner({pawnId})");
            PrisonerInfo pawn = Prisoners.FirstOrDefault(p => p.Id == pawnId);
            if (pawn == null) return CommandResult.Fail("prisoner not found");
            Prisoners.Remove(pawn);
            return CommandResult.Ok($"{pawn.Name} was released and left the colony.");
        }

        public CommandResult TameAnimal(int pawnId)
        {
            Calls.Add($"TameAnimal({pawnId})");
            AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
            if (animal == null) return CommandResult.Fail("animal not found");
            if (!animal.Wild) return CommandResult.Fail($"{animal.Name} is already tame");
            if (!TameMarked.Add(pawnId)) return CommandResult.Fail($"{animal.Name} is already marked for taming");
            return CommandResult.Ok($"Marked {animal.Name} for taming. A handler will attempt it when available.");
        }

        public CommandResult SetAnimalTraining(int pawnId, string trainableDefName, bool enable)
        {
            Calls.Add($"SetAnimalTraining({pawnId},{trainableDefName},{enable})");
            AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
            if (animal == null) return CommandResult.Fail("animal not found");
            if (animal.Wild) return CommandResult.Fail($"{animal.Name} is wild; tame it first");
            DefInfo training = AnimalTraining.FirstOrDefault(d => string.Equals(d.DefName, trainableDefName, StringComparison.OrdinalIgnoreCase));
            if (training == null) return CommandResult.Fail($"unknown animal training {trainableDefName}");
            string key = $"{pawnId}:{training.DefName}";
            if (enable) TrainingOn.Add(key);
            else TrainingOn.Remove(key);
            return CommandResult.Ok(enable
                ? $"{animal.Name} will now be trained in {training.Label}."
                : $"{animal.Name} will no longer be trained in {training.Label}.");
        }

        public CommandResult SetAnimalArea(int pawnId, string areaLabel)
        {
            Calls.Add($"SetAnimalArea({pawnId},{areaLabel})");
            AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == pawnId);
            if (animal == null) return CommandResult.Fail("animal not found");
            if (animal.Wild) return CommandResult.Fail($"{animal.Name} is wild; tame it first");
            string label = areaLabel ?? string.Empty;
            if (label.Length > 0 && !AnimalAreas.Any(d => string.Equals(d.Label, label, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"unknown animal area {label}");
            animal.Area = label;
            return CommandResult.Ok($"{animal.Name}'s allowed area is now {(label.Length > 0 ? label : "unrestricted")}.");
        }

        public CommandResult AddZone(string kind, CellRectSpec rect, string plantDefName)
        {
            Calls.Add($"AddZone({kind},{rect})");
            bool stockpile = string.Equals(kind, "Stockpile", StringComparison.OrdinalIgnoreCase);
            bool growing = string.Equals(kind, "Growing", StringComparison.OrdinalIgnoreCase);
            if (!stockpile && !growing) return CommandResult.Fail($"unknown zone kind {kind}. Use stockpile or growing.");
            int id = Zones.Count == 0 ? 1 : Zones.Max(z => z.Id) + 1;
            string label = stockpile ? $"Stockpile {id}" : $"Growing zone {id}";
            Zones.Add(new ZoneInfo { Id = id, Label = label, Kind = stockpile ? "Stockpile" : "Growing", CellCount = rect.CellCount });
            return CommandResult.Ok($"Added {label.ToLowerInvariant()} [{id}] covering {rect.CellCount} cells.");
        }

        public CommandResult DeleteZone(int zoneId)
        {
            Calls.Add($"DeleteZone({zoneId})");
            ZoneInfo zone = Zones.FirstOrDefault(z => z.Id == zoneId);
            if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
            Zones.Remove(zone);
            ZoneDisallowed.Remove(zoneId);
            return CommandResult.Ok($"Deleted zone [{zoneId}] {zone.Label}. Stored items stay on the ground.");
        }

        public CommandResult SetZoneFilter(int zoneId, IReadOnlyList<string> allowDefNames, IReadOnlyList<string> disallowDefNames)
        {
            Calls.Add($"SetZoneFilter({zoneId})");
            ZoneInfo zone = Zones.FirstOrDefault(z => z.Id == zoneId);
            if (zone == null) return CommandResult.Fail($"no zone has id {zoneId}");
            if (!string.Equals(zone.Kind, "Stockpile", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail($"{zone.Label} is a growing zone and has no storage filter");
            var allows = (allowDefNames ?? Array.Empty<string>()).ToList();
            var disallows = (disallowDefNames ?? Array.Empty<string>()).ToList();
            if (allows.Count == 0 && disallows.Count == 0) return CommandResult.Fail("nothing to allow or disallow");
            foreach (string defName in allows.Concat(disallows))
            {
                if (!StorageFilterables.Any(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase)))
                    return CommandResult.Fail($"unknown stored item {defName}");
            }
            if (!ZoneDisallowed.TryGetValue(zoneId, out HashSet<string> banned))
            {
                banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ZoneDisallowed[zoneId] = banned;
            }
            foreach (string defName in allows) banned.Remove(defName);
            foreach (string defName in disallows) banned.Add(defName);
            var parts = new List<string>();
            if (allows.Count > 0) parts.Add("allows " + string.Join(", ", allows.Select(ItemLabel).ToArray()));
            if (disallows.Count > 0) parts.Add("disallows " + string.Join(", ", disallows.Select(ItemLabel).ToArray()));
            return CommandResult.Ok($"Zone [{zoneId}] {zone.Label} now {string.Join("; ", parts.ToArray())}.");
        }

        private string ItemLabel(string defName)
        {
            DefInfo def = StorageFilterables.FirstOrDefault(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase));
            return def != null ? def.Label : defName;
        }

        public CommandResult AddBill(int tableId, string recipeDefName, int count)
        {
            Calls.Add($"AddBill({tableId},{recipeDefName},{count})");
            WorkTableInfo table = WorkTables.FirstOrDefault(t => t.Id == tableId);
            if (table == null) return CommandResult.Fail($"no work table has id {tableId}");
            DefInfo recipe = BillRecipes.FirstOrDefault(d => string.Equals(d.DefName, recipeDefName, StringComparison.OrdinalIgnoreCase));
            if (recipe == null) return CommandResult.Fail($"unknown bill recipe {recipeDefName}");
            if (count < 1) return CommandResult.Fail($"count must be at least 1, not {count}");
            if (!TableBills.TryGetValue(tableId, out List<BillInfo> bills))
            {
                bills = new List<BillInfo>();
                TableBills[tableId] = bills;
            }
            bills.Add(new BillInfo { Index = bills.Count + 1, Label = $"Make {recipe.Label}", Repeat = $"{count}x" });
            table.BillCount = bills.Count;
            return CommandResult.Ok($"Added bill [{bills.Count}] Make {recipe.Label} x{count} on {table.Label}.");
        }

        public CommandResult DeleteBill(int tableId, int index)
        {
            Calls.Add($"DeleteBill({tableId},{index})");
            WorkTableInfo table = WorkTables.FirstOrDefault(t => t.Id == tableId);
            if (table == null) return CommandResult.Fail($"no work table has id {tableId}");
            if (!TableBills.TryGetValue(tableId, out List<BillInfo> bills) || index < 1 || index > bills.Count)
                return CommandResult.Fail($"bill [{index}] does not exist on {table?.Label ?? "that table"}. List the bills again to get valid numbers.");
            BillInfo removed = bills[index - 1];
            bills.RemoveAt(index - 1);
            for (int i = 0; i < bills.Count; i++) bills[i].Index = i + 1;
            table.BillCount = bills.Count;
            return CommandResult.Ok($"Deleted bill {removed.Label} from {table.Label}. Materials already gathered stay where they are.");
        }

        /// <summary>
        /// 與正式端同語義的假結算：單價取貨物市價（正式端用遊戲公式），先驗證全部再交割，
        /// 餘額與庫存不足整筆不做。殖民地庫存與銀兩取自 <see cref="Resources"/>。
        /// </summary>
        public CommandResult ExecuteTrade(TradeRequest request)
        {
            Calls.Add($"ExecuteTrade({request?.TraderId},{request?.NegotiatorPawnId})");
            if (request == null) return CommandResult.Fail("no trade requested");
            TraderInfo trader = Traders.FirstOrDefault(t => string.Equals(t.Id, request.TraderId, StringComparison.OrdinalIgnoreCase));
            if (trader == null) return CommandResult.Fail($"unknown trader \"{request.TraderId}\". List the traders again to get a valid id.");
            ColonistInfo negotiator = Colonists.FirstOrDefault(c => c.Id == request.NegotiatorPawnId);
            if (negotiator == null) return CommandResult.Fail("negotiator not found");
            if (negotiator.Downed) return CommandResult.Fail($"{negotiator.Name} is downed and cannot negotiate.");
            var buys = request.Buys ?? Array.Empty<TradeLine>();
            var sells = request.Sells ?? Array.Empty<TradeLine>();
            if (buys.Count == 0 && sells.Count == 0) return CommandResult.Fail("nothing to buy or sell.");

            foreach (TradeLine line in buys.Concat(sells))
            {
                if (string.Equals(line.DefName, "Silver", StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("silver moves automatically as payment; do not list it.");
                if (line.Count <= 0) return CommandResult.Fail($"invalid count {line.Count} for {line.DefName}.");
            }
            foreach (TradeLine line in buys)
            {
                int available = trader.Goods.Where(g => string.Equals(g.DefName, line.DefName, StringComparison.OrdinalIgnoreCase)).Sum(g => g.Count);
                if (available < line.Count)
                    return CommandResult.Fail($"{trader.Name} only has {available} {line.DefName} for sale, not {line.Count}.");
            }
            var stock = Resources.Items.ToList();
            foreach (TradeLine line in sells)
            {
                int available = stock.Where(r => string.Equals(r.DefName, line.DefName, StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
                if (available < line.Count)
                    return CommandResult.Fail($"the colony only has {available} sellable {line.DefName}, not {line.Count}.");
            }

            float PriceOf(string defName, float fallback)
            {
                return Prices.TryGetValue(defName, out float p) ? p : fallback;
            }
            int buyTotal = buys.Sum(l => (int)(PriceOf(l.DefName, trader.Goods.FirstOrDefault(g => string.Equals(g.DefName, l.DefName, StringComparison.OrdinalIgnoreCase))?.MarketValue ?? 1f) * l.Count));
            int sellTotal = sells.Sum(l => (int)(PriceOf(l.DefName, 1f) * l.Count));
            int colonySilver = stock.Where(r => string.Equals(r.DefName, "Silver", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
            if (colonySilver - buyTotal + sellTotal < 0)
                return CommandResult.Fail($"the colony cannot afford this: needs {buyTotal - sellTotal} silver but has {colonySilver}.");
            if (trader.Silver - sellTotal + buyTotal < 0)
                return CommandResult.Fail($"{trader.Name} cannot afford this: needs {sellTotal - buyTotal} silver but has {trader.Silver}.");

            foreach (TradeLine line in buys) TakeFrom(trader.Goods, line.DefName, line.Count);
            foreach (TradeLine line in buys) AddTo(stock, line.DefName, LabelOf(line.DefName), line.Count);
            foreach (TradeLine line in sells) TakeFrom(stock, line.DefName, line.Count);
            foreach (TradeLine line in sells)
            {
                var goods = trader.Goods.ToList();
                AddTo(goods, line.DefName, LabelOf(line.DefName), line.Count, PriceOf(line.DefName, 1f));
                trader.Goods = goods;
            }
            TakeFrom(stock, "Silver", buyTotal);
            AddTo(stock, "Silver", "silver", sellTotal);
            trader.Silver += buyTotal - sellTotal;
            Resources = new ResourceSnapshot { TotalHumanEdibleNutrition = Resources.TotalHumanEdibleNutrition, Items = stock };

            string bought = buys.Count == 0 ? string.Empty : "bought " + string.Join(", ", buys.Select(l => $"{LabelOf(l.DefName)} ({l.DefName}) x{l.Count}"));
            string sold = sells.Count == 0 ? string.Empty : "sold " + string.Join(", ", sells.Select(l => $"{LabelOf(l.DefName)} ({l.DefName}) x{l.Count}"));
            string what = string.Join("; ", new[] { bought, sold }.Where(s => s.Length > 0));
            return CommandResult.Ok($"Traded with {trader.Name} (negotiated by {negotiator.Name}): {what}. Colony silver {colonySilver} -> {colonySilver - buyTotal + sellTotal}.");
        }

        private string LabelOf(string defName)
        {
            DefInfo known = TradeGoods.FirstOrDefault(d => string.Equals(d.DefName, defName, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known.Label;
            ResourceInfo resource = Resources.Items.FirstOrDefault(r => string.Equals(r.DefName, defName, StringComparison.OrdinalIgnoreCase));
            return resource?.Label ?? defName;
        }

        private static void TakeFrom<T>(IEnumerable<T> items, string defName, int count) where T : class
        {
            int rest = count;
            foreach (T item in items.ToList())
            {
                if (rest <= 0) break;
                if (item is TradeGoodsInfo g)
                {
                    if (!string.Equals(g.DefName, defName, StringComparison.OrdinalIgnoreCase)) continue;
                    int take = Math.Min(rest, g.Count);
                    g.Count -= take;
                    rest -= take;
                }
                else if (item is ResourceInfo r)
                {
                    if (!string.Equals(r.DefName, defName, StringComparison.OrdinalIgnoreCase)) continue;
                    int take = Math.Min(rest, r.Count);
                    r.Count -= take;
                    rest -= take;
                }
            }
        }

        private static void AddTo<T>(List<T> items, string defName, string label, int count, float marketValue = 1f) where T : class, new()
        {
            if (count <= 0) return;
            if (typeof(T) == typeof(ResourceInfo))
            {
                var list = items as List<ResourceInfo>;
                ResourceInfo existing = list.FirstOrDefault(r => string.Equals(r.DefName, defName, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Count += count;
                else list.Add(new ResourceInfo { DefName = defName, Label = label, Count = count });
            }
            else if (typeof(T) == typeof(TradeGoodsInfo))
            {
                var list = items as List<TradeGoodsInfo>;
                TradeGoodsInfo existing = list.FirstOrDefault(g => string.Equals(g.DefName, defName, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Count += count;
                else list.Add(new TradeGoodsInfo { DefName = defName, Label = label, Count = count, MarketValue = marketValue });
            }
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
