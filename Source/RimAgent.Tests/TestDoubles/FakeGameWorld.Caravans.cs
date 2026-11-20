using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
    /// <summary>殖民地能裝車的物品（正式端讀倉儲與家園區）。</summary>
    public List<CaravanCargoInfo> CaravanCargo { get; } =
    [
        new() { DefName = "Silver", Label = "silver", Count = 800, MassEach = 0.008f },
        new() { DefName = "Pemmican", Label = "pemmican", Count = 200, MassEach = 0.05f },
        new() { DefName = "MedicineIndustrial", Label = "medicine", Count = 12, MassEach = 0.5f },
        new() { DefName = "Bedroll", Label = "bedroll", Count = 3, MassEach = 7f }
    ];

    /// <summary>規劃查詢收到的請求（查詢不記進 <see cref="Calls"/>，那裡只放會動世界的命令）。</summary>
    public List<string> PlanCalls { get; } = [];

    /// <summary>還在集結的商隊。</summary>
    public List<FormingCaravanInfo> Forming { get; } = [];

    /// <summary>設定後 <see cref="PlanCaravan"/> 與 <see cref="FormCaravan"/> 直接用它（測試想要特定數字或拒絕原因時）。</summary>
    public CaravanPlanInfo PlanOverride { get; set; }

    private int _nextFormingId = 70;

    /// <summary>簡化的組隊規劃：每位殖民者承重 40 公斤、動物 120 公斤，其餘數字固定；拒絕原因照原版的幾個檢查。</summary>
    private CaravanPlanInfo BuildPlan(CaravanRequest request)
    {
        if (PlanOverride != null) return PlanOverride;
        var members = new List<CaravanMemberInfo>();
        foreach (int id in (request.PawnIds ?? []).Distinct())
        {
            ColonistInfo colonist = Colonists.FirstOrDefault(c => c.Id == id);
            AnimalInfo animal = Animals.FirstOrDefault(a => a.Id == id);
            PrisonerInfo prisoner = Prisoners.FirstOrDefault(p => p.Id == id);
            if (colonist != null)
            {
                if (colonist.Drafted) return new CaravanPlanInfo { Error = $"{colonist.Name} is drafted; undraft them first" };
                members.Add(new CaravanMemberInfo { Id = id, Name = colonist.Name, Kind = CaravanMemberInfo.ColonistKind, Downed = colonist.Downed });
            }
            else if (animal != null && !animal.Wild)
            {
                members.Add(new CaravanMemberInfo { Id = id, Name = animal.Name, Kind = CaravanMemberInfo.AnimalKind, Downed = animal.Downed });
            }
            else if (prisoner != null)
            {
                members.Add(new CaravanMemberInfo { Id = id, Name = prisoner.Name, Kind = CaravanMemberInfo.PrisonerKind, Downed = prisoner.Downed });
            }
            else
            {
                return new CaravanPlanInfo { Error = string.Create(CultureInfo.InvariantCulture, $"no pawn with id {id} is on this map") };
            }
        }

        var cargo = new List<CaravanPlanCargo>();
        foreach (TradeLine line in request.Cargo ?? [])
        {
            CaravanCargoInfo have = CaravanCargo.FirstOrDefault(c => string.Equals(c.DefName, line.DefName, StringComparison.OrdinalIgnoreCase));
            if (have == null || have.Count < line.Count)
            {
                return new CaravanPlanInfo { Error = string.Create(CultureInfo.InvariantCulture, $"the colony can only supply {have?.Count ?? 0} {line.DefName} for the caravan (asked for {line.Count})") };
            }
            cargo.Add(new CaravanPlanCargo { DefName = have.DefName, Label = have.Label, Count = line.Count, MassEach = have.MassEach });
        }

        float usage = cargo.Sum(c => c.MassEach * c.Count);
        float capacity = members.Count(m => m.Kind != CaravanMemberInfo.AnimalKind) * 40f + members.Count(m => m.Kind == CaravanMemberInfo.AnimalKind) * 120f;
        bool pemmican = cargo.Exists(c => c.DefName == "Pemmican");
        var blockers = new List<string>();
        var warnings = new List<string>();
        if (request.DestinationTile < 0) blockers.Add("You must choose a route first.");
        if (!members.Exists(m => m.Kind == CaravanMemberInfo.ColonistKind && !m.Downed)) blockers.Add("A caravan needs at least one colonist who is not downed.");
        if (usage > capacity) blockers.Add("Too big caravan mass usage.");
        float foodDays = pemmican ? cargo.First(c => c.DefName == "Pemmican").Count / 4f : 0f;
        if (foodDays < 5f) warnings.Add(foodDays < 0.1f ? "The caravan has no food." : string.Create(CultureInfo.InvariantCulture, $"The caravan only has {foodDays:0.#} days' worth of food."));
        return new CaravanPlanInfo
        {
            Members = members,
            Cargo = cargo,
            MassUsage = usage,
            MassCapacity = capacity,
            TilesPerDay = 15f,
            TicksPerMove = 4000,
            FoodDays = foodDays,
            DaysUntilRot = pemmican ? 90f : 600f,
            ForagedFoodLabel = "berries",
            ForagedPerDay = members.Count > 1 ? 1.5f : 0f,
            Visibility = 0.4f,
            DestinationTile = request.DestinationTile,
            DestinationLabel = request.DestinationTile < 0 ? string.Empty : "tile " + request.DestinationTile.ToString(CultureInfo.InvariantCulture) + " (Temperate forest)",
            DistanceTiles = request.DestinationTile < 0 ? 0f : 12f,
            EtaTicks = request.DestinationTile < 0 ? 0 : 120000,
            DestinationReachable = request.DestinationTile >= 0,
            Blockers = blockers,
            Warnings = warnings
        };
    }

    public IReadOnlyList<CaravanCargoInfo> GetCaravanCargo() => CaravanCargo;

    public CaravanPlanInfo PlanCaravan(CaravanRequest request)
    {
        PlanCalls.Add(Describe(request));
        return request == null ? new CaravanPlanInfo { Error = "no caravan requested" } : BuildPlan(request);
    }

    public CommandResult FormCaravan(CaravanRequest request)
    {
        Calls.Add($"FormCaravan({Describe(request)})");
        if (request == null) return CommandResult.Fail("no caravan requested");
        CaravanPlanInfo plan = BuildPlan(request);
        if (!string.IsNullOrEmpty(plan.Error)) return CommandResult.Fail(plan.Error);
        if (plan.Blockers.Count > 0) return CommandResult.Fail(string.Join("; ", plan.Blockers.ToArray()));
        Forming.Add(new FormingCaravanInfo
        {
            Id = _nextFormingId++,
            Status = "gathering items",
            Members = plan.Members,
            Cargo = [.. plan.Cargo.Select(c => c.Label + " x" + c.Count.ToString(CultureInfo.InvariantCulture))],
            ExitX = 0,
            ExitZ = 30
        });
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"Started forming a caravan with {string.Join(", ", plan.Members.Select(m => m.Name).ToArray())} for tile {request.DestinationTile}"));
    }

    private static string Describe(CaravanRequest request)
    {
        if (request == null) return "null";
        return $"{string.Join('+', (request.PawnIds ?? []).ToArray())}|" +
               string.Create(CultureInfo.InvariantCulture, $"{string.Join('+', (request.Cargo ?? []).Select(l => l.DefName + ":" + l.Count.ToString(CultureInfo.InvariantCulture)).ToArray())}|{request.DestinationTile}");
    }

    public IReadOnlyList<FormingCaravanInfo> GetFormingCaravans() => Forming;

    public CommandResult CancelFormingCaravan(int formingId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"CancelFormingCaravan({formingId})"));
        FormingCaravanInfo forming = Forming.FirstOrDefault(f => f.Id == formingId);
        if (forming == null) return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"no caravan is being formed with id {formingId}"));
        Forming.Remove(forming);
        return CommandResult.Ok("Stopped forming the caravan");
    }

    public IReadOnlyList<CaravanInfo> GetCaravans() => Caravans;

    public WorldSurveyInfo GetWorldSurvey(int originCaravanId) => Survey;

    public IReadOnlyList<TravelEstimateInfo> GetTravelEstimates(int originCaravanId, IReadOnlyList<int> tiles)
    {
        return [.. tiles.Select(t => TravelEstimates.TryGetValue(t, out TravelEstimateInfo e)
            ? e
            : new TravelEstimateInfo { Tile = t, Reachable = true, Ticks = 60000, DistanceTiles = 10f })];
    }

    public WorldTileInfo GetWorldTile(int tile, int originCaravanId)
    {
        if (tile > 99999) return new WorldTileInfo { Tile = tile, Error = string.Create(CultureInfo.InvariantCulture, $"tile {tile} does not exist on this layer of the world") };
        return WorldTiles.TryGetValue(tile, out WorldTileInfo info)
            ? info
            : new WorldTileInfo { Tile = tile, Biome = "Temperate forest", Hilliness = "Flat", TemperatureC = 14f, MinTemperatureC = -8f, MaxTemperatureC = 31f, RainfallMm = 900f, ElevationM = 300f, CanSettle = true };
    }

    public CaravanOptionsInfo GetCaravanOptions(int caravanId, int tile)
    {
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return new CaravanOptionsInfo { Error = "caravan not found" };
        if (!string.IsNullOrEmpty(CaravanOrdersError)) return new CaravanOptionsInfo { Error = CaravanOrdersError, CaravanName = caravan.Name };
        return new CaravanOptionsInfo { CaravanName = caravan.Name, Options = CaravanOrders };
    }

    public CommandResult SendCaravan(int caravanId, int tile, int optionNumber, string label)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SendCaravan({caravanId},{tile},{optionNumber},{label})"));
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return CommandResult.Fail("caravan not found");
        FloatMenuOptionInfo option = CaravanOrders.FirstOrDefault(o => o.Number == optionNumber);
        if (option == null || !string.Equals(option.Label, label, StringComparison.OrdinalIgnoreCase)) return CommandResult.Fail("orders changed");
        if (option.Disabled) return CommandResult.Fail("disabled");
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
            return CommandResult.Ok($"\"{label}\" is waiting for an answer");
        }
        caravan.Moving = true;
        caravan.Paused = false;
        caravan.DestinationTile = tile;
        caravan.DestinationLabel = "tile " + tile.ToString(CultureInfo.InvariantCulture);
        caravan.EtaTicks = 150000;
        return CommandResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{caravan.Name} is on its way to tile {tile}"));
    }

    public CommandResult SetCaravanMovement(int caravanId, string action)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SetCaravanMovement({caravanId},{action})"));
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return CommandResult.Fail("caravan not found");
        if (!caravan.Moving) return CommandResult.Fail($"{caravan.Name} has no destination");
        if (action == CaravanMovementAction.Pause)
        {
            caravan.Paused = true;
        }
        else if (action == CaravanMovementAction.Resume)
        {
            caravan.Paused = false;
        }
        else
        {
            caravan.Moving = false;
            caravan.Paused = false;
            caravan.DestinationTile = -1;
        }
        return CommandResult.Ok($"{caravan.Name} {action}");
    }

    public CommandResult SettleCaravan(int caravanId, string action)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"SettleCaravan({caravanId},{action})"));
        CaravanInfo caravan = Caravans.FirstOrDefault(c => c.Id == caravanId);
        if (caravan == null) return CommandResult.Fail("caravan not found");
        if (FollowUpDialog != null)
        {
            PendingChoices.Add(FollowUpDialog);
            FollowUpDialog = null;
            return CommandResult.Ok("The game asks to confirm");
        }
        return CommandResult.Ok($"{caravan.Name} is starting to {action}");
    }
}
