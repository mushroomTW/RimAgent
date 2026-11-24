using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Game;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

/// <summary>研究、馴服、囚犯、手術、刪除與買賣題。</summary>
public sealed partial class BenchmarkComponent
{
    private const string TargetKey = "target";
    private const string OthersKey = "others";
    private const string DeletedZoneLabel = "臨時堆放區";
    private const string KeptZoneLabel = "主要倉庫";
    private const int BoughtComponents = 3;

    private static ResearchProjectDef _previousProject;
    private static Pawn _wildAnimal;
    private static Pawn _prisoner;
    private static Pawn _amputee;
    private static BodyPartRecord _missingLeg;
    /// <summary>缺腿會連同舊傷一起清掉，還原時補回。</summary>
    private static List<Hediff> _legHediffs = [];
    private static HashSet<Bill> _billsBeforeSurgery = [];
    private static Pawn _trader;
    private static List<Pawn> _caravan = [];
    private static Dictionary<Thing, (ThingDef Def, IntVec3 Cell, int Count)> _itemsBeforeTrade = [];
    private static List<IntVec3> _addedHome = [];

    private static BenchTask SetResearchTask() =>
        new()
        {
            Id = "set_research",
            Instruction = "把殖民地目前的研究項目換成釀酒（beer brewing）。",
            Setup = _ =>
            {
                _previousProject = Find.ResearchManager.GetProject();
                ResearchProjectDef target = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Brewing");
                if (target == null || target.IsFinished || !target.CanStartNow) return "Brewing cannot be researched in this game";
                if (_previousProject != null) Find.ResearchManager.StopProject(_previousProject);
                return null;
            },
            Cleanup = _ =>
            {
                ResearchProjectDef current = Find.ResearchManager.GetProject();
                if (current != null) Find.ResearchManager.StopProject(current);
                if (_previousProject != null) Find.ResearchManager.SetCurrentProject(_previousProject);
            },
            Measure = _ => new() { [TargetKey] = Find.ResearchManager.GetProject()?.defName == "Brewing" ? 1 : 0 },
            Check = (_, after, _) => after[TargetKey] == 1 ? null : "current research is not Brewing",
            Solve = world => world.SetResearch("Brewing")
        };

    private static BenchTask TameAnimalTask() =>
        new()
        {
            Id = "tame_animal",
            Instruction = "殖民者附近有一頭野生羊駝（alpaca）。標記一頭要馴服的羊駝，不要標記其他動物。",
            Setup = map =>
            {
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Alpaca");
                if (kind == null) return "missing pawn kind Alpaca";
                _wildAnimal = (Pawn)GenSpawn.Spawn(PawnGenerator.GeneratePawn(kind), NearColonists(map), map);
                return null;
            },
            Cleanup = _ => DestroySpawned(ref _wildAnimal),
            Measure = map => new()
            {
                [TargetKey] = TameMarks(map).Count(IsAlpaca),
                [OthersKey] = TameMarks(map).Count(t => !IsAlpaca(t))
            },
            Check = (before, after, _) => Fails(
                Delta(before, after, TargetKey) == 1 ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"alpacas marked for taming +{Delta(before, after, TargetKey)} (need +1)"),
                Delta(before, after, OthersKey) == 0 ? null : "other animals were marked for taming"),
            Solve = world => world.TameAnimal(_wildAnimal.thingIDNumber)
        };

    private static BenchTask RecruitPrisonerTask() =>
        new()
        {
            Id = "recruit_prisoner",
            Instruction = "殖民地剛抓到一名囚犯。把對這名囚犯的處置方式改成招募。",
            Setup = map =>
            {
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Drifter");
                if (kind == null) return "missing pawn kind Drifter";
                _prisoner = PawnGenerator.GeneratePawn(kind);
                _prisoner.guest.Recruitable = true;
                GenSpawn.Spawn(_prisoner, NearColonists(map), map);
                _prisoner.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
                _prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.MaintainOnly);
                return _prisoner.IsPrisonerOfColony ? null : "could not imprison the generated pawn";
            },
            Cleanup = _ => DestroySpawned(ref _prisoner),
            Measure = _ => new() { [TargetKey] = _prisoner?.guest?.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.AttemptRecruit ? 1 : 0 },
            Check = (_, after, _) => after[TargetKey] == 1 ? null : "the prisoner interaction mode is not recruit",
            Solve = world => world.SetPrisonerInteraction(_prisoner.thingIDNumber, PrisonerInteractionModeDefOf.AttemptRecruit.defName)
        };

    private static BenchTask SurgeryTask() =>
        new()
        {
            Id = "surgery",
            Tier = Hard,
            Instruction = "有一位殖民者少了一條腿。找出是誰，替這個人排一個在缺腿的位置安裝木腿（peg leg）的手術。",
            Setup = map =>
            {
                _billsBeforeSurgery = [.. map.mapPawns.FreeColonistsSpawned.SelectMany(p => p.BillStack.Bills)];
                _amputee = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => Legs(p).Any());
                if (_amputee == null) return "no colonist with a natural leg";
                _missingLeg = Legs(_amputee).First();
                _legHediffs = [.. _amputee.health.hediffSet.hediffs.Where(h => h.Part != null && _missingLeg.GetPartAndAllChildParts().Contains(h.Part))];
                var missing = (Hediff_MissingPart)HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, _amputee, _missingLeg);
                missing.IsFresh = false;
                _amputee.health.AddHediff(missing, _missingLeg);
                return null;
            },
            Cleanup = map =>
            {
                foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                {
                    foreach (Bill bill in pawn.BillStack.Bills.Where(b => !_billsBeforeSurgery.Contains(b)).ToList()) pawn.BillStack.Delete(bill);
                }
                if (_amputee == null || _amputee.Destroyed) return;
                _amputee.health.RestorePart(_missingLeg);
                foreach (Hediff old in _legHediffs) _amputee.health.hediffSet.AddDirect(old);
                _amputee = null;
            },
            Measure = map => new()
            {
                [TargetKey] = PegLegBills(_amputee).Count(b => b.Part == _missingLeg),
                [OthersKey] = map.mapPawns.FreeColonistsSpawned.Sum(p => p.BillStack.Bills.Count)
            },
            Check = (before, after, _) => Fails(
                after[TargetKey] == 1 ? null : "no peg leg surgery on the missing leg",
                Delta(before, after, OthersKey) <= 1 ? null : "more than one operation was scheduled"),
            Solve = world => world.ScheduleSurgery(new SurgeryRequest { PawnId = _amputee.thingIDNumber, RecipeDefName = "InstallPegLeg", BodyPartLabel = _missingLeg.Label })
        };

    private static BenchTask DeleteTask() =>
        new()
        {
            Id = "delete",
            Tier = Hard,
            Instruction = $"刪掉叫「{DeletedZoneLabel}」的儲存區，再把爐灶上簡易餐點的工作單刪掉；其他區域與工作單都要留著。",
            Setup = map =>
            {
                _stove = SpawnBuilding(map, "FueledStove");
                if (_stove is not IBillGiver giver) return "could not place a stove near the colonists";
                foreach (string recipe in new[] { "CookMealFine", "CookMealSimple", "CookMealLavish" })
                {
                    if (DefDatabase<RecipeDef>.GetNamedSilentFail(recipe) is not { } def) return "missing recipe " + recipe;
                    giver.BillStack.AddBill(def.MakeNewBill());
                }
                return AddStockpile(map, KeptZoneLabel) && AddStockpile(map, DeletedZoneLabel) ? null : "no room for the stockpiles";
            },
            Measure = map => new()
            {
                ["deletedZone"] = map.zoneManager.AllZones.Count(z => z.label == DeletedZoneLabel),
                ["zones"] = map.zoneManager.AllZones.Count,
                ["simpleBill"] = HasBill(_stove, "CookMealSimple", 0) ? 1 : 0,
                ["bills"] = (_stove as IBillGiver)?.BillStack.Bills.Count ?? 0
            },
            Check = (before, after, _) => Fails(
                after["deletedZone"] == 0 ? null : "the zone was not deleted",
                after["simpleBill"] == 0 ? null : "the simple meal bill was not deleted",
                Delta(before, after, "zones") >= -1 ? null : "other zones were deleted",
                Delta(before, after, "bills") >= -1 ? null : "other bills were deleted"),
            Solve = world =>
            {
                CommandResult zone = world.DeleteZone(world.GetZones().First(z => z.Label == DeletedZoneLabel).Id);
                // CookMealSimple 排在第二張，編號從 1 起算。
                return zone.Success ? world.DeleteBill(_stove.thingIDNumber, 2) : zone;
            }
        };

    private static BenchTask TradeTask() =>
        new()
        {
            Id = "trade",
            Tier = Hard,
            Instruction = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"有一支商隊正在地圖上。向商隊買 {BoughtComponents} 個零部件（component），不要買賣其他東西。"),
            Setup = SpawnTraders,
            Cleanup = RemoveTraders,
            Measure = _ => new()
            {
                [TargetKey] = TraderGoods(d => d == ThingDefOf.ComponentIndustrial),
                [OthersKey] = TraderGoods(d => d != ThingDefOf.ComponentIndustrial && d != ThingDefOf.Silver)
            },
            Check = (before, after, _) => Fails(
                Delta(before, after, TargetKey) == -BoughtComponents ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"bought {-Delta(before, after, TargetKey)} components (need {BoughtComponents})"),
                Delta(before, after, OthersKey) == 0 ? null : "other goods changed hands"),
            Solve = world => world.ExecuteTrade(new TradeRequest
            {
                TraderId = world.GetTraders().First(t => t.Kind == TraderInfo.VisitorKind).Id,
                NegotiatorPawnId = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First(p => !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled).thingIDNumber,
                Buys = [new TradeLine { DefName = ThingDefOf.ComponentIndustrial.defName, Count = BoughtComponents }]
            })
        };

    private static IEnumerable<BodyPartRecord> Legs(Pawn pawn) =>
        pawn.health.hediffSet.GetNotMissingParts().Where(p => p.def == BodyPartDefOf.Leg && !pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(p));

    private static IEnumerable<Thing> TameMarks(Map map) =>
        map.designationManager.AllDesignations.Where(d => d.def == DesignationDefOf.Tame).Select(d => d.target.Thing);

    private static bool IsAlpaca(Thing thing) => thing?.def.defName == "Alpaca";

    private static IEnumerable<Bill_Medical> PegLegBills(Pawn pawn) =>
        pawn?.BillStack.Bills.OfType<Bill_Medical>().Where(b => b.recipe.defName == "InstallPegLeg") ?? [];

    private static IntVec3 NearColonists(Map map)
    {
        IntVec3 center = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
        return CellFinder.RandomClosewalkCellNear(center, map, 12);
    }

    private static void DestroySpawned(ref Pawn pawn)
    {
        if (pawn is { Destroyed: false }) pawn.Destroy();
        pawn = null;
    }

    private static bool AddStockpile(Map map, string label)
    {
        IntVec3 center = map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
        foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, 25f, useCenter: false))
        {
            CellRect rect = CellRect.CenteredOn(cell, 1);
            if (!rect.InBounds(map) || !rect.Cells.All(c => c.Standable(map) && c.GetEdifice(map) == null && map.zoneManager.ZoneAt(c) == null)) continue;
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 c in rect.Cells) zone.AddCell(c);
            zone.label = label;
            return true;
        }
        return false;
    }

    /// <summary>商隊照遊戲事件進場，再塞入零部件並給殖民地足夠的銀，買賣才不受庫存與物價影響。</summary>
    private static string SpawnTraders(Map map)
    {
        _itemsBeforeTrade = Items(map).ToDictionary(t => t, t => (t.def, t.Position, t.stackCount));
        HashSet<Pawn> before = [.. map.mapPawns.AllPawnsSpawned];
        IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentDefOf.TraderCaravanArrival.category, map);
        parms.forced = true;
        bool arrived = IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(parms);
        _caravan = [.. map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p))];
        if (!arrived) return "no trader caravan could arrive";
        _trader = _caravan.Find(p => p.trader?.CanTradeNow == true);
        Pawn carrier = _caravan.Find(p => p.GetTraderCaravanRole() == TraderCaravanRole.Carrier) ?? _trader;
        if (_trader == null) return "the caravan has no trader";
        Thing components = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
        components.stackCount = 10;
        if (!carrier.inventory.innerContainer.TryAdd(components)) return "could not stock the caravan";

        Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
        silver.stackCount = 500;
        if (!GenPlace.TryPlaceThing(silver, NearColonists(map), map, ThingPlaceMode.Near, out Thing placed)) return "could not place silver";
        placed.SetForbidden(value: false, warnOnFail: false);
        // 地面商隊只看得到居住區或儲存區裡的東西（Pawn_TraderTracker.ColonyThingsWillingToBuy）。
        _addedHome = [.. new[] { placed.Position }.Where(c => !map.areaManager.Home[c])];
        foreach (IntVec3 cell in _addedHome) map.areaManager.Home[cell] = true;
        return null;
    }

    private static void RemoveTraders(Map map)
    {
        foreach (Pawn pawn in _caravan.Where(p => !p.Destroyed).ToList())
        {
            pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.Vanished);
            pawn.Destroy();
        }
        _caravan = [];
        _trader = null;
        foreach (Thing item in Items(map).Where(t => !_itemsBeforeTrade.ContainsKey(t)).ToList()) item.Destroy();
        // 佈置的銀與買到的東西會併進舊堆，付款也可能先用舊堆。
        foreach ((Thing item, (ThingDef def, IntVec3 cell, int count)) in _itemsBeforeTrade)
        {
            if (item.Spawned) item.stackCount = count;
            else if (def == ThingDefOf.Silver)
            {
                Thing silver = ThingMaker.MakeThing(def);
                silver.stackCount = count;
                GenPlace.TryPlaceThing(silver, cell, map, ThingPlaceMode.Near);
            }
        }
        _itemsBeforeTrade = [];
        foreach (IntVec3 cell in _addedHome) map.areaManager.Home[cell] = false;
        _addedHome = [];
    }

    private static IEnumerable<Thing> Items(Map map) => map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item);

    private static int TraderGoods(Func<ThingDef, bool> predicate) =>
        _trader?.trader?.Goods.Where(t => predicate(t.def)).Sum(t => t.stackCount) ?? 0;

    /// <summary>
    /// 不叫模型的自我檢查：每題都佈置得起來、什麼都不做不會通過，有參考解的題目做完會通過，還原後沒有殘留。
    /// </summary>
    private static void SelfCheck()
    {
        Map map = Find.CurrentMap;
        var world = new RimWorldGameWorld(Current.Game);
        int failed = 0;
        Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        foreach (BenchTask task in Tasks)
        {
            string problem = null;
            int silver = Items(map).Where(t => t.def == ThingDefOf.Silver || t.def == ThingDefOf.ComponentIndustrial).Sum(t => t.stackCount);
            int bills = map.mapPawns.FreeColonistsSpawned.Sum(p => p.BillStack.Bills.Count);
            int hediffs = map.mapPawns.FreeColonistsSpawned.Sum(p => p.health.hediffSet.hediffs.Count);
            int pawns = map.mapPawns.AllPawnsSpawned.Count;
            int zones = map.zoneManager.AllZones.Count;
            int[] terrain = [.. map.AllCells.Select(c => (int)c.GetTerrain(map).shortHash)];
            int home = map.areaManager.Home.TrueCount;
            int marks = map.designationManager.AllDesignations.Count();
            int settled = map.listerThings.AllThings.Count(t => t.def.category is ThingCategory.Plant or ThingCategory.Item or ThingCategory.Building);
            var snapshot = new BenchmarkSnapshot(map);
            try
            {
                problem = task.Setup?.Invoke(map) is { } setup ? "setup: " + setup : null;
                if (problem == null)
                {
                    Dictionary<string, int> before = task.Measure(map);
                    if (task.Check(before, task.Measure(map), string.Empty) == null) problem = "passes without doing anything";
                    CommandResult solved = problem == null ? task.Solve?.Invoke(world) : null;
                    if (solved is { Success: false }) problem = "solve: " + solved.Message;
                    else if (solved != null) problem = task.Check(before, task.Measure(map), string.Empty);
                }
            }
            catch (Exception ex)
            {
                problem = ex.ToString();
            }
            TryCleanUp(task.Id, () => task.Cleanup?.Invoke(map));
            TryCleanUp(task.Id, snapshot.Restore);
            if (problem == null && map.mapPawns.AllPawnsSpawned.Count != pawns) problem = "pawns left behind";
            if (problem == null && map.zoneManager.AllZones.Count != zones) problem = "zones left behind";
            if (problem == null && Items(map).Where(t => t.def == ThingDefOf.Silver || t.def == ThingDefOf.ComponentIndustrial).Sum(t => t.stackCount) != silver) problem = "silver or components changed";
            if (problem == null && map.mapPawns.FreeColonistsSpawned.Sum(p => p.BillStack.Bills.Count) != bills) problem = "operations left behind";
            if (problem == null && map.mapPawns.FreeColonistsSpawned.Sum(p => p.health.hediffSet.hediffs.Count) != hediffs) problem = "health changed";
            if (problem == null && !map.AllCells.Select(c => (int)c.GetTerrain(map).shortHash).SequenceEqual(terrain)) problem = "terrain changed";
            if (problem == null && task.Tier == Survey && map.listerThings.AllThings.Count(t => t.def.category is ThingCategory.Plant or ThingCategory.Item or ThingCategory.Building) != settled) problem = "plants, items or buildings changed";
            if (problem == null && task.Tier == Survey && (map.areaManager.Home.TrueCount != home || map.designationManager.AllDesignations.Count() != marks)) problem = "home area or marks changed";
            if (problem != null) failed++;
            Log.Message(LogPrefix + "SelfCheck " + task.Id + ": " + (problem ?? (task.Solve == null ? "ok" : "ok, solved")));
        }
        Log.Warning(LogPrefix + "SelfCheck " + (failed == 0 ? "PASS" : "FAIL " + failed.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
