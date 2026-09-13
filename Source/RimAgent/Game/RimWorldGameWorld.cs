using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

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
        private IReadOnlyList<DefInfo> _tradeGoods;
        private IReadOnlyList<DefInfo> _medicalCareLevels;
        private IReadOnlyList<DefInfo> _surgeryRecipes;

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

        public IReadOnlyList<ColonistHealthInfo> GetHealth()
        {
            return Map.mapPawns.FreeColonistsSpawned.Select(ToHealth).ToList();
        }

        private static ColonistHealthInfo ToHealth(Pawn pawn)
        {
            var conditions = new List<HealthConditionInfo>();
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                string kind = hediff is Hediff_MissingPart ? HealthConditionInfo.MissingKind
                    : hediff is Hediff_Injury ? HealthConditionInfo.InjuryKind
                    : HealthConditionInfo.ConditionKind;
                bool tended = false, needsTending = false;
                if (hediff.def.tendable)
                {
                    HediffComp_TendDuration tend = (hediff as HediffWithComps)?.TryGetComp<HediffComp_TendDuration>();
                    tended = tend != null && tend.IsTended;
                    needsTending = !tended && hediff.TendableNow(false);
                }
                conditions.Add(new HealthConditionInfo
                {
                    Label = hediff.LabelCap,
                    Part = hediff.Part != null ? hediff.Part.LabelCap : string.Empty,
                    Severity = hediff.Severity,
                    Kind = kind,
                    Bleeding = hediff.BleedRate > 0f,
                    Tended = tended,
                    NeedsTending = needsTending
                });
            }
            List<SurgeryBillInfo> surgeries = pawn.BillStack.Bills.OfType<Bill_Medical>().Select(b => new SurgeryBillInfo
            {
                Label = b.LabelCap,
                Part = b.Part != null ? b.Part.LabelCap : string.Empty,
                Suspended = b.suspended
            }).ToList();
            return new ColonistHealthInfo { PawnId = pawn.thingIDNumber, Conditions = conditions, PendingSurgeries = surgeries };
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

        public IReadOnlyList<TraderInfo> GetTraders()
        {
            Map map = Map;
            var result = new List<TraderInfo>();
            foreach (TradeShip ship in map.passingShipManager.passingShips.OfType<TradeShip>())
            {
                if (!ship.CanTradeNow) continue;
                var goods = new List<TradeGoodsInfo>();
                foreach (Thing t in ship.Goods)
                {
                    if (t == null || t.Destroyed) continue;
                    if (t.def == ThingDefOf.Silver) continue;
                    Pawn pawn = t as Pawn;
                    goods.Add(new TradeGoodsInfo
                    {
                        DefName = t.def.defName,
                        Label = pawn != null ? pawn.LabelShort : t.def.label,
                        Count = Math.Max(1, t.stackCount),
                        MarketValue = t.MarketValue
                    });
                }
                result.Add(new TraderInfo
                {
                    Kind = TraderInfo.OrbitalKind,
                    Id = ship.GetUniqueLoadID(),
                    Name = ship.FullTitle,
                    Faction = ship.Faction?.Name ?? string.Empty,
                    Silver = ship.Silver,
                    Goods = goods
                        .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(g => g.DefName, StringComparer.Ordinal)
                        .ToList()
                });
            }
            // 來訪商隊（Visitor）：同一 Lord 共用整隊庫存，只列一次；不拿軌道商人頂替。
            // CanTradeNow 已排除死亡、倒地、俘虜、玩家派系與敵對派系，只剩地圖歸屬要自己檢查。
            foreach (IGrouping<object, Pawn> group in map.mapPawns.AllPawnsSpawned
                         .Where(p => p.Map == map && p.trader != null && p.trader.traderKind != null && p.trader.CanTradeNow)
                         .GroupBy(p => (object)p.GetLord() ?? p))
            {
                Pawn leader = group.First();
                var goods = new List<TradeGoodsInfo>();
                int silver = 0;
                foreach (Thing t in leader.trader.Goods)
                {
                    if (t == null || t.Destroyed) continue;
                    if (t.def == ThingDefOf.Silver)
                    {
                        silver += t.stackCount;
                        continue;
                    }
                    Pawn goodsPawn = t as Pawn;
                    goods.Add(new TradeGoodsInfo
                    {
                        DefName = t.def.defName,
                        Label = goodsPawn != null ? goodsPawn.LabelShort : t.def.label,
                        Count = Math.Max(1, t.stackCount),
                        MarketValue = t.MarketValue
                    });
                }
                result.Add(new TraderInfo
                {
                    Kind = TraderInfo.VisitorKind,
                    Id = leader.GetUniqueLoadID(),
                    Name = leader.LabelShort + " (" + leader.trader.traderKind.label + ")",
                    Faction = leader.Faction?.Name ?? string.Empty,
                    Silver = silver,
                    Goods = goods
                        .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(g => g.DefName, StringComparer.Ordinal)
                        .ToList()
                });
            }
            return result;
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

        public IReadOnlyList<DefInfo> TradeGoods =>
            _tradeGoods ?? (_tradeGoods = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.category == ThingCategory.Item)
                .Select(d => new DefInfo(d.defName, d.label))
                .ToList());

        public IReadOnlyList<DefInfo> MedicalCareLevels =>
            _medicalCareLevels ?? (_medicalCareLevels = Enum.GetValues(typeof(MedicalCareCategory))
                .Cast<MedicalCareCategory>()
                .Select(c => new DefInfo(c.ToString(), MedicalCareUtility.GetLabel(c)))
                .ToList());

        public CommandResult SetMedicalCare(int pawnId, string careDefName)
        {
            Pawn pawn = FindColonist(pawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            if (pawn.playerSettings == null) return CommandResult.Fail($"{pawn.LabelShort} has no medical care settings");
            object parsed;
            try
            {
                parsed = Enum.Parse(typeof(MedicalCareCategory), careDefName, true);
            }
            catch (ArgumentException)
            {
                return CommandResult.Fail($"unknown medical care level {careDefName}");
            }
            if (!Enum.IsDefined(typeof(MedicalCareCategory), parsed)) return CommandResult.Fail($"unknown medical care level {careDefName}");
            pawn.playerSettings.medCare = (MedicalCareCategory)parsed;
            return CommandResult.Ok();
        }

        public IReadOnlyList<DefInfo> SurgeryRecipes =>
            _surgeryRecipes ?? (_surgeryRecipes = DefDatabase<RecipeDef>.AllDefsListForReading
                .Where(r => r.workerClass != null && typeof(Recipe_Surgery).IsAssignableFrom(r.workerClass))
                .Select(r => new DefInfo(r.defName, r.label))
                .ToList());

        public CommandResult ScheduleSurgery(SurgeryRequest request)
        {
            Pawn pawn = FindColonist(request.PawnId);
            if (pawn == null) return CommandResult.Fail("colonist not found");
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(request.RecipeDefName);
            if (recipe == null || recipe.workerClass == null || !typeof(Recipe_Surgery).IsAssignableFrom(recipe.workerClass))
                return CommandResult.Fail($"unknown surgery {request.RecipeDefName}");
            if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished)
                return CommandResult.Fail($"{recipe.label} needs research {recipe.researchPrerequisite.label} first");
            if (recipe.researchPrerequisites != null)
            {
                foreach (ResearchProjectDef prerequisite in recipe.researchPrerequisites)
                {
                    if (prerequisite != null && !prerequisite.IsFinished)
                        return CommandResult.Fail($"{recipe.label} needs research {prerequisite.label} first");
                }
            }
            BodyPartRecord part = null;
            List<BodyPartRecord> options = recipe.Worker.GetPartsToApplyOn(pawn, recipe).ToList();
            if (recipe.targetsBodyPart)
            {
                if (options.Count == 0) return CommandResult.Fail($"{recipe.label} does not apply to {pawn.LabelShort}");
                string want = (request.BodyPartLabel ?? string.Empty).Trim();
                part = want.Length == 0 ? options[0] : options.FirstOrDefault(p => MatchesBodyPart(p, want));
                if (part == null) return CommandResult.Fail($"{pawn.LabelShort} has no body part matching \"{want}\" for {recipe.label}");
            }
            if (pawn.BillStack.Bills.OfType<Bill_Medical>().Any(b => b.recipe == recipe && b.Part == part))
                return CommandResult.Fail($"{recipe.label} is already scheduled on {pawn.LabelShort}");
            var bill = new Bill_Medical(recipe, null) { Part = part };
            pawn.BillStack.AddBill(bill);
            string detail = $"Scheduled {recipe.label} on {pawn.LabelShort}";
            if (part != null) detail += $" ({part.LabelCap})";
            return CommandResult.Ok(detail + ".");
        }

        private static bool MatchesBodyPart(BodyPartRecord part, string want)
        {
            return string.Equals(part.LabelCap, want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(part.Label, want, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(part.customLabel) && string.Equals(part.customLabel, want, StringComparison.OrdinalIgnoreCase))
                || (part.def != null && string.Equals(part.def.label, want, StringComparison.OrdinalIgnoreCase));
        }

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

        public CommandResult ExecuteTrade(TradeRequest request)
        {
            if (request == null) return CommandResult.Fail("no trade requested");
            if (string.IsNullOrEmpty(request.TraderId)) return CommandResult.Fail("no trader specified");
            IReadOnlyList<TradeLine> buys = request.Buys ?? Array.Empty<TradeLine>();
            IReadOnlyList<TradeLine> sells = request.Sells ?? Array.Empty<TradeLine>();
            if (buys.Count == 0 && sells.Count == 0) return CommandResult.Fail("nothing to buy or sell");
            foreach (TradeLine line in buys.Concat(sells))
            {
                if (string.Equals(line.DefName, ThingDefOf.Silver.defName, StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("silver moves automatically as payment; do not list it");
                if (line.Count <= 0) return CommandResult.Fail($"invalid count {line.Count} for {line.DefName}");
            }

            // 玩家手動開著交易窗時不搶 TradeSession，否則對方的結算會被掀掉。
            if (TradeSession.Active) return CommandResult.Fail("a trade window is already open. Close it first.");

            ITrader trader = FindTrader(request.TraderId);
            if (trader == null) return CommandResult.Fail($"trader \"{request.TraderId}\" was not found. List the traders again to get a valid id.");
            if (!trader.CanTradeNow) return CommandResult.Fail($"{TraderLabel(trader)} cannot trade right now.");
            if (trader.TradeCurrency != TradeCurrency.Silver) return CommandResult.Fail($"{TraderLabel(trader)} does not trade in silver.");

            Pawn negotiator = FindColonist(request.NegotiatorPawnId);
            if (negotiator == null) return CommandResult.Fail("negotiator was not found among the colonists.");
            if (negotiator.Downed) return CommandResult.Fail($"{negotiator.LabelShort} is downed and cannot negotiate.");

            // 之後全走遊戲的原生結算：價格、銀兩與物品交割都由 TradeDeal 處理，
            // 這裡只做「先驗證全部、再一次交割」，餘額或庫存不足整筆不做。
            TradeSession.SetupWith(trader, negotiator, false);
            try
            {
                TradeDeal deal = TradeSession.deal;
                // 買賣分開追蹤：同 def 可能兩邊都有貨而併在同一行，淨額結算。
                var buyAllocations = new Dictionary<Tradeable, int>();
                var sellAllocations = new Dictionary<Tradeable, int>();
                foreach (TradeLine line in buys)
                {
                    string fail = Allocate(buyAllocations, deal, line, true, TraderLabel(trader));
                    if (fail != null) return CommandResult.Fail(fail);
                }
                foreach (TradeLine line in sells)
                {
                    string fail = Allocate(sellAllocations, deal, line, false, TraderLabel(trader));
                    if (fail != null) return CommandResult.Fail(fail);
                }
                foreach (Tradeable row in buyAllocations.Keys.Concat(sellAllocations.Keys).Distinct())
                {
                    row.AdjustTo(row.CountToTransfer + Allocated(buyAllocations, row) - Allocated(sellAllocations, row));
                }

                deal.UpdateCurrencyCount();
                Tradeable currency = deal.CurrencyTradeable;
                int colonySilver = currency != null ? currency.CountHeldBy(Transactor.Colony) : 0;
                if (currency == null || currency.CountPostDealFor(Transactor.Colony) < 0)
                {
                    // 先擋下：TryExecute 的殖民地餘額不足分支會對不存在的對話窗呼叫 FlashSilver。
                    int need = colonySilver - (currency != null ? currency.CountPostDealFor(Transactor.Colony) : 0);
                    return CommandResult.Fail($"the colony cannot afford this: needs {need} silver but has {colonySilver}.");
                }
                if (!deal.DoesTraderHaveEnoughSilver())
                {
                    return CommandResult.Fail($"{TraderLabel(trader)} cannot afford this: needs more silver to pay for the colony's goods.");
                }

                // 交割前先把收據算好：銀兩實際變動取自貨幣行的公式值，品項數量即交割數量。
                string receipt = $"Traded with {TraderLabel(trader)} (negotiated by {negotiator.LabelShort}): " +
                    string.Join("; ", ReceiptParts(buys, sells)) +
                    $". Colony silver {colonySilver} -> {currency.CountPostDealFor(Transactor.Colony)}.";
                if (!deal.TryExecute(out _))
                {
                    return CommandResult.Fail("the trade was blocked before anything changed hands.");
                }
                return CommandResult.Ok(receipt);
            }
            finally
            {
                TradeSession.Close();
            }
        }

        /// <summary>
        /// 把一行買賣需求按方向分到同 def 的各行（同 def 可能因品質／材料分多行）。
        /// 全部吃不下回傳原因字串；成功回傳 null。只算數不動遊戲狀態。
        /// </summary>
        private static string Allocate(Dictionary<Tradeable, int> allocations, TradeDeal deal, TradeLine line, bool buy, string traderLabel)
        {
            List<Tradeable> rows = deal.AllTradeables
                .Where(r => !r.IsCurrency && r.ThingDef != null && string.Equals(r.ThingDef.defName, line.DefName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            int rest = line.Count;
            foreach (Tradeable row in rows)
            {
                if (rest <= 0) break;
                int capacity = buy ? row.GetMaximumToTransfer() : -row.GetMinimumToTransfer();
                int room = capacity - Allocated(allocations, row);
                int take = Math.Min(rest, Math.Max(0, room));
                if (take <= 0) continue;
                allocations[row] = Allocated(allocations, row) + take;
                rest -= take;
            }
            if (rest > 0)
            {
                int available = line.Count - rest;
                return buy
                    ? $"{traderLabel} only has {available} {ItemLabel(line.DefName)} for sale, not {line.Count}."
                    : $"the colony only has {available} sellable {ItemLabel(line.DefName)}, not {line.Count}.";
            }
            return null;
        }

        private static int Allocated(Dictionary<Tradeable, int> allocations, Tradeable row)
        {
            return allocations.TryGetValue(row, out int n) ? n : 0;
        }

        private static IEnumerable<string> ReceiptParts(IReadOnlyList<TradeLine> buys, IReadOnlyList<TradeLine> sells)
        {
            if (buys.Count > 0) yield return "bought " + string.Join(", ", buys.Select(l => $"{ItemLabel(l.DefName)} ({l.DefName}) x{l.Count}"));
            if (sells.Count > 0) yield return "sold " + string.Join(", ", sells.Select(l => $"{ItemLabel(l.DefName)} ({l.DefName}) x{l.Count}"));
        }

        private static string ItemLabel(string defName)
        {
            return DefDatabase<ThingDef>.GetNamedSilentFail(defName)?.label ?? defName;
        }

        private static string TraderLabel(ITrader trader)
        {
            TradeShip ship = trader as TradeShip;
            if (ship != null) return ship.FullTitle;
            Pawn pawn = trader as Pawn;
            if (pawn != null) return pawn.LabelShort;
            return trader.TraderName;
        }

        private static ITrader FindTrader(string id)
        {
            Map map = Map;
            foreach (TradeShip ship in map.passingShipManager.passingShips.OfType<TradeShip>())
            {
                if (ship.GetUniqueLoadID() == id) return ship;
            }
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.trader != null && pawn.trader.traderKind != null && pawn.GetUniqueLoadID() == id) return pawn;
            }
            return null;
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
                MedicalCare = pawn.playerSettings != null ? pawn.playerSettings.medCare.ToString() : string.Empty,
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
