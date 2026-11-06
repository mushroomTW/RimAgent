using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 世界地圖上的商隊：查看商隊與世界物件（定居點、據點、任務地點）、查一格的地形、派商隊前往並選抵達動作、
/// 暫停與停下、就地建立殖民地或營地。離開地圖組隊仍是 form_caravan；抵達後進入地圖就是對商隊所在的格子下「進入」命令。
/// </summary>
public sealed class WorldTools
{
    internal const int DefaultObjectLimit = 20;
    internal const int MaxObjectLimit = 100;
    private const int TicksPerHour = 2500;
    private const int TicksPerDay = 60000;

    private static readonly string[] ObjectKinds = ["all", "player", "faction", "site", "quest", "other"];

    private readonly IGameWorld _world;
    private readonly ConfirmationGate _gate;

    public WorldTools(IGameWorld world, ConfirmationGate gate = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gate = gate;
    }

    [AgentTool("list_caravans", ToolCategory.Trade, ToolRisk.Query)]
    [Description("Lists your caravans on the world map: id, tile, members (kind, health, downed), whether it is travelling, paused, resting or stuck, destination with arrival time, days of food left, mass carried against capacity, and whether it can found a colony or set up camp where it stands. Caravans still forming on the map (members gathering cargo, not left yet) are listed after them with their status. Use the caravan id or name with get_world_tile, send_caravan, set_caravan_movement and settle_caravan.")]
    public string ListCaravans()
    {
        IReadOnlyList<CaravanInfo> caravans = _world.GetCaravans();
        IReadOnlyList<FormingCaravanInfo> forming = _world.GetFormingCaravans();
        if (caravans.Count == 0 && forming.Count == 0)
            return "You have no caravans on the world map. A caravan is formed with form_caravan.";
        var sb = new StringBuilder();
        if (caravans.Count == 0) sb.AppendLine("No caravans on the world map yet.");
        else
        {
            sb.Append(caravans.Count).Append(caravans.Count == 1 ? " caravan:" : " caravans:").AppendLine();
            foreach (CaravanInfo c in caravans) AppendCaravan(sb, c);
        }
        if (forming.Count > 0)
        {
            sb.Append(forming.Count).Append(forming.Count == 1 ? " forming caravan" : " forming caravans")
              .AppendLine(" still on the map (not left yet; cancel_forming_caravan aborts):");
            foreach (FormingCaravanInfo f in forming) AppendForming(sb, f);
        }
        return sb.ToString().Trim();
    }

    [AgentTool("list_world_objects", ToolCategory.Trade, ToolRisk.Query)]
    [Description("Lists world map objects of interest nearest first: your settlements and camps, other factions' settlements (faction, hostile or not), sites including quest sites (linked to their quest id), and other objects. Each shows its id, tile, distance in tiles and the game's travel-time estimate from the origin. The origin is the named caravan, or the current map's tile when none is named (travel times then assume a default-speed caravan). Quest sites of a quest are found with kind \"quest\". Inspect one with get_world_tile.")]
    public string ListWorldObjects(
        [Description("Caravan name or id to measure from; empty uses the only caravan if there is exactly one, otherwise the current map's tile.")] string caravan = "",
        [Description("Filter: all, player (your settlements and camps), faction (other factions' settlements), site, quest (anything a quest points at) or other.")] string kind = "all",
        [Description("Only objects whose name, faction, kind or linked quest contains this text; empty for no filter.")] string search = "",
        [Description("How many to list, nearest first (1-100).")] int limit = DefaultObjectLimit)
    {
        Result<CaravanInfo> from = ResolveCaravan(caravan, _world.GetCaravans(), required: false);
        if (!from.Success) return from.Error;
        string filter = string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();
        if (!ObjectKinds.Contains(filter))
            return ToolText.Error($"unknown kind \"{kind}\". Use {string.Join(", ", ObjectKinds)}.");
        int max = Math.Max(1, Math.Min(MaxObjectLimit, limit));
        int originId = from.Value?.Id ?? -1;

        WorldSurveyInfo survey = _world.GetWorldSurvey(originId);
        string needle = (search ?? string.Empty).Trim();
        List<WorldObjectInfo> matches = [.. survey.Objects
            .Where(o => MatchesKind(o, filter) && MatchesSearch(o, needle))
            .OrderBy(o => o.DistanceTiles).ThenBy(o => o.Id)];
        string origin = DescribeOrigin(survey);
        if (matches.Count == 0)
            return $"No matching world objects ({survey.Objects.Count} known in total). Distances are from {origin}.";

        List<WorldObjectInfo> shown = [.. matches.Take(max)];
        Dictionary<int, TravelEstimateInfo> travel = TravelByTile(originId, shown.Select(o => o.Tile));
        var sb = new StringBuilder();
        sb.Append(shown.Count == matches.Count ? $"{matches.Count} world objects" : $"Nearest {shown.Count} of {matches.Count} world objects")
          .Append(" from ").Append(origin).Append(survey.OriginIsCaravan ? string.Empty : "; travel times assume a default-speed caravan").AppendLine(":");
        foreach (WorldObjectInfo o in shown)
        {
            travel.TryGetValue(o.Tile, out TravelEstimateInfo estimate);
            sb.Append("- ").AppendLine(FormatObject(o, estimate));
        }
        return sb.ToString().Trim();
    }

    [AgentTool("get_world_tile", ToolCategory.Trade, ToolRisk.Query)]
    [Description("Inspects one world tile: biome, hilliness, temperature, rainfall, elevation, whether a colony can be founded there and why not, the world objects on it, and the route and travel time from the origin. Name a caravan and it also lists the orders that caravan can be given for this tile, numbered for send_caravan (travel there, enter, visit, trade, attack...). Give a tile id or a world object id; with neither, the caravan's own tile (or the current map's tile) is inspected.")]
    public string GetWorldTile(
        [Description("Tile id; -1 to use worldObjectId or the origin's own tile.")] int tile = -1,
        [Description("World object id (from list_world_objects or list_caravans) whose tile to inspect; -1 for none.")] int worldObjectId = -1,
        [Description("Caravan name or id to measure from and list orders for; empty uses the only caravan if there is exactly one, otherwise no orders are listed.")] string caravan = "")
    {
        IReadOnlyList<CaravanInfo> caravans = _world.GetCaravans();
        Result<CaravanInfo> from = ResolveCaravan(caravan, caravans, required: false);
        if (!from.Success) return from.Error;
        int originId = from.Value?.Id ?? -1;
        Result<int> target = ResolveTile(tile, worldObjectId, caravans, originId);
        if (!target.Success) return target.Error;
        int tileId = target.Value;
        if (tileId < 0)
        {
            tileId = from.Value != null ? from.Value.Tile : (_world.GetMaps().FirstOrDefault(m => m.IsCurrent)?.Tile ?? -1);
            if (tileId < 0) return ToolText.Error("give a tile id or a world object id.");
        }

        WorldTileInfo info = _world.GetWorldTile(tileId, originId);
        if (!string.IsNullOrEmpty(info.Error)) return ToolText.Error(info.Error);
        WorldSurveyInfo survey = _world.GetWorldSurvey(originId);
        TravelEstimateInfo estimate = TravelByTile(originId, new[] { tileId }).Values.FirstOrDefault();

        var sb = new StringBuilder();
        sb.Append("Tile ").Append(string.IsNullOrEmpty(info.TileLayer) ? string.Empty : info.TileLayer + ":").Append(info.Tile).Append(": ").Append(info.Biome).Append(", ").Append(info.Hilliness);
        if (!string.IsNullOrEmpty(info.Feature)) sb.Append(" | feature: ").Append(info.Feature);
        if (!string.IsNullOrEmpty(info.Landmark)) sb.Append(" | landmark: ").Append(info.Landmark);
        if (info.Mutators.Count > 0) sb.Append(" | ").Append(string.Join(", ", info.Mutators.ToArray()));
        sb.AppendLine();
        sb.Append("Temperature ").Append(ToolText.Num(info.TemperatureC)).Append("C on average (").Append(ToolText.Num(info.MinTemperatureC))
          .Append("C to ").Append(ToolText.Num(info.MaxTemperatureC)).Append("C), rainfall ").Append(ToolText.Num(info.RainfallMm))
          .Append(" mm, elevation ").Append(ToolText.Num(info.ElevationM)).Append(" m");
        if (info.Coastal) sb.Append(", coastal");
        if (info.WaterCovered) sb.Append(", water");
        if (info.Impassable) sb.Append(", impassable");
        sb.AppendLine();
        sb.Append("A colony can be founded here: ").AppendLine(info.CanSettle ? "yes" : "no" + (string.IsNullOrEmpty(info.SettleBlockedReason) ? string.Empty : " (" + info.SettleBlockedReason + ")"));
        sb.Append("From ").Append(DescribeOrigin(survey)).Append(": ").AppendLine(estimate == null ? "unknown" : Travel(estimate));
        if (info.Objects.Count == 0) sb.AppendLine("No world objects on this tile.");
        else
        {
            sb.AppendLine("World objects here:");
            foreach (WorldObjectInfo o in info.Objects) sb.Append("- ").AppendLine(FormatObject(o, null));
        }

        if (from.Value != null)
        {
            CaravanOptionsInfo options = _world.GetCaravanOptions(from.Value.Id, tileId);
            if (!string.IsNullOrEmpty(options.Error)) sb.Append("Orders: ").AppendLine(options.Error);
            else
            {
                sb.Append("Orders for ").Append(from.Value.Name).AppendLine(" on this tile (send_caravan):");
                foreach (FloatMenuOptionInfo o in options.Options) sb.AppendLine(FormatOption(o));
            }
        }
        return sb.ToString().Trim();
    }

    [AgentTool("send_caravan", ToolCategory.Trade, ToolRisk.Confirm)]
    [Description("Sends one of your caravans to a world tile or world object and does what the game's world right-click menu offers there: plain travel, enter or visit a settlement or site, trade, offer gifts, attack and so on. List the numbered orders with get_world_tile (same caravan and destination), then pass the one you want as \"2) label\" so a changed menu is caught. To enter the map on the tile the caravan already stands on, pick the enter order for that tile. Travel takes game time: use wait_for_time and check list_caravans. Attacking or entering hostile places is dangerous. Trading with a settlement opens the game's trade window for the player on arrival, which you cannot operate. A confirmation dialog the game raises is answered with choose_option.")]
    public string SendCaravan(
        [Description("Caravan name or id; may be empty when there is exactly one caravan.")] string caravan,
        [Description("The order: number from get_world_tile, ideally with its label, e.g. \"2) Enter Foo Camp\". A bare number is taken from the current menu.")] string option,
        [Description("Destination tile id; -1 to use worldObjectId.")] int tile = -1,
        [Description("Destination world object id; -1 to use tile.")] int worldObjectId = -1)
    {
        IReadOnlyList<CaravanInfo> caravans = _world.GetCaravans();
        Result<CaravanInfo> picked = ResolveCaravan(caravan, caravans, required: true);
        if (!picked.Success) return picked.Error;
        CaravanInfo c = picked.Value;
        Result<int> target = ResolveTile(tile, worldObjectId, caravans, c.Id);
        if (!target.Success) return target.Error;
        if (target.Value < 0) return ToolText.Error("give a destination: tile (a tile id) or worldObjectId.");

        CaravanOptionsInfo options = _world.GetCaravanOptions(c.Id, target.Value);
        if (!string.IsNullOrEmpty(options.Error)) return ToolText.Error(options.Error);
        Result<FloatMenuOptionInfo> chosen = ResolveOption(option, options.Options);
        if (!chosen.Success) return chosen.Error;
        FloatMenuOptionInfo order = chosen.Value;
        if (order.Disabled) return ToolText.Error($"\"{order.Label}\" is disabled (the game's reason is in the label).");

        string blocked = ToolText.Blocked(_gate, $"would send {c.Name} [{c.Id}] to tile {target.Value}: \"{order.Label}\".");
        if (blocked != null) return blocked;

        CommandResult result = _world.SendCaravan(c.Id, target.Value, order.Number, order.Label);
        if (!result.Success) return ToolText.Error(result.Message);
        string text = $"Ordered {c.Name}: \"{order.Label}\" on tile {target.Value}. {result.Message}";
        CaravanInfo now = _world.GetCaravans().FirstOrDefault(x => x.Id == c.Id);
        if (now != null && now.Moving && now.EtaTicks > 0) text += $"; arrival in {Duration(now.EtaTicks)}";
        return text + "." + GizmoTools.OpenDialogs(_world);
    }

    [AgentTool("set_caravan_movement", ToolCategory.Trade, ToolRisk.Safe)]
    [Description("Pauses, resumes or stops a travelling caravan, like the pause button of its world map gizmo. pause keeps the destination and the arrival action, resume carries on, stop clears the destination (send_caravan again to go somewhere). The caravan stays where it stands.")]
    public string SetCaravanMovement(
        [Description("Caravan name or id; may be empty when there is exactly one caravan.")] string caravan,
        [Description("pause, resume or stop.")] string action)
    {
        Result<CaravanInfo> picked = ResolveCaravan(caravan, _world.GetCaravans(), required: true);
        if (!picked.Success) return picked.Error;
        CaravanInfo c = picked.Value;
        string verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (verb != CaravanMovementAction.Pause && verb != CaravanMovementAction.Resume && verb != CaravanMovementAction.Stop)
            return ToolText.Error($"unknown action \"{action}\". Use pause, resume or stop.");

        string blocked = ToolText.Blocked(_gate, $"would {verb} {c.Name} [{c.Id}] on tile {c.Tile}.");
        if (blocked != null) return blocked;

        CommandResult result = _world.SetCaravanMovement(c.Id, verb);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message;
    }

    [AgentTool("settle_caravan", ToolCategory.Trade, ToolRisk.Confirm)]
    [Description("Makes a caravan found a new colony on the tile it stands on (action \"settle\"), or set up a camp there (action \"camp\", a small map for resting or fighting). The whole caravan enters the new map. Check list_caravans or get_world_tile first: the game refuses on unsuitable tiles, next to another settlement, or past the colony limit, and the reason is shown. Settling near other factions costs goodwill and the game asks to confirm first (choose_option). The map is generated in the background and then shows up in get_maps; switch to it with set_current_map.")]
    public string SettleCaravan(
        [Description("Caravan name or id; may be empty when there is exactly one caravan.")] string caravan,
        [Description("settle or camp.")] string action)
    {
        Result<CaravanInfo> picked = ResolveCaravan(caravan, _world.GetCaravans(), required: true);
        if (!picked.Success) return picked.Error;
        CaravanInfo c = picked.Value;
        string verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (verb != CaravanSettleAction.Settle && verb != CaravanSettleAction.Camp)
            return ToolText.Error($"unknown action \"{action}\". Use settle or camp.");
        bool camp = verb == CaravanSettleAction.Camp;
        if (camp ? !c.CanCamp : !c.CanSettle)
        {
            string reason = camp ? c.CampBlockedReason : c.SettleBlockedReason;
            return ToolText.Error($"{c.Name} cannot {(camp ? "set up camp" : "found a colony")} on tile {c.Tile}" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason));
        }

        string members = string.Join(", ", c.Members.Select(m => m.Name).ToArray());
        string blocked = ToolText.Blocked(_gate, camp
            ? $"would set up camp with {c.Name} [{c.Id}] ({members}) on tile {c.Tile}."
            : $"would found a new colony with {c.Name} [{c.Id}] ({members}) on tile {c.Tile}; the whole caravan moves in.");
        if (blocked != null) return blocked;

        CommandResult result = _world.SettleCaravan(c.Id, verb);
        if (!result.Success) return ToolText.Error(result.Message);
        return result.Message + "." + GizmoTools.OpenDialogs(_world);
    }

    // ---- 解析 ----

    /// <summary>
    /// 用編號或名稱指定一支商隊。名稱先精確再包含比對；沒給名稱時只有一支就用它。
    /// <paramref name="required"/> 為 false 時沒有商隊或沒指名（且不只一支）回成功但值為 null（代表從當前地圖量）。
    /// </summary>
    internal static Result<CaravanInfo> ResolveCaravan(string input, IReadOnlyList<CaravanInfo> caravans, bool required)
    {
        string needle = (input ?? string.Empty).Trim();
        if (caravans.Count == 0)
        {
            return required
                ? Result<CaravanInfo>.Fail(ToolText.Error("you have no caravans on the world map. Form one with form_caravan."))
                : Result<CaravanInfo>.Ok(null);
        }
        if (needle.Length == 0)
        {
            if (caravans.Count == 1) return Result<CaravanInfo>.Ok(caravans[0]);
            return required
                ? Result<CaravanInfo>.Fail(ToolText.Error("name the caravan. Yours: " + Candidates(caravans)))
                : Result<CaravanInfo>.Ok(null);
        }
        if (int.TryParse(needle, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            CaravanInfo byId = caravans.FirstOrDefault(c => c.Id == id);
            return byId != null
                ? Result<CaravanInfo>.Ok(byId)
                : Result<CaravanInfo>.Fail(ToolText.Error($"no caravan has id {id}. Yours: {Candidates(caravans)}"));
        }
        List<CaravanInfo> exact = [.. caravans.Where(c => string.Equals(c.Name, needle, StringComparison.OrdinalIgnoreCase))];
        List<CaravanInfo> hits = exact.Count > 0 ? exact : [.. caravans.Where(c => c.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)];
        if (hits.Count == 1) return Result<CaravanInfo>.Ok(hits[0]);
        return Result<CaravanInfo>.Fail(ToolText.Error(hits.Count == 0
            ? $"no caravan is named \"{needle}\". Yours: {Candidates(caravans)}"
            : $"\"{needle}\" matches several caravans: {Candidates(hits)}. Use the id."));
    }

    private static string Candidates(IEnumerable<CaravanInfo> caravans)
    {
        return string.Join(", ", caravans.Select(c => "[" + c.Id + "] " + c.Name).ToArray());
    }

    /// <summary>
    /// 目的格：tile 或 worldObjectId 擇一（兩個都給必須指向同一格）；都沒給回成功且值為 -1。
    /// worldObjectId 可以是世界物件，也可以是自己的商隊。
    /// </summary>
    private Result<int> ResolveTile(int tile, int worldObjectId, IReadOnlyList<CaravanInfo> caravans, int originCaravanId)
    {
        return ResolveTile(_world, tile, worldObjectId, caravans, originCaravanId);
    }

    internal static Result<int> ResolveTile(IGameWorld world, int tile, int worldObjectId, IReadOnlyList<CaravanInfo> caravans, int originCaravanId)
    {
        if (worldObjectId < 0) return Result<int>.Ok(tile >= 0 ? tile : -1);
        int objectTile;
        CaravanInfo own = caravans.FirstOrDefault(c => c.Id == worldObjectId);
        if (own != null) objectTile = own.Tile;
        else
        {
            WorldObjectInfo obj = world.GetWorldSurvey(originCaravanId).Objects.FirstOrDefault(o => o.Id == worldObjectId);
            if (obj == null)
                return Result<int>.Fail(ToolText.Error($"no world object has id {worldObjectId}. Use list_world_objects to get valid ids."));
            objectTile = obj.Tile;
        }
        if (tile >= 0 && tile != objectTile)
            return Result<int>.Fail(ToolText.Error($"world object {worldObjectId} is on tile {objectTile}, not tile {tile}. Give one of them."));
        return Result<int>.Ok(objectTile);
    }

    /// <summary>
    /// 解析命令序號：「2) 標籤」「2: 標籤」「2 標籤」要求序號與標籤對得上現在的清單（選單變了就擋下），
    /// 單獨的數字照現在的序號；純文字則標籤完全相同，再退而求其次找唯一一個包含這段文字的。
    /// </summary>
    internal static Result<FloatMenuOptionInfo> ResolveOption(string input, IReadOnlyList<FloatMenuOptionInfo> options)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return Result<FloatMenuOptionInfo>.Fail(ToolText.Error("give the order: a number from get_world_tile."));
        Match m = Regex.Match(text, @"^(\d+)\s*[\)\.:\-]?\s*(.*)$", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (m.Success)
        {
            int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string label = m.Groups[2].Value.Trim();
            FloatMenuOptionInfo byNumber = options.FirstOrDefault(o => o.Number == number);
            if (byNumber == null)
                return Result<FloatMenuOptionInfo>.Fail(ToolText.Error($"there is no order {number}. Orders now: {OrderList(options)}"));
            if (label.Length > 0 && byNumber.Label.IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0)
                return Result<FloatMenuOptionInfo>.Fail(ToolText.Error($"order {number} is now \"{byNumber.Label}\", not \"{label}\": the menu changed. Orders now: {OrderList(options)}"));
            return Result<FloatMenuOptionInfo>.Ok(byNumber);
        }
        List<FloatMenuOptionInfo> exact = [.. options.Where(o => string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase))];
        List<FloatMenuOptionInfo> hits = exact.Count > 0 ? exact : [.. options.Where(o => o.Label.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)];
        if (hits.Count == 1) return Result<FloatMenuOptionInfo>.Ok(hits[0]);
        return Result<FloatMenuOptionInfo>.Fail(ToolText.Error(hits.Count == 0
            ? $"no order matches \"{text}\". Orders now: {OrderList(options)}"
            : $"\"{text}\" matches several orders. Use a number. Orders now: {OrderList(options)}"));
    }

    private static string OrderList(IReadOnlyList<FloatMenuOptionInfo> options)
    {
        return options.Count == 0 ? "none" : string.Join("; ", options.Select(o => o.Number + ") " + o.Label).ToArray());
    }

    private Dictionary<int, TravelEstimateInfo> TravelByTile(int originCaravanId, IEnumerable<int> tiles)
    {
        var result = new Dictionary<int, TravelEstimateInfo>();
        foreach (TravelEstimateInfo e in _world.GetTravelEstimates(originCaravanId, tiles.Distinct().ToList()))
            result[e.Tile] = e;
        return result;
    }

    private static bool MatchesKind(WorldObjectInfo o, string filter)
    {
        switch (filter)
        {
            case "player": return o.Kind == WorldObjectInfo.PlayerSettlementKind || o.Kind == WorldObjectInfo.CampKind;
            case "faction": return o.Kind == WorldObjectInfo.FactionSettlementKind;
            case "site": return o.Kind == WorldObjectInfo.SiteKind;
            case "quest": return o.Quests.Count > 0;
            case "other": return o.Kind == WorldObjectInfo.CaravanKind || o.Kind == WorldObjectInfo.OtherKind;
            default: return true;
        }
    }

    private static bool MatchesSearch(WorldObjectInfo o, string needle)
    {
        if (needle.Length == 0) return true;
        return Contains(o.Label, needle) || Contains(o.Faction, needle) || Contains(o.Kind, needle) || Contains(o.DefName, needle)
               || Contains(o.Detail, needle) || o.Quests.Any(q => Contains(q.QuestName, needle));
    }

    private static bool Contains(string text, string needle)
    {
        return !string.IsNullOrEmpty(text) && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ---- 格式 ----

    private static string DescribeOrigin(WorldSurveyInfo survey)
    {
        return survey.OriginTile < 0 ? survey.OriginLabel : $"{survey.OriginLabel} (tile {survey.OriginTile})";
    }

    /// <summary>tick 換成白話：不到一天用小時，否則用天（遊戲一天 60000 tick）。</summary>
    internal static string Duration(int ticks)
    {
        if (ticks <= 0) return "no time";
        if (ticks < TicksPerDay) return "~" + Math.Max(1, (int)Math.Round(ticks / (double)TicksPerHour)).ToString(CultureInfo.InvariantCulture) + " h";
        string days = ToolText.Num(ticks / (float)TicksPerDay);
        return "~" + days + (days == "1" ? " day" : " days");
    }

    internal static string Travel(TravelEstimateInfo t)
    {
        if (!t.Reachable) return "no land route";
        if (t.Ticks <= 0) return "same tile";
        return ToolText.Num(t.DistanceTiles) + " tiles, " + Duration(t.Ticks);
    }

    internal static string FormatOption(FloatMenuOptionInfo o)
    {
        return o.Number + ") " + (string.IsNullOrEmpty(o.Label) ? "(unnamed)" : o.Label) + (o.Disabled ? " [disabled]" : string.Empty);
    }

    internal static string FormatObject(WorldObjectInfo o, TravelEstimateInfo travel)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(o.Id).Append("] ").Append(o.Label).Append(" (").Append(o.Kind);
        if (!string.IsNullOrEmpty(o.Faction)) sb.Append(", ").Append(o.Faction);
        if (o.Hostile) sb.Append(", hostile");
        sb.Append("), tile ").Append(string.IsNullOrEmpty(o.TileLayer) ? string.Empty : o.TileLayer + ":").Append(o.Tile);
        if (o.HasMap) sb.Append(", has a map");
        if (!string.IsNullOrEmpty(o.Detail)) sb.Append(", ").Append(o.Detail);
        if (o.ExpiresInTicks >= 0) sb.Append(", gone in ").Append(Duration(o.ExpiresInTicks));
        if (travel != null) sb.Append(" | ").Append(Travel(travel));
        foreach (WorldObjectQuestLink q in o.Quests)
            sb.Append(" | quest [").Append(q.QuestId).Append("] ").Append(q.QuestName).Append(q.Active ? " (active)" : " (not accepted yet)");
        return sb.ToString();
    }

    internal static string Status(CaravanInfo c)
    {
        var parts = new List<string>();
        if (c.Moving)
        {
            string text = (c.Paused ? "paused on the way to " : "travelling to ") + c.DestinationLabel;
            if (!string.IsNullOrEmpty(c.ArrivalAction)) text += ", then: " + c.ArrivalAction;
            if (c.EtaTicks > 0) text += ", arrival in " + Duration(c.EtaTicks);
            parts.Add(text);
        }
        else parts.Add("stopped, no destination");
        if (c.Resting) parts.Add("resting for the night");
        if (c.Immobilized) parts.Add("immobilized: overloaded");
        if (c.AllOwnersDowned) parts.Add("all members are downed");
        if (c.AllOwnersInMentalBreak) parts.Add("all members are in a mental break");
        return string.Join("; ", parts.ToArray());
    }

    private static void AppendCaravan(StringBuilder sb, CaravanInfo c)
    {
        sb.Append('[').Append(c.Id).Append("] ").Append(c.Name).Append(" | tile ").Append(c.Tile);
        if (!string.IsNullOrEmpty(c.Terrain)) sb.Append(" (").Append(c.Terrain).Append(')');
        sb.AppendLine();
        sb.Append("  status: ").AppendLine(Status(c));
        sb.Append("  food: ").Append(c.FoodDays >= 600f ? "600+ days" : ToolText.Num(c.FoodDays) + " days")
          .Append(" | mass ").Append(ToolText.Num(c.MassUsage)).Append('/').Append(ToolText.Num(c.MassCapacity)).Append(" kg")
          .Append(c.MassUsage > c.MassCapacity ? " (overloaded)" : string.Empty)
          .Append(" | ").Append(c.TicksPerMove).AppendLine(" ticks per tile");
        sb.Append("  members: ").AppendLine(string.Join(", ", c.Members.Select(FormatMember).ToArray()));
        sb.Append("  found a colony here: ").Append(c.CanSettle ? "yes" : "no" + Reason(c.SettleBlockedReason))
          .Append(" | set up camp here: ").AppendLine(c.CanCamp ? "yes" : "no" + Reason(c.CampBlockedReason));
        if (c.ObjectsHere.Count > 0) sb.Append("  here: ").AppendLine(string.Join(", ", c.ObjectsHere.ToArray()));
    }

    private static void AppendForming(StringBuilder sb, FormingCaravanInfo f)
    {
        sb.Append('[').Append(f.Id).Append("] ").Append(f.Status).Append(" | leaves the map at ").AppendLine(ToolText.Cell(f.ExitX, f.ExitZ));
        sb.Append("  members: ").AppendLine(string.Join(", ", f.Members.Select(FormatMember).ToArray()));
        sb.Append("  cargo: ").AppendLine(f.Cargo.Count == 0 ? "none" : string.Join(", ", f.Cargo.ToArray()));
    }

    private static string Reason(string reason)
    {
        return string.IsNullOrEmpty(reason) ? string.Empty : " (" + reason + ")";
    }

    private static string FormatMember(CaravanMemberInfo m)
    {
        var sb = new StringBuilder();
        sb.Append(m.Name).Append(" (").Append(m.Kind).Append(", health ").Append(ToolText.Pct(m.HealthPercent));
        if (m.Downed) sb.Append(", downed");
        if (m.InMentalState) sb.Append(", in a mental break");
        return sb.Append(')').ToString();
    }
}
