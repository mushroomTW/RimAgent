using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimAgent.Game
{
    /// <summary>
    /// <see cref="IGameWorld"/> 的正式實作：對 Find.CurrentMap 的薄轉接層。
    /// 只做 RimWorld API 呼叫與純資料轉換，不含決策邏輯；假設呼叫者已在 Unity 主線程
    /// （由框架的 AsMainThreadFunctionInvokingClient 保證）。這一層只能在遊戲內驗證。
    /// </summary>
    internal sealed class RimWorldGameWorld : IGameWorld
    {
        private const string MineDefName = "Mine";

        private IReadOnlyList<DefInfo> _workTypes;
        private IReadOnlyList<DefInfo> _buildables;
        private IReadOnlyList<DefInfo> _stuffs;
        private IReadOnlyList<DefInfo> _timeAssignments;

        private static Map Map => Find.CurrentMap;

        public ColonyOverview GetOverview()
        {
            Map map = Map;
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            return new ColonyOverview
            {
                ColonyName = map.Parent?.Label ?? Faction.OfPlayer?.Name ?? string.Empty,
                ColonistCount = colonists.Count,
                Year = GenLocalDate.Year(map),
                Season = GenLocalDate.Season(map).ToString(),
                DayOfSeason = GenLocalDate.DayOfSeason(map),
                Hour = GenLocalDate.HourInteger(map),
                Weather = map.weatherManager.curWeather?.label ?? string.Empty,
                OutdoorTemperatureC = map.mapTemperature.OutdoorTemp,
                Wealth = map.wealthWatcher.WealthTotal,
                HostileCount = GetThreats().Count(t => t.Kind != "Fire"),
                ColonistsNearMentalBreak = colonists.Count(IsNearMentalBreak)
            };
        }

        public IReadOnlyList<ColonistInfo> GetColonists()
        {
            return Map.mapPawns.FreeColonistsSpawned.Select(ToColonist).ToList();
        }

        public ResourceSnapshot GetResources()
        {
            ResourceCounter counter = Map.resourceCounter;
            return new ResourceSnapshot
            {
                TotalHumanEdibleNutrition = counter.TotalHumanEdibleNutrition,
                Items = counter.AllCountedAmounts
                    .Where(pair => pair.Value > 0)
                    .Select(pair => new ResourceInfo
                    {
                        DefName = pair.Key.defName,
                        Label = pair.Key.label,
                        Count = pair.Value,
                        Category = pair.Key.FirstThingCategory?.label ?? pair.Key.category.ToString()
                    })
                    .ToList()
            };
        }

        public IReadOnlyList<ThreatInfo> GetThreats()
        {
            Map map = Map;
            var threats = new List<ThreatInfo>();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Dead || !pawn.HostileTo(Faction.OfPlayer)) continue;
                string kind = pawn.RaceProps.Humanlike ? "HostilePawn"
                    : pawn.InMentalState ? "Manhunter"
                    : "HostileAnimal";
                threats.Add(new ThreatInfo
                {
                    Id = pawn.thingIDNumber,
                    Label = pawn.LabelShort,
                    Kind = kind,
                    Faction = pawn.Faction?.Name ?? string.Empty,
                    X = pawn.Position.x,
                    Z = pawn.Position.z,
                    Downed = pawn.Downed
                });
            }
            foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
            {
                threats.Add(new ThreatInfo { Id = fire.thingIDNumber, Label = fire.Label, Kind = "Fire", X = fire.Position.x, Z = fire.Position.z });
            }
            return threats;
        }

        public ResearchInfo GetResearch()
        {
            ResearchProjectDef current = Find.ResearchManager.GetProject();
            return new ResearchInfo
            {
                Current = current == null ? null : ToResearch(current),
                Available = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .Where(r => !r.IsFinished && r.CanStartNow)
                    .OrderBy(r => r.CostApparent)
                    .Select(ToResearch)
                    .ToList()
            };
        }

        public IReadOnlyList<ThingInfo> FindThings(ThingQuery query)
        {
            Map map = Map;
            IEnumerable<Thing> things = map.listerThings.AllThings.Where(t => t.Spawned && KindOf(t) != null);
            if (query.Kind != ThingKind.Any) things = things.Where(t => KindOf(t) == query.Kind);
            if (!string.IsNullOrEmpty(query.DefName))
            {
                things = things.Where(t => string.Equals(t.def.defName, query.DefName, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(query.LabelContains))
            {
                string needle = query.LabelContains;
                things = things.Where(t =>
                    t.def.defName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (t.def.label ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (query.Area.HasValue)
            {
                CellRectSpec area = query.Area.Value;
                things = things.Where(t => area.Contains(t.Position.x, t.Position.z));
            }
            return things.Take(query.Limit).Select(t => ToThing(t, map)).ToList();
        }

        public IReadOnlyList<ThingInfo> GetSelection()
        {
            Map map = Map;
            return Find.Selector.SelectedObjects.OfType<Thing>()
                .Where(t => t.Spawned && t.Map == map && KindOf(t) != null)
                .Select(t => ToThing(t, map))
                .ToList();
        }

        public MapBounds GetMapBounds()
        {
            IntVec3 size = Map.Size;
            return new MapBounds { Width = size.x, Height = size.z };
        }

        public IReadOnlyList<DefInfo> WorkTypes =>
            _workTypes ?? (_workTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading
                .Where(w => w.visible)
                .Select(w => new DefInfo(w.defName, w.labelShort ?? w.label))
                .ToList());

        public IReadOnlyList<DefInfo> Buildables =>
            _buildables ?? (_buildables = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer).Select(d => new DefInfo(d.defName, d.label))
                .Concat(DefDatabase<TerrainDef>.AllDefsListForReading.Where(d => d.BuildableByPlayer).Select(d => new DefInfo(d.defName, d.label)))
                .ToList());

        public IReadOnlyList<DefInfo> Stuffs =>
            _stuffs ?? (_stuffs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.IsStuff)
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public IReadOnlyList<DefInfo> TimeAssignments =>
            _timeAssignments ?? (_timeAssignments = DefDatabase<TimeAssignmentDef>.AllDefsListForReading
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public CommandResult SetWorkPriority(int pawnId, string workTypeDefName, int priority)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            if (pawn.workSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no work settings");
            WorkTypeDef workType = DefDatabase<WorkTypeDef>.GetNamedSilentFail(workTypeDefName);
            if (workType == null) return CommandResult.Fail($"unknown work type {workTypeDefName}");
            if (pawn.WorkTypeIsDisabled(workType)) return CommandResult.Fail($"{pawn.LabelShort} is incapable of {workType.label}");

            string note = string.Empty;
            bool needsManualPriorities = priority != 0 && priority != 3;
            if (needsManualPriorities && !Current.Game.playSettings.useWorkPriorities)
            {
                // 1/2/4 只有在手動優先級開啟時才有意義；否則遊戲只認勾選／不勾選。
                Current.Game.playSettings.useWorkPriorities = true;
                note = "Manual work priorities were turned on for the colony.";
            }
            pawn.workSettings.SetPriority(workType, priority);
            return CommandResult.Ok(note);
        }

        public CommandResult SetTimeAssignment(int pawnId, int hour, string assignmentDefName)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            if (pawn.timetable == null) return CommandResult.Fail($"{pawn.LabelShort} has no schedule");
            TimeAssignmentDef assignment = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(assignmentDefName);
            if (assignment == null) return CommandResult.Fail($"unknown time assignment {assignmentDefName}");
            if (hour < 0 || hour > 23) return CommandResult.Fail("hour must be 0-23");
            pawn.timetable.SetAssignment(hour, assignment);
            return CommandResult.Ok();
        }

        public CommandResult AddDesignationOnCell(int x, int z, string designationDefName)
        {
            Map map = Map;
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            if (def == null) return CommandResult.Fail($"unknown designation {designationDefName}");
            if (map.designationManager.DesignationAt(cell, def) != null) return CommandResult.Ok();
            if (def.defName == MineDefName && cell.GetFirstMineable(map) == null) return CommandResult.Fail("nothing to mine there");

            map.designationManager.AddDesignation(new Designation(cell, def));
            return CommandResult.Ok();
        }

        public CommandResult AddDesignationOnThing(int thingId, string designationDefName)
        {
            Map map = Map;
            Thing thing = FindThing(thingId);
            if (thing == null) return CommandResult.Fail("object not found on the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            if (def == null) return CommandResult.Fail($"unknown designation {designationDefName}");
            if (map.designationManager.DesignationOn(thing, def) != null) return CommandResult.Ok();

            string blocked = WhyNotDesignatable(thing, def);
            if (blocked != null) return CommandResult.Fail(blocked);

            map.designationManager.AddDesignation(new Designation(thing, def));
            return CommandResult.Ok();
        }

        public CommandResult RemoveDesignationOnCell(int x, int z, string designationDefName)
        {
            Map map = Map;
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            Designation existing = def == null ? null : map.designationManager.DesignationAt(cell, def);
            if (existing == null) return CommandResult.Fail("no such designation there");
            map.designationManager.RemoveDesignation(existing);
            return CommandResult.Ok();
        }

        public CommandResult RemoveDesignationOnThing(int thingId, string designationDefName)
        {
            Map map = Map;
            Thing thing = FindThing(thingId);
            if (thing == null) return CommandResult.Fail("object not found on the map");
            DesignationDef def = DefDatabase<DesignationDef>.GetNamedSilentFail(designationDefName);
            Designation existing = def == null ? null : map.designationManager.DesignationOn(thing, def);
            if (existing == null) return CommandResult.Fail("no such designation on it");
            map.designationManager.RemoveDesignation(existing);
            return CommandResult.Ok();
        }

        public CommandResult TryPlaceBlueprint(string buildableDefName, int x, int z, int rotation, string stuffDefName)
        {
            Map map = Map;
            BuildableDef def = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(buildableDefName)
                               ?? DefDatabase<TerrainDef>.GetNamedSilentFail(buildableDefName);
            if (def == null) return CommandResult.Fail($"unknown buildable {buildableDefName}");
            if (!def.BuildableByPlayer) return CommandResult.Fail($"{def.label} cannot be built by the player");
            if (!def.IsResearchFinished) return CommandResult.Fail($"{def.label} requires research that is not finished");

            ThingDef stuff = null;
            if (def.MadeFromStuff)
            {
                stuff = string.IsNullOrEmpty(stuffDefName)
                    ? GenStuff.DefaultStuffFor(def)
                    : DefDatabase<ThingDef>.GetNamedSilentFail(stuffDefName);
                if (stuff == null) return CommandResult.Fail($"unknown material {stuffDefName}");
                if (!GenStuff.AllowedStuffsFor(def).Contains(stuff)) return CommandResult.Fail($"{def.label} cannot be made of {stuff.label}");
            }

            var cell = new IntVec3(x, 0, z);
            var rot = new Rot4(rotation);
            AcceptanceReport report = GenConstruct.CanPlaceBlueprintAt(def, cell, rot, map, false, null, null, stuff);
            if (!report.Accepted) return CommandResult.Fail(string.IsNullOrEmpty(report.Reason) ? "cannot place there" : report.Reason);

            GenConstruct.PlaceBlueprintForBuild(def, cell, map, rot, Faction.OfPlayer, stuff);
            return CommandResult.Ok();
        }

        public CommandResult SetDrafted(int pawnId, bool drafted)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            if (pawn.drafter == null) return CommandResult.Fail($"{pawn.LabelShort} cannot be drafted");
            if (pawn.Downed && drafted) return CommandResult.Fail($"{pawn.LabelShort} is downed");
            pawn.drafter.Drafted = drafted;
            return CommandResult.Ok();
        }

        public CommandResult OrderMoveTo(int pawnId, int x, int z)
        {
            Map map = Map;
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            if (!pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is not drafted");
            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map)) return CommandResult.Fail("cell is outside the map");
            if (!cell.Standable(map)) return CommandResult.Fail("cell is not standable");
            if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return CommandResult.Fail($"{pawn.LabelShort} cannot reach that cell");

            Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.playerForced = true;
            return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
                ? CommandResult.Ok()
                : CommandResult.Fail($"{pawn.LabelShort} refused the move order");
        }

        public CommandResult OrderAttack(int pawnId, int targetId)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            if (!pawn.Drafted) return CommandResult.Fail($"{pawn.LabelShort} is not drafted");
            Thing target = FindThing(targetId);
            if (target == null) return CommandResult.Fail("target not found on the map");

            bool ranged = pawn.equipment?.Primary?.def.IsRangedWeapon == true;
            Job job = JobMaker.MakeJob(ranged ? JobDefOf.AttackStatic : JobDefOf.AttackMelee, target);
            job.playerForced = true;
            return pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)
                ? CommandResult.Ok()
                : CommandResult.Fail($"{pawn.LabelShort} refused the attack order");
        }

        // ---- 轉換 ----

        private static ColonistInfo ToColonist(Pawn pawn)
        {
            var triple = pawn.Name as NameTriple;
            string nick = triple?.Nick ?? string.Empty;
            string first = triple?.First ?? pawn.Name?.ToStringShort ?? pawn.LabelShort;
            bool hasDistinctNick = !string.IsNullOrEmpty(nick) && nick != first;

            return new ColonistInfo
            {
                Id = pawn.thingIDNumber,
                Name = pawn.LabelShort,
                Nickname = hasDistinctNick ? nick : string.Empty,
                FullName = pawn.Name?.ToStringFull ?? pawn.LabelShort,
                Age = pawn.ageTracker?.AgeBiologicalYears ?? 0,
                X = pawn.Position.x,
                Z = pawn.Position.z,
                HealthPercent = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f,
                Downed = pawn.Downed,
                MoodPercent = pawn.needs?.mood?.CurLevelPercentage ?? 0f,
                MinorBreakThreshold = pawn.mindState?.mentalBreaker?.BreakThresholdMinor ?? 0f,
                InMentalState = pawn.InMentalState,
                MentalStateLabel = pawn.MentalStateDef?.label ?? string.Empty,
                CurrentActivity = SafeReport(pawn),
                Drafted = pawn.Drafted,
                Skills = pawn.skills?.skills.Select(s => new SkillInfo
                {
                    DefName = s.def.defName,
                    Label = s.def.label,
                    Level = s.Level,
                    Passion = (int)s.passion,
                    Disabled = s.TotallyDisabled
                }).ToList() ?? (IReadOnlyList<SkillInfo>)Array.Empty<SkillInfo>(),
                WorkPriorities = pawn.workSettings == null
                    ? (IReadOnlyList<WorkPriorityInfo>)Array.Empty<WorkPriorityInfo>()
                    : DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).Select(w => new WorkPriorityInfo
                    {
                        WorkTypeDefName = w.defName,
                        Label = w.labelShort ?? w.label,
                        Priority = pawn.WorkTypeIsDisabled(w) ? 0 : pawn.workSettings.GetPriority(w),
                        Disabled = pawn.WorkTypeIsDisabled(w)
                    }).ToList(),
                Timetable = pawn.timetable == null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : Enumerable.Range(0, 24).Select(h => pawn.timetable.GetAssignment(h)?.defName ?? string.Empty).ToList()
            };
        }

        private static string SafeReport(Pawn pawn)
        {
            try
            {
                return pawn.jobs?.curDriver?.GetReport() ?? pawn.CurJob?.def.label ?? string.Empty;
            }
            catch (Exception)
            {
                // 某些 JobDriver 的 GetReport 會在狀態不完整時丟例外；活動描述只是輔助資訊。
                return pawn.CurJob?.def.label ?? string.Empty;
            }
        }

        private static bool IsNearMentalBreak(Pawn pawn)
        {
            Need_Mood mood = pawn.needs?.mood;
            MentalBreaker breaker = pawn.mindState?.mentalBreaker;
            return mood != null && breaker != null && mood.CurLevel < breaker.BreakThresholdMinor;
        }

        private static ResearchProjectInfo ToResearch(ResearchProjectDef def)
        {
            return new ResearchProjectInfo
            {
                DefName = def.defName,
                Label = def.label,
                ProgressPercent = def.ProgressPercent,
                Cost = def.CostApparent
            };
        }

        private static ThingKind? KindOf(Thing thing)
        {
            if (thing is Pawn) return ThingKind.Pawn;
            if (thing is Plant) return ThingKind.Plant;
            if (thing.def.mineable) return ThingKind.Mineable;
            if (thing is Building) return ThingKind.Building;
            if (thing.def.category == ThingCategory.Item) return ThingKind.Item;
            return null;
        }

        private static ThingInfo ToThing(Thing thing, Map map)
        {
            var designations = map.designationManager.AllDesignationsOn(thing).Select(d => d.def.defName).ToList();
            if (thing.def.mineable)
            {
                designations.AddRange(map.designationManager.AllDesignationsAt(thing.Position).Select(d => d.def.defName));
            }
            return new ThingInfo
            {
                Id = thing.thingIDNumber,
                DefName = thing.def.defName,
                Label = thing.LabelCap,
                Kind = KindOf(thing) ?? ThingKind.Any,
                X = thing.Position.x,
                Z = thing.Position.z,
                StackCount = thing.stackCount,
                PlayerOwned = thing.Faction == Faction.OfPlayer,
                Harvestable = (thing as Plant)?.HarvestableNow ?? false,
                Designations = designations.Distinct().ToList()
            };
        }

        private static string WhyNotDesignatable(Thing thing, DesignationDef def)
        {
            switch (def.defName)
            {
                case "CutPlant":
                case "HarvestPlant":
                    return thing is Plant ? null : $"{thing.LabelCap} is not a plant";
                case "Deconstruct":
                    if (!(thing is Building)) return $"{thing.LabelCap} is not a building";
                    if (thing.Faction != Faction.OfPlayer) return $"{thing.LabelCap} does not belong to the colony";
                    if (thing.def.building != null && !thing.def.building.IsDeconstructible) return $"{thing.LabelCap} cannot be deconstructed";
                    return null;
                case "Haul":
                    return thing.def.EverHaulable ? null : $"{thing.LabelCap} cannot be hauled";
                default:
                    return null;
            }
        }

        private static Pawn FindColonist(int id)
        {
            return Map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.thingIDNumber == id);
        }

        private static Thing FindThing(int id)
        {
            return Map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id && t.Spawned);
        }
    }
}
