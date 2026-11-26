using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimAgent.Game;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimAgent.InGameTests;

/// <summary>
/// 技能題：指令只描述情況，正確做法只寫在 AgentPack/Skills 的技能裡（判分條件註明出自哪一節），
/// 只在比較開、關技能時跑。
/// </summary>
public sealed partial class BenchmarkComponent
{
    private const float NutritionPerColonistDay = 1.6f;
    private const int RunwayDays = 3;

    private static Pawn _bleeder;
    private static Pawn _scratched;
    private static CellRect _shelter;
    private static readonly List<IntVec3> _shelterRoof = [];
    private static Pawn _newcomer;
    private static WorkTypeDef _newcomerCraft;
    private static readonly List<(Thing Thing, IntVec3 Cell)> _storedFood = [];
    private static float _runwayDays;
    private static Pawn _unhappy;
    private static List<Thought_Memory> _savedMemories = [];

    /// <summary>medical-triage 第 3 節：大量出血用工業藥以上、擦傷用草藥；第 2 節：由別人替出血的人治療。</summary>
    private static BenchTask SkillTriageTask() =>
        new()
        {
            Id = "skill_triage",
            Tier = SkillTier,
            Skill = "medical-triage",
            Instruction = "有殖民者受傷了，處理一下。",
            Setup = map =>
            {
                List<Pawn> colonists = [.. map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && p.playerSettings != null)];
                Pawn doctor = colonists.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)).MaxBy(MedicineLevel);
                if (colonists.Count < 2 || doctor == null) return "needs two colonists and a doctor";
                // 醫術最好的人不能是出血者，否則沒人能替他治療。
                colonists.Remove(doctor);
                colonists.Sort((a, b) => MedicineLevel(a).CompareTo(MedicineLevel(b)));
                _bleeder = colonists[0];
                _scratched = colonists.Count > 1 ? colonists[1] : doctor;
                BodyPartRecord torso = _bleeder.RaceProps.body.corePart;
                for (int i = 0; i < 3; i++)
                {
                    Hediff cut = HediffMaker.MakeHediff(HediffDefOf.Cut, _bleeder, torso);
                    cut.Severity = 6f;
                    _bleeder.health.AddHediff(cut, torso);
                }
                HediffDef scratchDef = DefDatabase<HediffDef>.GetNamedSilentFail("Scratch") ?? HediffDefOf.Cut;
                Hediff scratch = HediffMaker.MakeHediff(scratchDef, _scratched, _scratched.RaceProps.body.corePart);
                scratch.Severity = 1f;
                _scratched.health.AddHediff(scratch, _scratched.RaceProps.body.corePart);
                // 起點刻意顛倒，什麼都不改不會過。
                _bleeder.playerSettings.medCare = MedicalCareCategory.HerbalOrWorse;
                _scratched.playerSettings.medCare = MedicalCareCategory.Best;
                return null;
            },
            Cleanup = map =>
            {
                foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.Where(p => p.CurJobDef == JobDefOf.TendPatient).ToList()) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                _bleeder = null;
                _scratched = null;
            },
            Measure = map => new()
            {
                ["bleederCare"] = _bleeder?.playerSettings.medCare >= MedicalCareCategory.NormalOrWorse ? 1 : 0,
                ["scratchCare"] = _scratched?.playerSettings.medCare <= MedicalCareCategory.HerbalOrWorse ? 1 : 0,
                ["tended"] = map.mapPawns.FreeColonistsSpawned.Any(p => p != _bleeder && Jobs(p).Any(j => j.def == JobDefOf.TendPatient && j.targetA.Thing == _bleeder)) ? 1 : 0
            },
            Check = (_, after, _) => Fails(
                after["bleederCare"] == 1 ? null : $"{_bleeder?.LabelShort} (bleeding heavily) is not on industrial medicine or better",
                after["scratchCare"] == 1 ? null : $"{_scratched?.LabelShort} (a scratch) is not on herbal medicine or worse",
                after["tended"] == 1 ? null : $"nobody else was ordered to tend {_bleeder?.LabelShort}"),
            Solve = world =>
            {
                Pawn doctor = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Where(p => p != _bleeder && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)).MaxBy(MedicineLevel);
                return All([
                    world.SetMedicalCare(_bleeder.thingIDNumber, nameof(MedicalCareCategory.NormalOrWorse)),
                    world.SetMedicalCare(_scratched.thingIDNumber, nameof(MedicalCareCategory.HerbalOrWorse)),
                    world.OrderJob(doctor.thingIDNumber, "tend", _bleeder.thingIDNumber)]);
            }
        };

    /// <summary>raid-defense 第 2 節：發狂的動物要躲進室內，不要迎戰。</summary>
    private static BenchTask SkillManhunterTask() =>
        new()
        {
            Id = "skill_manhunter",
            Tier = SkillTier,
            Skill = "raid-defense",
            Instruction = "地圖邊緣出現了三頭發狂的野豬，正要往殖民地衝過來。處理一下。",
            Setup = map => BuildShelter(map) ?? SpawnEnemies(map),
            Cleanup = map =>
            {
                RemoveEnemies(map);
                DestroySpawnedThings();
                foreach (IntVec3 cell in _shelterRoof) map.roofGrid.SetRoof(cell, null);
                _shelterRoof.Clear();
            },
            Measure = map => new()
            {
                ["attacking"] = map.mapPawns.FreeColonistsSpawned.Count(IsAttackingEnemy),
                ["outside"] = map.mapPawns.FreeColonistsSpawned.Count(p => !p.Downed && !HeadingInto(p, _shelter.ContractedBy(1)))
            },
            Check = (_, after, _) => Fails(
                after["attacking"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["attacking"]} colonist(s) ordered to attack manhunting animals"),
                after["outside"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["outside"]} colonist(s) not in or heading into the roofed room")),
            Solve = world =>
            {
                List<IntVec3> inside = [.. _shelter.ContractedBy(1).Cells];
                List<Pawn> colonists = [.. Find.CurrentMap.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed)];
                return All(colonists.SelectMany((p, i) => new[]
                {
                    world.SetDrafted(p.thingIDNumber, drafted: true),
                    world.OrderMoveTo(p.thingIDNumber, inside[i].x, inside[i].z)
                }));
            }
        };

    /// <summary>work-assignment 第 1、2 節：滅火與臥床休養都是 1，專長設 1，其餘設 1 的工作不超過兩項。</summary>
    private static BenchTask SkillNewColonistTask()
    {
        var task = new BenchTask
        {
            Id = "skill_new_colonist",
            Tier = SkillTier,
            Skill = "work-assignment",
            Instruction = "有一位新殖民者剛加入。幫這位殖民者排工作優先級。",
            Cleanup = _ => DestroySpawned(ref _newcomer),
            Measure = _ => new()
            {
                ["firefighter"] = Priority(_newcomer, WorkTypeDefOf.Firefighter) == 1 ? 1 : 0,
                ["bedRest"] = Priority(_newcomer, BedRest) == 1 ? 1 : 0,
                ["craft"] = Priority(_newcomer, _newcomerCraft) == 1 ? 1 : 0,
                ["otherOnes"] = DefDatabase<WorkTypeDef>.AllDefs.Count(w => w != WorkTypeDefOf.Firefighter && w != BedRest && Priority(_newcomer, w) == 1)
            },
            Check = (_, after, _) => Fails(
                after["firefighter"] == 1 ? null : "firefighting is not 1",
                after["bedRest"] == 1 ? null : "patient/bed rest is not 1",
                after["craft"] == 1 ? null : $"{_newcomerCraft?.labelShort} (skill 14, major passion) is not 1",
                after["otherOnes"] <= 2 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["otherOnes"]} other jobs at 1 (max 2)")),
            Solve = world => All(new[] { WorkTypeDefOf.Firefighter, BedRest, _newcomerCraft }
                .Select(w => world.SetWorkPriority(_newcomer.thingIDNumber, w.defName, 1)))
        };
        task.Setup = map =>
        {
            _newcomer = null;
            _newcomerCraft = WorkTypeDefOf.Construction;
            for (int attempt = 0; attempt < 20 && _newcomer == null; attempt++)
            {
                // 不產生親屬：丟掉的候選人才不會留下和現有殖民者的關係。
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, canGeneratePawnRelations: false));
                if (new[] { WorkTypeDefOf.Firefighter, BedRest, _newcomerCraft }.All(w => !pawn.WorkTypeIsDisabled(w))) _newcomer = pawn;
                else Find.WorldPawns.PassToWorld(pawn, RimWorld.Planet.PawnDiscardDecideMode.Discard);
            }
            if (_newcomer == null) return "could not generate a colonist who can build and fight fires";
            foreach (SkillRecord skill in _newcomer.skills.skills)
            {
                skill.Level = 3;
                skill.passion = Passion.None;
            }
            SkillRecord construction = _newcomer.skills.GetSkill(SkillDefOf.Construction);
            construction.Level = 14;
            construction.passion = Passion.Major;
            GenSpawn.Spawn(_newcomer, NearColonists(map), map);
            Find.PlaySettings.useWorkPriorities = true;
            _newcomer.workSettings.EnableAndInitialize();
            foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefs.Where(w => !_newcomer.WorkTypeIsDisabled(w))) _newcomer.workSettings.SetPriority(work, 3);
            task.Instruction = $"新殖民者 {_newcomer.LabelShort} 剛加入。幫這位殖民者排工作優先級。";
            return null;
        };
        return task;
    }

    /// <summary>food-security：用技能的公式算出存糧天數，不到 5 天要補食物來源。</summary>
    private static BenchTask SkillFoodRunwayTask() =>
        new()
        {
            Id = "skill_food_runway",
            Tier = SkillTier,
            Skill = "food-security",
            Instruction = "食物還夠嗎？告訴我大概還能撐幾天，不夠的話想辦法補。",
            Setup = map =>
            {
                _storedFood.Clear();
                foreach (Thing food in map.haulDestinationManager.AllGroupsListForReading.SelectMany(g => g.HeldThings).Where(t => t.def.IsNutritionGivingIngestible && t.def.ingestible.HumanEdible).ToList())
                {
                    _storedFood.Add((food, food.Position));
                    food.DeSpawn();
                }
                if (!AddStockpile(map, "食物倉庫")) return "no room for a food stockpile";
                Zone_Stockpile pantry = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Last(z => z.label == "食物倉庫");
                int colonists = map.mapPawns.FreeColonistsSpawnedCount;
                float nutrition = ThingDefOf.MealSimple.GetStatValueAbstract(StatDefOf.Nutrition);
                int meals = (int)System.Math.Ceiling(RunwayDays * colonists * NutritionPerColonistDay / nutrition);
                foreach (IntVec3 cell in pantry.Cells)
                {
                    if (meals <= 0) break;
                    Thing stack = Stack(ThingDefOf.MealSimple, System.Math.Min(meals, ThingDefOf.MealSimple.stackLimit));
                    meals -= stack.stackCount;
                    _spawned.Add(GenSpawn.Spawn(stack, cell, map));
                }
                if (meals > 0) return "the food stockpile is too small";
                map.resourceCounter.UpdateResourceCounts();
                _runwayDays = map.resourceCounter.TotalHumanEdibleNutrition / (colonists * NutritionPerColonistDay);
                return null;
            },
            Cleanup = map =>
            {
                DestroySpawnedThings();
                foreach ((Thing food, IntVec3 cell) in _storedFood.Where(f => !f.Thing.Destroyed && !f.Thing.Spawned)) GenSpawn.Spawn(food, cell, map);
                _storedFood.Clear();
            },
            Measure = map => new()
            {
                ["growing"] = ZoneCells<Zone_Growing>(map, _ => true),
                ["hunting"] = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Hunt).Count(),
                ["bills"] = map.listerBuildings.allBuildingsColonist.OfType<IBillGiver>().Sum(b => b.BillStack.Count),
                ["workbenches"] = CountBuildTargets(map, d => d.IsWorkTable)
            },
            Check = (before, after, reply) => Fails(
                NamesRunway(reply) ? null : string.Create(CultureInfo.InvariantCulture, $"reply does not give about {_runwayDays:0.#} days"),
                new[] { "growing", "hunting", "bills", "workbenches" }.Any(k => Delta(before, after, k) > 0) ? null : "no new food source (field, hunting, cooking bill or workbench)")
        };

    /// <summary>mood-management 第 3 節：沒桌子吃飯要放桌子。</summary>
    private static BenchTask SkillMoodTask()
    {
        var task = new BenchTask
        {
            Id = "skill_mood",
            Tier = SkillTier,
            Skill = "mood-management",
            Instruction = "有一位殖民者最近心情很差。找出原因並解決。",
            Measure = map => new() { ["tables"] = CountBuildTargets(map, d => d.IsTable) },
            Check = (before, after, reply) => Fails(
                Need(after, before, "tables", 1),
                reply != null && (reply.Contains('桌') || reply.Contains("table", System.StringComparison.OrdinalIgnoreCase)) ? null : "reply does not name the missing table")
        };
        task.Setup = map =>
        {
            if (map.listerBuildings.allBuildingsColonist.Any(b => b.def.IsTable)) return "the colony already has a table";
            _unhappy = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.needs?.mood != null);
            if (_unhappy == null) return "no colonist has a mood";
            // 只留這一個記憶，原因才不會有爭議。
            List<Thought_Memory> memories = _unhappy.needs.mood.thoughts.memories.Memories;
            _savedMemories = [.. memories];
            memories.Clear();
            _unhappy.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.AteWithoutTable);
            task.Instruction = $"{_unhappy.LabelShort} 最近心情很差。找出原因並解決。";
            return null;
        };
        task.Cleanup = _ =>
        {
            if (_unhappy?.needs?.mood == null) return;
            List<Thought_Memory> memories = _unhappy.needs.mood.thoughts.memories.Memories;
            memories.Clear();
            memories.AddRange(_savedMemories);
            _savedMemories = [];
            _unhappy = null;
        };
        return task;
    }

    /// <summary>work-assignment 第 4 節：約 22 點到 6 點睡、晚上一兩小時娛樂。</summary>
    private static BenchTask SkillScheduleTask() =>
        new()
        {
            Id = "skill_schedule",
            Tier = SkillTier,
            Skill = "work-assignment",
            Instruction = "幫大家排一個正常的作息。",
            Setup = ResetSchedules,
            Measure = map => new() { ["off"] = map.mapPawns.FreeColonistsSpawned.Count(p => p.timetable != null && !BaselineDay(p.timetable)) },
            Check = (_, after, _) => after["off"] == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{after["off"]} colonist(s) without about 22-6 sleep and 1-2 evening joy hours"),
            Solve = world => All(Find.CurrentMap.mapPawns.FreeColonistsSpawned.SelectMany(p => Enumerable.Range(0, 24).Select(h =>
                world.SetTimeAssignment(p.thingIDNumber, h, BaselineHour(h).defName))))
        };

    private static WorkTypeDef BedRest => DefDatabase<WorkTypeDef>.GetNamed("PatientBedRest");

    private static TimeAssignmentDef BaselineHour(int hour)
    {
        if (hour < 6 || hour >= 22) return TimeAssignmentDefOf.Sleep;
        return hour >= 20 ? TimeAssignmentDefOf.Joy : TimeAssignmentDefOf.Anything;
    }

    /// <summary>睡 7 到 9 小時且涵蓋 0 到 5 點，娛樂 1 到 2 小時且都在 17 點以後。</summary>
    private static bool BaselineDay(Pawn_TimetableTracker timetable)
    {
        List<int> sleep = [.. Enumerable.Range(0, 24).Where(h => timetable.GetAssignment(h) == TimeAssignmentDefOf.Sleep)];
        List<int> joy = [.. Enumerable.Range(0, 24).Where(h => timetable.GetAssignment(h) == TimeAssignmentDefOf.Joy)];
        return sleep.Count is >= 7 and <= 9 && Enumerable.Range(0, 6).All(sleep.Contains) && joy.Count is >= 1 and <= 2 && joy.All(h => h >= 17);
    }

    /// <summary>回覆裡接在「天」旁的數字和技能公式算出的天數差不到 1 天。</summary>
    private static bool NamesRunway(string reply) =>
        BenchmarkScoring.Days(reply).Any(days => System.Math.Abs(days - _runwayDays) <= 1f);

    private static int? Priority(Pawn pawn, WorkTypeDef work) =>
        pawn == null || work == null || pawn.WorkTypeIsDisabled(work) ? null : pawn.workSettings.GetPriority(work);

    private static IEnumerable<Job> Jobs(Pawn pawn) =>
        new[] { pawn.CurJob }.Concat(pawn.jobs?.jobQueue?.Select(q => q.job) ?? []).Where(j => j != null);

    /// <summary>已經在 <paramref name="room"/> 裡，或正在走過去。</summary>
    private static bool HeadingInto(Pawn pawn, CellRect room) =>
        room.Contains(pawn.Position) || Jobs(pawn).Any(j => j.def == JobDefOf.Goto && room.Contains(j.targetA.Cell));

    /// <summary>在殖民者附近蓋一間內部 5x5、有門有屋頂的房間。</summary>
    private static string BuildShelter(Map map)
    {
        _spawned.Clear();
        IntVec3 center = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
        foreach (IntVec3 corner in GenRadial.RadialCellsAround(center, 25f, useCenter: false))
        {
            var rect = new CellRect(corner.x, corner.z, 7, 7);
            if (!rect.InBounds(map) || !rect.Cells.All(c => IsOpen(map, c) && c.GetThingList(map).Count == 0)) continue;
            _shelter = rect;
            IntVec3 door = new(rect.minX + 3, 0, rect.minZ);
            foreach (IntVec3 cell in rect.EdgeCells)
            {
                Thing part = cell == door ? ThingMaker.MakeThing(ThingDefOf.Door, ThingDefOf.WoodLog) : ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                part.SetFaction(Faction.OfPlayer);
                _spawned.Add(GenSpawn.Spawn(part, cell, map));
            }
            foreach (IntVec3 cell in rect.ContractedBy(1))
            {
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                _shelterRoof.Add(cell);
            }
            return null;
        }
        return "no room for a 7x7 shelter near the colonists";
    }
}
