using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>
    /// 組商隊（照原版組隊視窗）：規劃、送出（成員到集合點收齊貨物再一起離開地圖）、中止集結。
    /// 商隊離開之後的世界地圖操作在 <see cref="WorldTools"/>。
    /// </summary>
    public sealed class CaravanTools
    {
        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public CaravanTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("plan_caravan", ToolCategory.Trade, ToolRisk.Query)]
        [Description("Plans a caravan without starting it. Shows what the game's Form Caravan window shows for these members and cargo: mass carried against capacity, travel speed, days of food and until it rots, foraging, visibility, and for a destination tile the distance and travel time; plus every reason the game would refuse to send it and every warning it would raise. Use it to size the cargo before form_caravan: more pack animals raise capacity and speed, food must cover the trip. Cargo can only come from items in colony storage or the home area that nobody has reserved (silver, food, medicine, goods; furniture such as bedrolls must be uninstalled first). Nothing is moved or reserved.")]
        public string PlanCaravan(
            [Description("Colonist names or ids separated by commas, e.g. \"Ali, Bob\" (\"all\" is refused). At least one must be able to walk.")] string colonists,
            [Description("Colony animals to take as pack animals: names or ids, or \"species x count\", e.g. \"muffalo x3, Rex\". Empty for none.")] string animals = "",
            [Description("Cargo as \"name x count\", comma-separated, e.g. \"silver x500, pemmican x80, medicine x6\". Empty for none.")] string cargo = "",
            [Description("Prisoners to take along (names or ids). They must be secured in a cell; usually empty.")] string prisoners = "",
            [Description("Destination tile id (from list_world_objects or get_world_tile); -1 to use worldObjectId or leave unset.")] int tile = -1,
            [Description("Destination world object id (from list_world_objects); -1 to use tile or leave unset.")] int worldObjectId = -1)
        {
            Result<Prepared> prepared = Prepare(colonists, animals, cargo, prisoners, tile, worldObjectId);
            if (!prepared.Success) return prepared.Error;
            CaravanPlanInfo plan = _world.PlanCaravan(prepared.Value.Request);
            if (!string.IsNullOrEmpty(plan.Error)) return ToolText.Error(plan.Error);
            return FormatPlan(plan);
        }

        [AgentTool("form_caravan", ToolCategory.Trade, ToolRisk.Confirm)]
        [Description("Forms a caravan the way the game's Form Caravan window does when the player presses Send: the members walk to a meeting point, gather the cargo, and then leave the map together towards the destination tile. This takes game time (use wait_for_time and list_caravans; the caravan appears on the world map once everyone has left) and the members stop their usual work. Check plan_caravan first: the game refuses overloaded caravans, missing destinations, unreachable exits and unreachable items, and that reason is returned. Name every member explicitly (\"all\" is refused) and undraft them first; downed members are carried by the others but at least one colonist must be able to walk. Cargo only comes from colony storage or the home area. After departure, steer with list_caravans, send_caravan and set_caravan_movement; cancel_forming_caravan aborts before departure.")]
        public string FormCaravan(
            [Description("Colonist names or ids separated by commas, e.g. \"Ali, Bob\" (\"all\" is refused).")] string colonists,
            [Description("Colony animals to take as pack animals: names or ids, or \"species x count\", e.g. \"muffalo x3, Rex\". Empty for none.")] string animals = "",
            [Description("Cargo as \"name x count\", comma-separated, e.g. \"silver x500, pemmican x80, medicine x6\". Empty for none.")] string cargo = "",
            [Description("Prisoners to take along (names or ids). They must be secured in a cell; usually empty.")] string prisoners = "",
            [Description("Destination tile id (from list_world_objects or get_world_tile); -1 to use worldObjectId. A destination is required.")] int tile = -1,
            [Description("Destination world object id (from list_world_objects); -1 to use tile.")] int worldObjectId = -1)
        {
            Result<Prepared> prepared = Prepare(colonists, animals, cargo, prisoners, tile, worldObjectId);
            if (!prepared.Success) return prepared.Error;
            CaravanRequest request = prepared.Value.Request;
            CaravanPlanInfo plan = _world.PlanCaravan(request);
            if (!string.IsNullOrEmpty(plan.Error)) return ToolText.Error(plan.Error);
            if (plan.Blockers.Count > 0)
            {
                return ToolText.Error("the game would not let this caravan leave: " + string.Join("; ", plan.Blockers.ToArray()) +
                                      ". Adjust the members or cargo and check with plan_caravan.");
            }

            string preview = $"would start forming a caravan with {Members(plan)} carrying {Cargo(plan)} to tile {plan.DestinationTile} " +
                             $"({ToolText.Num(plan.DistanceTiles)} tiles, {WorldTools.Duration(plan.EtaTicks)}); mass {ToolText.Num(plan.MassUsage)}/{ToolText.Num(plan.MassCapacity)} kg, " +
                             $"{FoodText(plan.FoodDays)} of food. The members gather at a meeting point and leave the map together; it takes game time." +
                             (plan.Warnings.Count > 0 ? " Warnings: " + string.Join("; ", plan.Warnings.ToArray()) + "." : string.Empty);
            string blocked = ToolText.Blocked(_gate, preview);
            if (blocked != null) return blocked;

            CommandResult result = _world.FormCaravan(request);
            if (!result.Success) return ToolText.Error(result.Message);
            string text = result.Message + $" (~{ToolText.Num(plan.DistanceTiles)} tiles, {WorldTools.Duration(plan.EtaTicks)} of travel; food for {FoodText(plan.FoodDays)}).";
            if (plan.Warnings.Count > 0) text += " Warnings: " + string.Join("; ", plan.Warnings.ToArray()) + ".";
            return text + " Wait for game time to pass and check list_caravans; cancel_forming_caravan aborts it before departure.";
        }

        [AgentTool("cancel_forming_caravan", ToolCategory.Trade, ToolRisk.Safe)]
        [Description("Aborts a caravan that is still forming on the map (list_caravans shows it under forming caravans), like the Cancel forming caravan button: the members put down the cargo they carry and go back to their usual work. Does nothing to caravans that have already left the map.")]
        public string CancelFormingCaravan(
            [Description("Forming caravan id from list_caravans, or the name or id of one of its members; may be empty when exactly one caravan is forming.")] string caravan = "")
        {
            IReadOnlyList<FormingCaravanInfo> forming = _world.GetFormingCaravans();
            Result<FormingCaravanInfo> picked = ResolveForming(caravan, forming);
            if (!picked.Success) return picked.Error;
            FormingCaravanInfo f = picked.Value;

            string blocked = ToolText.Blocked(_gate, $"would stop forming the caravan [{f.Id}] with {string.Join(", ", f.Members.Select(m => m.Name).ToArray())}; they drop the cargo and resume normal work.");
            if (blocked != null) return blocked;

            CommandResult result = _world.CancelFormingCaravan(f.Id);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message + ".";
        }

        // ---- 解析 ----

        private sealed class Prepared
        {
            public CaravanRequest Request;
        }

        private Result<Prepared> Prepare(string colonists, string animals, string cargo, string prisoners, int tile, int worldObjectId)
        {
            if (TargetResolver.IsEveryoneKeyword(colonists))
                return Fail("name the caravan members explicitly, e.g. \"Ali, Bob\"; \"all\" would send the whole colony away.");
            Result<List<ColonistInfo>> people = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!people.Success) return Fail(people.Error);
            var ids = new List<int>(people.Value.Select(c => c.Id));

            Result<List<AnimalInfo>> beasts = ResolveAnimals(animals, _world.GetAnimals());
            if (!beasts.Success) return Fail(beasts.Error);
            ids.AddRange(beasts.Value.Select(a => a.Id));

            Result<List<PrisonerInfo>> captives = ResolvePrisoners(prisoners, _world.GetPrisoners());
            if (!captives.Success) return Fail(captives.Error);
            ids.AddRange(captives.Value.Select(p => p.Id));

            Result<List<TradeLine>> lines = ResolveCargo(cargo);
            if (!lines.Success) return Fail(lines.Error);

            Result<int> destination = WorldTools.ResolveTile(_world, tile, worldObjectId, _world.GetCaravans(), -1);
            if (!destination.Success) return Fail(destination.Error);

            return Result<Prepared>.Ok(new Prepared
            {
                Request = new CaravanRequest { PawnIds = ids, Cargo = lines.Value, DestinationTile = destination.Value }
            });
        }

        private static Result<Prepared> Fail(string error) => Result<Prepared>.Fail(ToolText.Error(error));

        /// <summary>逗號分隔的殖民地動物：名稱或編號指一隻，「物種 x 數量」取該物種沒倒地的前幾隻。野生動物不收。</summary>
        internal static Result<List<AnimalInfo>> ResolveAnimals(string input, IReadOnlyList<AnimalInfo> all)
        {
            var chosen = new List<AnimalInfo>();
            if (string.IsNullOrWhiteSpace(input)) return Result<List<AnimalInfo>>.Ok(chosen);
            List<AnimalInfo> colony = all.Where(a => !a.Wild).ToList();
            foreach (string raw in input.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                Match m = Regex.Match(part, @"^(.*?)\s*[x×*]\s*(\d+)$", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!m.Success)
                {
                    Result<AnimalInfo> one = AnimalTools.ResolveAnimal(part, colony);
                    if (!one.Success) return Result<List<AnimalInfo>>.Fail(one.Error + " Wild animals cannot join a caravan.");
                    if (!chosen.Contains(one.Value)) chosen.Add(one.Value);
                    continue;
                }

                string species = m.Groups[1].Value.Trim();
                int count = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (species.Length == 0 || count < 1 || count > 1000)
                    return Result<List<AnimalInfo>>.Fail(ToolText.Error($"cannot understand \"{part}\" in animals. Use \"species x count\", e.g. \"muffalo x3\"."));
                List<AnimalInfo> pool = colony.Where(a => !chosen.Contains(a)).ToList();
                List<AnimalInfo> matches = pool.Where(a => Same(a.Species, species) || Same(a.SpeciesDefName, species)).ToList();
                if (matches.Count == 0)
                    matches = pool.Where(a => Has(a.Species, species) || Has(a.SpeciesDefName, species)).ToList();
                if (matches.Count < count)
                {
                    return Result<List<AnimalInfo>>.Fail(ToolText.Error(
                        $"only {matches.Count} colony animal(s) match \"{species}\" (asked for {count}). Colony animals: " +
                        (colony.Count == 0 ? "none" : string.Join(", ", colony.Select(a => $"[{a.Id}] {a.Name}").ToArray())) + "."));
                }
                chosen.AddRange(matches.OrderBy(a => a.Downed).ThenBy(a => a.Id).Take(count));
            }
            return Result<List<AnimalInfo>>.Ok(chosen);
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool Has(string text, string needle) => !string.IsNullOrEmpty(text) && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static Result<List<PrisonerInfo>> ResolvePrisoners(string input, IReadOnlyList<PrisonerInfo> all)
        {
            var chosen = new List<PrisonerInfo>();
            if (string.IsNullOrWhiteSpace(input)) return Result<List<PrisonerInfo>>.Ok(chosen);
            foreach (string part in input.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Result<PrisonerInfo> one = PrisonerTools.ResolvePrisoner(part.Trim(), all);
                if (!one.Success) return Result<List<PrisonerInfo>>.Fail(one.Error);
                if (!chosen.Contains(one.Value)) chosen.Add(one.Value);
            }
            return Result<List<PrisonerInfo>>.Ok(chosen);
        }

        /// <summary>貨物名稱只在殖民地現在能裝車的物品裡找（原版組隊視窗物品頁那份），數量不得超過現有。同種物品的多行併成一行。</summary>
        private Result<List<TradeLine>> ResolveCargo(string cargo)
        {
            Result<List<TradeTools.ParsedLine>> parsed = TradeTools.ParseTradeList(cargo, "cargo");
            if (!parsed.Success) return Result<List<TradeLine>>.Fail(parsed.Error);
            var lines = new List<TradeLine>();
            if (parsed.Value.Count == 0) return Result<List<TradeLine>>.Ok(lines);

            IReadOnlyList<CaravanCargoInfo> available = _world.GetCaravanCargo();
            List<DefInfo> candidates = available.Select(c => new DefInfo(c.DefName, c.Label)).ToList();
            foreach (TradeTools.ParsedLine line in parsed.Value)
            {
                Result<DefInfo> def = DefResolver.Resolve(line.Item, candidates, "carryable item");
                if (!def.Success)
                {
                    return Result<List<TradeLine>>.Fail(def.Error +
                        " Only items in colony storage or the home area that nobody has reserved can be carried; furniture such as bedrolls must be uninstalled first.");
                }
                TradeLine existing = lines.FirstOrDefault(l => string.Equals(l.DefName, def.Value.DefName, StringComparison.OrdinalIgnoreCase));
                if (existing == null) lines.Add(new TradeLine { DefName = def.Value.DefName, Count = line.Count });
                else existing.Count += line.Count;
            }
            foreach (TradeLine line in lines)
            {
                CaravanCargoInfo have = available.First(c => string.Equals(c.DefName, line.DefName, StringComparison.OrdinalIgnoreCase));
                if (line.Count > have.Count)
                    return Result<List<TradeLine>>.Fail(ToolText.Error($"the colony can only supply {have.Count} {have.Label} for a caravan (asked for {line.Count})."));
            }
            return Result<List<TradeLine>>.Ok(lines);
        }

        /// <summary>用集結編號、或其中一名成員的名稱／編號指定一支集結中的商隊；只有一支時可不指名。</summary>
        internal static Result<FormingCaravanInfo> ResolveForming(string input, IReadOnlyList<FormingCaravanInfo> forming)
        {
            if (forming.Count == 0)
                return Result<FormingCaravanInfo>.Fail(ToolText.Error("no caravan is being formed. Caravans that already left the map are on the world map (list_caravans)."));
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return forming.Count == 1
                    ? Result<FormingCaravanInfo>.Ok(forming[0])
                    : Result<FormingCaravanInfo>.Fail(ToolText.Error("several caravans are forming; name one. " + FormingList(forming)));
            }
            List<FormingCaravanInfo> hits;
            if (int.TryParse(needle, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                hits = forming.Where(f => f.Id == id).Concat(forming.Where(f => f.Id != id && f.Members.Any(m => m.Id == id))).ToList();
            else
                hits = forming.Where(f => f.Members.Any(m => m.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (hits.Count == 1) return Result<FormingCaravanInfo>.Ok(hits[0]);
            return Result<FormingCaravanInfo>.Fail(ToolText.Error(hits.Count == 0
                ? $"no forming caravan matches \"{needle}\". " + FormingList(forming)
                : $"\"{needle}\" matches several forming caravans. " + FormingList(forming) + " Use the id."));
        }

        private static string FormingList(IReadOnlyList<FormingCaravanInfo> forming)
        {
            return "Forming: " + string.Join("; ", forming.Select(f => "[" + f.Id + "] " + string.Join(", ", f.Members.Select(m => m.Name).ToArray())).ToArray()) + ".";
        }

        // ---- 格式 ----

        internal static string FormatPlan(CaravanPlanInfo p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Caravan plan (nothing has been started):");
            sb.Append("Members (").Append(p.Members.Count).Append("): ").AppendLine(Members(p));
            sb.Append("Cargo: ").AppendLine(p.Cargo.Count == 0 ? "none" : string.Join(", ", p.Cargo.Select(c => c.Label + " x" + c.Count + " (" + ToolText.Num(c.MassEach * c.Count) + " kg)").ToArray()));
            sb.Append("Mass: ").Append(ToolText.Num(p.MassUsage)).Append(" / ").Append(ToolText.Num(p.MassCapacity)).Append(" kg");
            if (p.MassCapacity > 0f) sb.Append(" (").Append(ToolText.Pct(p.MassUsage / p.MassCapacity)).Append(')');
            sb.AppendLine(p.MassUsage > p.MassCapacity ? " - OVERLOADED" : string.Empty);
            sb.Append("Speed: ").Append(ToolText.Num(p.TilesPerDay)).Append(" tiles per day (").Append(p.TicksPerMove).AppendLine(" ticks per tile)");
            sb.Append("Food: ").Append(FoodText(p.FoodDays)).Append(" of food; ")
              .Append(p.DaysUntilRot >= 600f ? "nothing perishable" : "it rots in " + ToolText.Num(p.DaysUntilRot) + " days")
              .Append("; foraging: ").AppendLine(p.ForagedPerDay > 0f ? ToolText.Num(p.ForagedPerDay) + " " + p.ForagedFoodLabel + " per day" : "none");
            sb.Append("Visibility: ").AppendLine(ToolText.Pct(p.Visibility));
            if (p.DestinationTile < 0) sb.AppendLine("Destination: none chosen (give tile or worldObjectId; the game needs one to send a caravan)");
            else
            {
                sb.Append("Destination: ").Append(string.IsNullOrEmpty(p.DestinationLabel) ? "tile " + p.DestinationTile : p.DestinationLabel);
                sb.AppendLine(p.DestinationReachable
                    ? " | " + ToolText.Num(p.DistanceTiles) + " tiles, " + WorldTools.Duration(p.EtaTicks) + " at this speed"
                    : " | not reachable");
            }
            if (p.Warnings.Count > 0) sb.Append("Warnings: ").AppendLine(string.Join("; ", p.Warnings.ToArray()));
            if (p.Blockers.Count > 0) sb.Append("The game would refuse to send it: ").AppendLine(string.Join("; ", p.Blockers.ToArray()));
            sb.Append("Can be sent now: ").Append(p.Blockers.Count == 0 ? "yes (form_caravan)" : "no");
            return sb.ToString();
        }

        private static string Members(CaravanPlanInfo p)
        {
            return string.Join(", ", p.Members.Select(m =>
                m.Name + (m.Kind == CaravanMemberInfo.ColonistKind ? string.Empty : " (" + m.Kind + ")") + (m.Downed ? " [downed]" : string.Empty)).ToArray());
        }

        private static string Cargo(CaravanPlanInfo p)
        {
            return p.Cargo.Count == 0 ? "no cargo" : string.Join(", ", p.Cargo.Select(c => c.Label + " x" + c.Count).ToArray());
        }

        private static string FoodText(float days)
        {
            return days >= 600f ? "600+ days" : ToolText.Num(days) + " days";
        }
    }
}
