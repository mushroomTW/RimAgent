using System;
using System.Collections.Generic;
using System.Linq;
using DevTools.Testing;
using RimAgent.Game;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimAgent.InGameTests;

internal sealed partial class MapWorldTests
{
    /// <summary>Caravan 測試清理所需的所有狀態，避免 CleanupCaravanTest 參數過多。</summary>
    private sealed class CaravanTestContext
    {
        public Pawn Colonist { get; init; }
        public Lord Lord { get; init; }
        public List<Thing> Spawned { get; init; }
        public Pawn Animal { get; init; }
        public bool CreatedAnimal { get; init; }
        public Dictionary<IntVec3, bool> HomeBefore { get; init; }
        public Map Map { get; init; }
        public HashSet<Letter> LettersBefore { get; init; }
    }

    /// <summary>
    /// 商隊走完一趟：用原版從地圖邊緣組一支單人商隊，查詢對照原版，派到相鄰格並以 pather 讀回目的地，
    /// 暫停、繼續、停下，過期的選單被擋下；再把商隊放回家的格子，走「進入地圖」命令，
    /// 殖民者要回到家園地圖上，get_maps 的人數同步。結束時一定把殖民者帶回地圖。
    /// </summary>
    [Test]
    public void CaravanTravelsPausesStopsAndEntersTheHomeMap()
    {
        if (TestHelpers.SkipWithoutMap(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap))) return;
        Map map = Find.CurrentMap;
        if (!TrySetupCaravanTest(map, out var pawn)) return;
        var failures = new List<string>();
        _world.TakeNewGameErrors();
        var lettersBefore = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
        Caravan caravan = null;
        try
        {
            caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(new List<Pawn> { pawn }, Faction.OfPlayer, map.Tile, Direction8Way.North, PlanetTile.Invalid);
            if (caravan == null)
            {
                TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "vanilla could not form a caravan here");
                return;
            }
            // 原版從地圖邊緣出發時會自動往出口方向的格子走（CaravanExitMapUtility 的 StartPath(directionTile)）；
            // 查詢要照實回報，之後停下來，讓後面的檢查從靜止開始。
            CaravanInfo leaving = _world.GetCaravans().FirstOrDefault(c => c.Id == caravan.ID);
            if (leaving != null && leaving.Moving != caravan.pather.Moving) failures.Add($"Moving {leaving.Moving}, vanilla {caravan.pather.Moving}");
            caravan.pather.StopDead();
            CheckCaravanQuery(caravan, pawn, failures);

            // 單純前往相鄰的可走格（不是家）。
            PlanetTile next = FindNextTravelTile(map, caravan);
            if (next.Valid)
                CheckTravelControls(caravan, next, failures);
            else
                TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "no passable neighbouring tile to travel to");

            CheckEnterHomeMap(caravan, pawn, map, failures);
        }
        finally
        {
            // 沒走完測試流程時也要把人帶回家園地圖。
            if (caravan != null && !caravan.Destroyed && caravan.Spawned)
            {
                caravan.pather.StopDead();
                caravan.Tile = map.Tile;
                CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, CaravanDropInventoryMode.DoNotDrop, false);
            }
            foreach (Letter letter in Find.LetterStack.LettersListForReading.Where(l => !lettersBefore.Contains(l)).ToList())
                Find.LetterStack.RemoveLetter(letter);
        }
        AssertCaravanEndState(pawn, map, failures);
        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "caravan journey");
    }

    /// <summary>檢查地圖與殖民者前提，不符則跳過測試並回傳 false；符合則輸出殖民者清單與目標 pawn。</summary>
    private static bool TrySetupCaravanTest(Map map, out Pawn pawn)
    {
        pawn = null;
        if (map.Parent is not Settlement || map.Parent.Faction != Faction.OfPlayer)
        {
            TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "the current map is not a player settlement");
            return false;
        }
        List<Pawn> colonists = [.. map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && p.IsFreeNonSlaveColonist)];
        if (colonists.Count < 2)
        {
            TestHelpers.Skip(nameof(CaravanTravelsPausesStopsAndEntersTheHomeMap), "needs at least two free colonists so the colony is never empty");
            return false;
        }
        pawn = colonists[0];
        return true;
    }

    /// <summary>斷言殖民者已回到家園地圖且沒有殘留的目標選擇器。</summary>
    private static void AssertCaravanEndState(Pawn pawn, Map map, List<string> failures)
    {
        if (!pawn.Spawned || pawn.Map != map) failures.Add($"{pawn.LabelShort} did not end up back on the home map");
        if (Find.Targeter.IsTargeting || Find.WorldTargeter.IsTargeting) failures.Add("a targeter was left open");
    }

    /// <summary>從商隊所在格挑選一個可達、非自家且非通行禁止的相鄰格。</summary>
    private static PlanetTile FindNextTravelTile(Map map, Caravan caravan)
    {
        var neighbors = new List<PlanetTile>();
        Find.WorldGrid.GetTileNeighbors(caravan.Tile, neighbors);
        return neighbors.Where(t => t != map.Tile && !Find.World.Impassable(t) && caravan.CanReach(t))
            .DefaultIfEmpty(PlanetTile.Invalid).First();
    }

    private void CheckCaravanQuery(Caravan caravan, Pawn pawn, List<string> failures)
    {
        CaravanInfo info = _world.GetCaravans().FirstOrDefault(c => c.Id == caravan.ID);
        if (info == null)
        {
            failures.Add("the new caravan is missing from GetCaravans");
            return;
        }
        CheckCaravanBasics(info, caravan, pawn, failures);
        bool settleBlocked = SettleInEmptyTileUtility.SettleCommand(caravan).Disabled;
        CheckCaravanSettleCamp(info, caravan, settleBlocked, failures);
        WorldSurveyInfo survey = _world.GetWorldSurvey(caravan.ID);
        CheckCaravanSurvey(survey, caravan, failures);
        CheckSettleCommands(caravan, settleBlocked, failures);
    }

    /// <summary>比對商隊的基本欄位：格子、成員、移動中、目的地、載重與食物天數。</summary>
    private static void CheckCaravanBasics(CaravanInfo info, Caravan caravan, Pawn pawn, List<string> failures)
    {
        if (info.Tile != caravan.Tile.tileId) failures.Add($"caravan tile {info.Tile}, vanilla {caravan.Tile.tileId}");
        if (info.Members.Count != 1 || info.Members[0].Id != pawn.thingIDNumber || info.Members[0].Kind != CaravanMemberInfo.ColonistKind)
            failures.Add("caravan members do not match the one colonist that left");
        if (info.Moving) failures.Add("a fresh caravan reports it is moving");
        if (info.DestinationTile != -1) failures.Add("a fresh caravan reports destination " + info.DestinationTile);
        if (Math.Abs(info.MassCapacity - caravan.MassCapacity) > 0.01f) failures.Add($"mass capacity {info.MassCapacity}, vanilla {caravan.MassCapacity}");
        if (Math.Abs(info.FoodDays - DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(caravan)) > 0.01f) failures.Add("food days differ from vanilla");
    }

    /// <summary>比對定居與露營指令的 disabled 狀態。</summary>
    private static void CheckCaravanSettleCamp(CaravanInfo info, Caravan caravan, bool settleBlocked, List<string> failures)
    {
        if (info.CanSettle == settleBlocked) failures.Add($"CanSettle {info.CanSettle}, vanilla command disabled {settleBlocked}");
        if (settleBlocked && string.IsNullOrEmpty(info.SettleBlockedReason)) failures.Add("settling is blocked but no reason is given");
        if (info.CanCamp == SettleInEmptyTileUtility.SetupCamp(caravan).Disabled) failures.Add("CanCamp differs from the vanilla camp command");
    }

    /// <summary>從商隊視角發出的調查，起點應為商隊本身且不應列出商隊自己。</summary>
    private static void CheckCaravanSurvey(WorldSurveyInfo survey, Caravan caravan, List<string> failures)
    {
        if (!survey.OriginIsCaravan || survey.OriginTile != caravan.Tile.tileId) failures.Add("a survey from the caravan does not start at the caravan");
        if (survey.Objects.Any(o => o.Id == caravan.ID)) failures.Add("the survey lists the caravan it starts from");
    }

    /// <summary>驗證定居命令：原版封鎖時應被拒絕，且未知動作不應成功。</summary>
    private void CheckSettleCommands(Caravan caravan, bool settleBlocked, List<string> failures)
    {
        // 沒打算在這裡真的定居：只在原版也說不行時驗證命令會被擋下。
        if (settleBlocked)
        {
            CommandResult settle = _world.SettleCaravan(caravan.ID, CaravanSettleAction.Settle);
            if (settle.Success) failures.Add("settling succeeded although the vanilla command is disabled");
            if (caravan.Destroyed || !caravan.Spawned) failures.Add("a refused settle removed the caravan");
        }
        if (_world.SettleCaravan(caravan.ID, "build").Success) failures.Add("an unknown settle action succeeded");
    }

    private void CheckTravelControls(Caravan caravan, PlanetTile next, List<string> failures)
    {
        CaravanOptionsInfo options = _world.GetCaravanOptions(caravan.ID, next.tileId);
        if (!string.IsNullOrEmpty(options.Error) || options.Options.Count == 0)
        {
            failures.Add("no orders for the neighbouring tile: " + options.Error);
            return;
        }
        CheckFirstTravelOption(options, next, failures);
        CheckStaleSendRejected(caravan, next, failures);
        FloatMenuOptionInfo travel = options.Options[0];
        CheckSendSucceeds(caravan, next, travel, failures);
        CheckMovementControls(caravan, failures);
    }

    /// <summary>首個下單選項應為前往目標格，且不應出現開發者模式指令。</summary>
    private static void CheckFirstTravelOption(CaravanOptionsInfo options, PlanetTile next, List<string> failures)
    {
        FloatMenuOptionInfo travel = options.Options[0];
        if (travel.Disabled || !travel.Label.StartsWith("Travel to tile " + next.tileId, StringComparison.Ordinal))
            failures.Add($"first order is \"{travel.Label}\" (disabled {travel.Disabled})");
        if (options.Options.Any(o => o.Label.Contains("(Dev:"))) failures.Add("a developer-mode order was offered");
    }

    /// <summary>用過期標籤下單應被拒絕，且商隊不得開始移動。</summary>
    private void CheckStaleSendRejected(Caravan caravan, PlanetTile next, List<string> failures)
    {
        CommandResult stale = _world.SendCaravan(caravan.ID, next.tileId, 1, "Travel somewhere else");
        if (stale.Success || caravan.pather.Moving) failures.Add("an order with a wrong label was carried out");
    }

    /// <summary>正確下單後，商隊與 GetCaravans 應顯示目的地與預估到達時間。</summary>
    private void CheckSendSucceeds(Caravan caravan, PlanetTile next, FloatMenuOptionInfo travel, List<string> failures)
    {
        CommandResult sent = _world.SendCaravan(caravan.ID, next.tileId, travel.Number, travel.Label);
        if (!sent.Success) failures.Add("send failed: " + sent.Error);
        if (!caravan.pather.Moving || caravan.pather.Destination != next) failures.Add("the caravan's pather has no destination " + next.tileId);
        CaravanInfo moving = _world.GetCaravans().First(c => c.Id == caravan.ID);
        if (!moving.Moving || moving.DestinationTile != next.tileId) failures.Add($"GetCaravans destination {moving.DestinationTile}, expected {next.tileId}");
        if (moving.EtaTicks <= 0) failures.Add("a travelling caravan has no arrival estimate");
    }

    /// <summary>暫停／繼續／停止商隊移動的狀態應與原版一致。</summary>
    private void CheckMovementControls(Caravan caravan, List<string> failures)
    {
        if (!_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Pause).Success || !caravan.pather.Paused) failures.Add("pause did not pause the pather");
        if (!_world.GetCaravans().First(c => c.Id == caravan.ID).Paused) failures.Add("GetCaravans does not show the pause");
        if (!_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Resume).Success || caravan.pather.Paused) failures.Add("resume did not clear the pause");
        if (!_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Stop).Success || caravan.pather.Moving) failures.Add("stop left the caravan moving");
        if (_world.GetCaravans().First(c => c.Id == caravan.ID).DestinationTile != -1) failures.Add("a stopped caravan still reports a destination");
        if (_world.SetCaravanMovement(caravan.ID, CaravanMovementAction.Pause).Success) failures.Add("pausing a stopped caravan succeeded");
    }

    private void CheckEnterHomeMap(Caravan caravan, Pawn pawn, Map map, List<string> failures)
    {
        // 瞬間回到家的格子（原版開發者「Dev: instantly」做的事），再走正式的進入命令。
        caravan.pather.StopDead();
        caravan.Tile = map.Tile;
        CaravanInfo here = _world.GetCaravans().FirstOrDefault(c => c.Id == caravan.ID);
        if (here == null || here.ObjectsHere.Count == 0) failures.Add("the caravan on the home tile does not list the home settlement as an object here");

        string enterLabel = "EnterMap".Translate(map.Parent.Label).ToString();
        CaravanOptionsInfo options = _world.GetCaravanOptions(caravan.ID, map.Tile.tileId);
        if (options.Options.Count == 0 || !options.Options[0].Disabled) failures.Add("plain travel to the caravan's own tile should be disabled");
        FloatMenuOptionInfo enter = options.Options.FirstOrDefault(o => o.Label.StartsWith(enterLabel, StringComparison.Ordinal) && !o.Disabled);
        if (enter == null)
        {
            failures.Add($"no enabled \"{enterLabel}\" order; orders: {string.Join("; ", options.Options.Select(o => o.Number + ") " + o.Label).ToArray())}");
            return;
        }
        CommandResult entered = _world.SendCaravan(caravan.ID, map.Tile.tileId, enter.Number, enter.Label);
        if (!entered.Success) failures.Add("entering the home map failed: " + entered.Error);
        if (!pawn.Spawned || pawn.Map != map) failures.Add($"{pawn.LabelShort} is not spawned on the home map after entering");
        if (!caravan.Destroyed && caravan.Spawned) failures.Add("the caravan still exists after its members entered the map");
        if (_world.GetCaravans().Any(c => c.Id == caravan.ID)) failures.Add("the entered caravan is still listed");
        RimAgent.Game.MapInfo mapInfo = _world.GetMaps().FirstOrDefault(m => m.Id == map.uniqueID);
        if (mapInfo == null) failures.Add("the home map is missing from GetMaps");
        else if (mapInfo.ColonistCount != map.mapPawns.FreeColonistsSpawnedCount) failures.Add($"GetMaps counts {mapInfo.ColonistCount} colonists, vanilla {map.mapPawns.FreeColonistsSpawnedCount}");
    }

    /// <summary>
    /// 組商隊：在家園地圖上放好銀兩、食物與一隻殖民地動物，規劃的數字對照原版計算器；超重被擋下且不集結；
    /// 再送出，檢查原版的集結狀態（Lord、LordJob 的貨物清單、成員），最後中止集結，成員恢復平常、貨物原封不動。
    /// 測試期間不推進遊戲時間，所以沒有人真的離開地圖；結束時一律清掉 Lord 與放出來的東西。
    /// </summary>
    [Test]
    public void CaravanPlansFormsAndCancelsWithVanillaState()
    {
        if (TestHelpers.SkipWithoutMap(nameof(CaravanPlansFormsAndCancelsWithVanillaState))) return;
        Map map = Find.CurrentMap;
        if (map.Parent is not Settlement || map.Parent.Faction != Faction.OfPlayer)
        {
            TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "the current map is not a player settlement");
            return;
        }
        Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.Drafted && p.IsFreeNonSlaveColonist);
        if (colonist == null || map.mapPawns.FreeColonistsSpawnedCount < 2)
        {
            TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "needs a walking colonist and at least two colonists");
            return;
        }
        var neighbors = new List<PlanetTile>();
        Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
        PlanetTile destination = neighbors.Where(t => !Find.World.Impassable(t) && Find.WorldReachability.CanReach(map.Tile, t))
            .DefaultIfEmpty(PlanetTile.Invalid).First();
        if (!destination.Valid)
        {
            TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "no reachable neighbouring tile to plan a trip to");
            return;
        }
        ThingDef foodDef = DefDatabase<ThingDef>.GetNamedSilentFail("Pemmican") ?? ThingDefOf.MealSimple;

        // 放貨物的格子：玩家看得到、站得住、沒東西、殖民者走得到；家園區暫時設成包含它們，結束時還原。
        List<IntVec3> cells = [.. GenRadial.RadialCellsAround(colonist.Position, 20f, true)
            .Where(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetFirstItem(map) == null && c.GetEdifice(map) == null
                        && colonist.CanReach(c, PathEndMode.Touch, Danger.Deadly))
            .Take(8)];
        if (cells.Count < 8)
        {
            TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState), "not enough free cells near a colonist");
            return;
        }

        var failures = new List<string>();
        _world.TakeNewGameErrors();
        var lettersBefore = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
        var homeBefore = cells.ToDictionary(c => c, c => map.areaManager.Home[c]);
        var spawned = new List<Thing>();
        Pawn animal = null;
        bool createdAnimal = false;
        Lord lord = null;
        try
        {
            foreach (IntVec3 c in cells) map.areaManager.Home[c] = true;
            Thing silver = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Silver), cells[0], map);
            silver.stackCount = 300;
            Thing food = GenSpawn.Spawn(ThingMaker.MakeThing(foodDef), cells[1], map);
            food.stackCount = 40;
            spawned.Add(silver);
            spawned.Add(food);

            animal = EnsureCaravanAnimal(map, cells, out createdAnimal);
            CheckCaravanCargoQuery(silver, food, failures);

            var request = new CaravanRequest
            {
                PawnIds = new[] { colonist.thingIDNumber, animal.thingIDNumber },
                Cargo = new[] { new TradeLine { DefName = silver.def.defName, Count = 100 }, new TradeLine { DefName = food.def.defName, Count = 30 } },
                DestinationTile = destination.tileId
            };
            CaravanPlanInfo plan = _world.PlanCaravan(request);
            if (!string.IsNullOrEmpty(plan.Error))
            {
                failures.Add("plan failed: " + plan.Error);
                TestHelpers.AssertNone(failures, "caravan forming");
                return;
            }
            CheckPlanMatchesVanilla(plan, colonist, animal, silver, food, destination, map, failures);

            CheckInvalidPlans(request, silver, destination, failures);
            int steelAvailable = SpawnSteelCargo(map, cells, spawned);
            CheckOverloadedPlan(colonist, steelAvailable, destination, map, failures);

            if (ShouldSkipForming(plan, failures)) return;

            // 送出：原版集結狀態。
            CommandResult formed = _world.FormCaravan(request);
            CheckFormed(formed, failures);
            lord = CheckFormedCaravanState(colonist, animal, foodDef, failures);
            CheckListedFormingCaravan(lord, colonist, animal, failures);
            if (Find.WorldObjects.Caravans.Any(c => c.PawnsListForReading.Contains(colonist))) failures.Add("the caravan left the map within the same frame");

            // 中止：成員恢復平常，貨物沒動。
            CheckCancelFormingCaravan(colonist, animal, lord, silver, food, foodDef, failures);
            lord = null;
        }
        finally
        {
            CleanupCaravanTest(new CaravanTestContext
            {
                Colonist = colonist,
                Lord = lord,
                Spawned = spawned,
                Animal = animal,
                CreatedAnimal = createdAnimal,
                HomeBefore = homeBefore,
                Map = map,
                LettersBefore = lettersBefore
            });
        }
        foreach (string error in _world.TakeNewGameErrors()) failures.Add(GameLogged + error);
        TestHelpers.AssertNone(failures, "caravan forming");
    }

    /// <summary>若地圖上沒可用的殖民地動物，就生成一隻獼牛並放在貨物格上；回傳動物與是否新建。</summary>
    private static Pawn EnsureCaravanAnimal(Map map, List<IntVec3> cells, out bool createdAnimal)
    {
        createdAnimal = false;
        Pawn animal = map.mapPawns.PawnsInFaction(Faction.OfPlayer)
            .FirstOrDefault(p => p.Spawned && p.RaceProps.Animal && p.RaceProps.allowedOnCaravan && !p.Downed && !p.Drafted && p.GetLord() == null);
        if (animal == null)
        {
            animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Muffalo, Faction.OfPlayer));
            GenSpawn.Spawn(animal, cells[2], map);
            createdAnimal = true;
        }
        return animal;
    }

    /// <summary>沒選目的地與超過現有庫存的規劃，都應被原版視窗擋下。</summary>
    private void CheckInvalidPlans(CaravanRequest request, Thing silver, PlanetTile destination, List<string> failures)
    {
        CaravanPlanInfo noRoute = _world.PlanCaravan(new CaravanRequest { PawnIds = request.PawnIds, Cargo = request.Cargo, DestinationTile = -1 });
        if (noRoute.Blockers.Count == 0) failures.Add("a plan without a destination has no blocker");
        if (_world.FormCaravan(new CaravanRequest { PawnIds = request.PawnIds, Cargo = request.Cargo, DestinationTile = -1 }).Success)
            failures.Add("forming without a destination succeeded");
        CaravanPlanInfo tooMuch = _world.PlanCaravan(new CaravanRequest
        {
            PawnIds = request.PawnIds,
            Cargo = new[] { new TradeLine { DefName = silver.def.defName, Count = 999999 } },
            DestinationTile = destination.tileId
        });
        if (string.IsNullOrEmpty(tooMuch.Error)) failures.Add("asking for more silver than the colony has was not an error");
    }

    /// <summary>生成三堆鋼作為超重貨物，並回傳可用的鋼總量。</summary>
    private static int SpawnSteelCargo(Map map, List<IntVec3> cells, List<Thing> spawned)
    {
        for (int i = 3; i < 6; i++)
        {
            Thing steel = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), cells[i], map);
            steel.stackCount = steel.def.stackLimit;
            spawned.Add(steel);
        }
        return spawned.Where(t => t.def == ThingDefOf.Steel).Sum(t => t.stackCount);
    }

    /// <summary>超重規劃應帶原版過載原因，且嘗試集結應被拒絕。</summary>
    private void CheckOverloadedPlan(Pawn colonist, int steelAvailable, PlanetTile destination, Map map, List<string> failures)
    {
        CaravanPlanInfo heavy = _world.PlanCaravan(new CaravanRequest
        {
            PawnIds = new[] { colonist.thingIDNumber },
            Cargo = new[] { new TradeLine { DefName = ThingDefOf.Steel.defName, Count = steelAvailable } },
            DestinationTile = destination.tileId
        });
        string tooBig = "TooBigCaravanMassUsage".Translate().Resolve();
        if (string.IsNullOrEmpty(heavy.Error) && heavy.MassUsage > heavy.MassCapacity && !heavy.Blockers.Contains(tooBig))
            failures.Add("an overloaded plan does not carry the game's reason: " + string.Join("; ", heavy.Blockers.ToArray()));
        if (string.IsNullOrEmpty(heavy.Error) && heavy.MassUsage > heavy.MassCapacity)
        {
            int lordsBefore = map.lordManager.lords.Count;
            CommandResult refused = _world.FormCaravan(new CaravanRequest { PawnIds = new[] { colonist.thingIDNumber },
                Cargo = new[] { new TradeLine { DefName = ThingDefOf.Steel.defName, Count = steelAvailable } }, DestinationTile = destination.tileId });
            if (refused.Success || map.lordManager.lords.Count != lordsBefore || colonist.IsFormingCaravan())
                failures.Add("an overloaded caravan was formed");
        }
    }

    /// <summary>原版會拒絕集結時，測試應跳過後續的集結驗證。</summary>
    private static bool ShouldSkipForming(CaravanPlanInfo plan, List<string> failures)
    {
        if (plan.Blockers.Count > 0)
        {
            TestHelpers.AssertNone(failures, "caravan planning");
            TestHelpers.Skip(nameof(CaravanPlansFormsAndCancelsWithVanillaState),
                "vanilla would refuse this caravan here, so forming is not exercised: " + string.Join("; ", plan.Blockers.ToArray()));
            return true;
        }
        return false;
    }

    /// <summary>集結命令成功與否應被記錄，失敗時即斷言。</summary>
    private static void CheckFormed(CommandResult formed, List<string> failures)
    {
        if (!formed.Success)
        {
            failures.Add("forming failed: " + formed.Error);
            TestHelpers.AssertNone(failures, "caravan forming");
        }
    }

    /// <summary>檢查集結領主與其貨物佇列是否與原版一致，並回傳領主。</summary>
    private static Lord CheckFormedCaravanState(Pawn colonist, Pawn animal, ThingDef foodDef, List<string> failures)
    {
        if (!colonist.IsFormingCaravan() || !animal.IsFormingCaravan()) failures.Add("the colonist and the animal are not forming a caravan");
        colonist.TryGetFormingCaravanLord(out Lord lord);
        if (lord?.LordJob is not LordJob_FormAndSendCaravan job) failures.Add("the colonist's lord is not a LordJob_FormAndSendCaravan");
        else
        {
            int silverQueued = job.transferables.Where(t => t.HasAnyThing && t.ThingDef == ThingDefOf.Silver).Sum(t => t.CountToTransfer);
            int foodQueued = job.transferables.Where(t => t.HasAnyThing && t.ThingDef == foodDef).Sum(t => t.CountToTransfer);
            if (silverQueued != 100 || foodQueued != 30) failures.Add($"the lord job queues silver {silverQueued}, food {foodQueued}; expected 100 and 30");
            if (job.transferables.Any(t => t.AnyThing is Pawn)) failures.Add("pawns were left in the lord job's item list");
            if (!lord.ownedPawns.Contains(colonist) || !lord.ownedPawns.Contains(animal)) failures.Add("the lord does not own both members");
        }
        return lord;
    }

    /// <summary>集結中的商隊應列在 GetFormingCaravans，成員與貨物正確。</summary>
    private void CheckListedFormingCaravan(Lord lord, Pawn colonist, Pawn animal, List<string> failures)
    {
        FormingCaravanInfo listed = _world.GetFormingCaravans().FirstOrDefault(f => lord != null && f.Id == lord.loadID);
        if (listed == null) failures.Add("GetFormingCaravans does not list the new caravan");
        else
        {
            if (!listed.Members.Any(m => m.Id == colonist.thingIDNumber) || !listed.Members.Any(m => m.Id == animal.thingIDNumber))
                failures.Add("the forming caravan lists the wrong members");
            if (!listed.Cargo.Any(c => c.EndsWith(" x100", StringComparison.Ordinal))) failures.Add("the forming caravan does not list the silver: " + string.Join(", ", listed.Cargo.ToArray()));
            if (string.IsNullOrEmpty(listed.Status)) failures.Add("the forming caravan has no status");
        }
    }

    /// <summary>取消集結後，成員應恢復平常、貨物原封不動、集結清單應移除。</summary>
    private void CheckCancelFormingCaravan(Pawn colonist, Pawn animal, Lord lord, Thing silver, Thing food, ThingDef foodDef, List<string> failures)
    {
        FormingCaravanInfo listed = _world.GetFormingCaravans().FirstOrDefault(f => lord != null && f.Id == lord.loadID);
        if (listed != null)
        {
            CommandResult cancelled = _world.CancelFormingCaravan(listed.Id);
            if (!cancelled.Success) failures.Add("cancelling failed: " + cancelled.Error);
        }
        if (colonist.IsFormingCaravan() || animal.IsFormingCaravan() || colonist.GetLord() is Lord leftover && leftover.LordJob is LordJob_FormAndSendCaravan)
            failures.Add("the members still belong to a forming lord after cancelling");
        if (_world.GetFormingCaravans().Any(f => lord != null && f.Id == lord.loadID)) failures.Add("the cancelled caravan is still listed");
        if (_world.CancelFormingCaravan(lord?.loadID ?? -12345).Success) failures.Add("cancelling a caravan that no longer exists succeeded");
        if (!silver.Spawned || silver.stackCount != 300 || !food.Spawned || food.stackCount != 40) failures.Add("cancelling changed the cargo on the ground");
        if (colonist.inventory.innerContainer.Any(t => t.def == ThingDefOf.Silver || t.def == foodDef)) failures.Add("the colonist still carries cargo after cancelling");
    }

    /// <summary>測試結束後的清理：停止集結、銷毀生成的物件、還原家園區、移除新信件。</summary>
    private static void CleanupCaravanTest(CaravanTestContext ctx)
    {
        Lord stuck = ctx.Colonist.GetLord();
        if (ctx.Lord != null && ctx.Lord.lordManager != null) CaravanFormingUtility.StopFormingCaravan(ctx.Lord);
        else if (stuck != null && stuck.LordJob is LordJob_FormAndSendCaravan) CaravanFormingUtility.StopFormingCaravan(stuck);
        foreach (Thing thing in ctx.Spawned.Where(t => !t.Destroyed)) thing.Destroy();
        // 放下的貨物若被撿了又放，清掉殖民者身上殘留的測試物品。
        foreach (Thing carried in ctx.Colonist.inventory.innerContainer.Where(t => t.def == ThingDefOf.Silver || t.def == ThingDefOf.Steel).ToList())
            carried.Destroy();
        if (ctx.CreatedAnimal && ctx.Animal != null && !ctx.Animal.Destroyed) ctx.Animal.Destroy();
        foreach (KeyValuePair<IntVec3, bool> before in ctx.HomeBefore) ctx.Map.areaManager.Home[before.Key] = before.Value;
        foreach (Letter letter in Find.LetterStack.LettersListForReading.Where(l => !ctx.LettersBefore.Contains(l)).ToList())
            Find.LetterStack.RemoveLetter(letter);
    }


    private void CheckCaravanCargoQuery(Thing silver, Thing food, List<string> failures)
    {
        IReadOnlyList<CaravanCargoInfo> cargo = _world.GetCaravanCargo();
        CaravanCargoInfo silverInfo = cargo.FirstOrDefault(c => c.DefName == silver.def.defName);
        CaravanCargoInfo foodInfo = cargo.FirstOrDefault(c => c.DefName == food.def.defName);
        if (MissingOrTooFew(silverInfo, 300)) failures.Add("the cargo query does not list the 300 silver we put in the home area");
        else if (MassMismatch(silverInfo, silver.def.BaseMass)) failures.Add("silver mass per unit differs from the def");
        if (MissingOrTooFew(foodInfo, 40)) failures.Add("the cargo query does not list the food we put in the home area");
        if (HasEmptyEntry(cargo)) failures.Add("the cargo query lists an empty entry");
    }

    private static bool MissingOrTooFew(CaravanCargoInfo info, int min) => info == null || info.Count < min;

    private static bool MassMismatch(CaravanCargoInfo info, float expected) => Math.Abs(info.MassEach - expected) > 0.0001f;

    private static bool HasEmptyEntry(IReadOnlyList<CaravanCargoInfo> cargo) => cargo.Any(c => string.IsNullOrEmpty(c.DefName) || c.Count <= 0);

#pragma warning disable S107 // 私有輔助方法，參數皆為規劃對照相關，無需重構
    /// <summary>規劃的數字逐項對照原版計算器（用測試自己組的 transferable，不經轉接層的草稿）。</summary>
    private static void CheckPlanMatchesVanilla(CaravanPlanInfo plan, Pawn colonist, Pawn animal, Thing silver, Thing food,
        PlanetTile destination, Map map, List<string> failures)
    {
        var pawns = new List<Pawn> { colonist, animal };
        var transferables = new List<TransferableOneWay>();
        foreach (Pawn pawn in pawns)
        {
            var transferable = new TransferableOneWay();
            transferable.things.Add(pawn);
            transferable.AdjustTo(1);
            transferables.Add(transferable);
        }
        var silverRow = new TransferableOneWay();
        silverRow.things.Add(silver);
        silverRow.AdjustTo(100);
        var foodRow = new TransferableOneWay();
        foodRow.things.Add(food);
        foodRow.AdjustTo(30);
        transferables.Add(silverRow);
        transferables.Add(foodRow);

        const IgnorePawnsInventoryMode mode = IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload;
        float usage = CollectionsMassCalculator.MassUsageTransferables(transferables, mode, false, false);
        float capacity = CollectionsMassCalculator.CapacityTransferables(transferables, new System.Text.StringBuilder());
        PlanetTile start = CaravanExitMapUtility.BestExitTileToGoTo(destination, map);
        int ticksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(pawns, usage, capacity, false, null);
        float tilesPerDay = TilesPerDayCalculator.ApproxTilesPerDay(transferables, usage, capacity, map.Tile, start, false, null);
        float visibility = CaravanVisibilityCalculator.Visibility(transferables, new System.Text.StringBuilder());
        var foraged = ForagedFoodPerDayCalculator.ForagedFoodPerDay(transferables, map.Biome, Faction.OfPlayer, new System.Text.StringBuilder());
        float foodDays;
        int eta;
        using (WorldPath path = map.Tile.Layer.Pather.FindPath(map.Tile, destination, null))
        {
            foodDays = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transferables, map.Tile, mode, Faction.OfPlayer, path, 0f, ticksPerMove);
            eta = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(map.Tile, destination, path, 0f, ticksPerMove, Find.TickManager.TicksAbs);
        }

        CheckPlanNumbers(plan, usage, capacity, ticksPerMove, tilesPerDay, foodDays, visibility, foraged.Item2, eta, failures);
        if (plan.DestinationTile != destination.tileId || !plan.DestinationReachable) failures.Add("the plan does not report the destination as reachable");
        if (plan.Members.Count != 2 || plan.Members.All(m => m.Kind != CaravanMemberInfo.AnimalKind)) failures.Add("the plan does not list the animal as an animal");
        if (plan.Cargo.Sum(c => c.Count) != 130) failures.Add("the plan lists " + plan.Cargo.Sum(c => c.Count) + " cargo items, expected 130");
        // 食物不夠五天時，原版會彈出警告框；兩邊要一致。
        bool warnsAboutFood = plan.Warnings.Contains("DaysWorthOfFoodWarningDialog".Translate(foodDays.ToString("0.#")).Resolve())
                              || plan.Warnings.Contains("DaysWorthOfFoodWarningDialog_NoFood".Translate().Resolve());
        if ((foodDays < 5f) != warnsAboutFood) failures.Add($"food warning {warnsAboutFood} with {foodDays} days of food");
    }

#pragma warning restore S107

#pragma warning disable S107 // 私有輔助方法，參數皆為對照原版數值，無需重構
    private static void CheckPlanNumbers(CaravanPlanInfo plan, float usage, float capacity, int ticksPerMove, float tilesPerDay, float foodDays, float visibility, float foragedPerDay, int etaTicks, List<string> failures)
    {
        if (Math.Abs(plan.MassUsage - usage) > 0.01f) failures.Add($"mass usage {plan.MassUsage}, vanilla {usage}");
        if (Math.Abs(plan.MassCapacity - capacity) > 0.01f) failures.Add($"mass capacity {plan.MassCapacity}, vanilla {capacity}");
        if (plan.TicksPerMove != ticksPerMove) failures.Add($"ticks per move {plan.TicksPerMove}, vanilla {ticksPerMove}");
        if (Math.Abs(plan.TilesPerDay - tilesPerDay) > 0.01f) failures.Add($"tiles per day {plan.TilesPerDay}, vanilla {tilesPerDay}");
        if (Math.Abs(plan.FoodDays - foodDays) > 0.05f) failures.Add($"days of food {plan.FoodDays}, vanilla {foodDays}");
        if (Math.Abs(plan.Visibility - visibility) > 0.001f) failures.Add($"visibility {plan.Visibility}, vanilla {visibility}");
        if (Math.Abs(plan.ForagedPerDay - foragedPerDay) > 0.001f) failures.Add($"foraged per day {plan.ForagedPerDay}, vanilla {foragedPerDay}");
        if (Math.Abs(plan.EtaTicks - etaTicks) > 60) failures.Add($"arrival estimate {plan.EtaTicks} ticks, vanilla {etaTicks}");
    }
#pragma warning restore S107
}
