using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimAgent.Game;

internal sealed partial class RimWorldGameWorld
{
    public IReadOnlyList<TraderInfo> GetTraders()
    {
        Map map = Map;
        var result = new List<TraderInfo>();
        foreach (TradeShip ship in map.passingShipManager.passingShips.OfType<TradeShip>())
        {
            if (ship.CanTradeNow)
            {
                result.Add(CreateOrbitalTraderInfo(ship));
            }
        }
        // 來訪商隊（Visitor）：同一 Lord 共用整隊庫存，只列一次；不拿軌道商人頂替。
        // CanTradeNow 已排除死亡、倒地、俘虜、玩家派系與敵對派系，只剩地圖歸屬要自己檢查。
        foreach (IGrouping<object, Pawn> group in map.mapPawns.AllPawnsSpawned
                     .Where(p => p.Map == map && p.trader != null && p.trader.traderKind != null && p.trader.CanTradeNow)
                     .GroupBy(p => (object)p.GetLord() ?? p))
        {
            result.Add(CreateVisitorTraderInfo(group.First()));
        }
        return result;
    }

    private static List<TradeGoodsInfo> ExtractGoods(IEnumerable<Thing> rawGoods, out int silver)
    {
        var goods = new List<TradeGoodsInfo>();
        silver = 0;
        foreach (Thing t in rawGoods)
        {
            if (t == null || t.Destroyed) continue;
            if (t.def == ThingDefOf.Silver)
            {
                silver += t.stackCount;
                continue;
            }
            goods.Add(new TradeGoodsInfo
            {
                DefName = t.def.defName,
                Label = t is Pawn goodsPawn ? goodsPawn.LabelShort : t.def.label,
                Count = Math.Max(1, t.stackCount),
                MarketValue = t.MarketValue
            });
        }
        return [.. goods
            .OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.DefName, StringComparer.Ordinal)];
    }

    private static TraderInfo CreateOrbitalTraderInfo(TradeShip ship)
    {
        var goods = ExtractGoods(ship.Goods, out _);
        return new TraderInfo
        {
            Kind = TraderInfo.OrbitalKind,
            Id = ship.GetUniqueLoadID(),
            Name = ship.FullTitle,
            Faction = ship.Faction?.Name ?? string.Empty,
            Silver = ship.Silver,
            Goods = goods
        };
    }

    private static TraderInfo CreateVisitorTraderInfo(Pawn leader)
    {
        var goods = ExtractGoods(leader.trader.Goods, out int silver);
        return new TraderInfo
        {
            Kind = TraderInfo.VisitorKind,
            Id = leader.GetUniqueLoadID(),
            Name = leader.LabelShort + " (" + leader.trader.traderKind.label + ")",
            Faction = leader.Faction?.Name ?? string.Empty,
            Silver = silver,
            Goods = goods
        };
    }

    public CommandResult ExecuteTrade(TradeRequest? request)
    {
        CommandResult? linesResult = ValidateTradeLines(request, out IReadOnlyList<TradeLine> buys, out IReadOnlyList<TradeLine> sells);
        if (linesResult != null || request == null) return linesResult ?? CommandResult.Fail("no trade requested");

        // 玩家手動開著交易窗時不搶 TradeSession，否則對方的結算會被掀掉。
        if (TradeSession.Active) return CommandResult.Fail("a trade window is already open. Close it first.");

        ITrader? trader = FindTrader(request.TraderId);
        CommandResult? traderResult = ValidateTrader(trader, request.TraderId);
        if (traderResult != null || trader == null) return traderResult ?? CommandResult.Fail("trader missing");

        Pawn? negotiator = FindColonist(request.NegotiatorPawnId);
        CommandResult? negResult = ValidateNegotiator(negotiator, trader);
        if (negResult != null || negotiator == null) return negResult ?? CommandResult.Fail("negotiator missing");

        // 之後全走遊戲的原生結算：價格、銀兩與物品交割都由 TradeDeal 處理，
        // 這裡只做「先驗證全部、再一次交割」，餘額或庫存不足整筆不做。
        TradeSession.SetupWith(trader, negotiator, giftMode: false);
        try
        {
            TradeDeal deal = TradeSession.deal;
            CommandResult? allocResult = ApplyAllocations(deal, trader, buys, sells);
            if (allocResult != null) return allocResult;

            CommandResult? affordResult = ValidateAffordability(deal, trader, out int colonySilver, out int postDealSilver);
            if (affordResult != null) return affordResult;

            // 交割前先把收據算好：銀兩實際變動取自貨幣行的公式值，品項數量即交割數量。
            string receipt = $"Traded with {TraderLabel(trader)} (negotiated by {negotiator.LabelShort}): " +
                string.Join("; ", ReceiptParts(buys, sells)) +
                string.Create(CultureInfo.InvariantCulture, $". Colony silver {colonySilver} -> {postDealSilver}.");
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

    private static CommandResult? ApplyAllocations(TradeDeal deal, ITrader trader, IReadOnlyList<TradeLine> buys, IReadOnlyList<TradeLine> sells)
    {
        var buyAllocations = new Dictionary<Tradeable, int>();
        var sellAllocations = new Dictionary<Tradeable, int>();
        foreach (TradeLine line in buys)
        {
            string? fail = Allocate(buyAllocations, deal, line, buy: true, TraderLabel(trader));
            if (fail != null) return CommandResult.Fail(fail);
        }
        foreach (TradeLine line in sells)
        {
            string? fail = Allocate(sellAllocations, deal, line, buy: false, TraderLabel(trader));
            if (fail != null) return CommandResult.Fail(fail);
        }
        foreach (Tradeable row in buyAllocations.Keys.Concat(sellAllocations.Keys).Distinct())
        {
            int target = row.CountToTransfer + Allocated(buyAllocations, row) - Allocated(sellAllocations, row);
            // AdjustTo 為 void，且會把數量夾在可交易範圍內；事後比對實際數量，與預期不符就整筆不做（此時尚未交割）。
            row.AdjustTo(target);
            if (row.CountToTransfer != target)
                return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"could only set {row.LabelCap} to {row.CountToTransfer} instead of {target}; the trade was not executed."));
        }
        return null;
    }

    private static CommandResult? ValidateAffordability(TradeDeal deal, ITrader trader, out int colonySilver, out int postDealSilver)
    {
        deal.UpdateCurrencyCount();
        Tradeable? currency = deal.CurrencyTradeable;
        colonySilver = currency != null ? currency.CountHeldBy(Transactor.Colony) : 0;
        postDealSilver = currency != null ? currency.CountPostDealFor(Transactor.Colony) : 0;
        if (currency == null || postDealSilver < 0)
        {
            int need = colonySilver - postDealSilver;
            return CommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"the colony cannot afford this: needs {need} silver but has {colonySilver}."));
        }
        if (!deal.DoesTraderHaveEnoughSilver())
        {
            return CommandResult.Fail($"{TraderLabel(trader)} cannot afford this: needs more silver to pay for the colony's goods.");
        }
        return null;
    }

    private static string? Allocate(Dictionary<Tradeable, int> allocations, TradeDeal deal, TradeLine line, bool buy, string traderLabel)
    {
        List<Tradeable> rows = [.. deal.AllTradeables.Where(r => !r.IsCurrency && r.ThingDef != null && string.Equals(r.ThingDef.defName, line.DefName, StringComparison.OrdinalIgnoreCase))];
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
                ? string.Create(CultureInfo.InvariantCulture, $"{traderLabel} only has {available} {ItemLabel(line.DefName)} for sale, not {line.Count}.")
                : string.Create(CultureInfo.InvariantCulture, $"the colony only has {available} sellable {ItemLabel(line.DefName)}, not {line.Count}.");
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
        if (trader is TradeShip ship) return ship.FullTitle;
        if (trader is Pawn pawn) return pawn.LabelShort;
        return trader.TraderName;
    }

    private static CommandResult? ValidateTradeLines(TradeRequest? request, out IReadOnlyList<TradeLine> buys, out IReadOnlyList<TradeLine> sells)
    {
        buys = request?.Buys ?? [];
        sells = request?.Sells ?? [];
        if (request == null) return CommandResult.Fail("no trade requested");
        if (string.IsNullOrEmpty(request.TraderId)) return CommandResult.Fail("no trader specified");
        if (buys.Count == 0 && sells.Count == 0) return CommandResult.Fail("nothing to buy or sell");
        foreach (TradeLine line in buys.Concat(sells))
        {
            if (string.Equals(line.DefName, ThingDefOf.Silver.defName, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("silver moves automatically as payment; do not list it");
            if (line.Count <= 0) return CommandResult.Fail($"invalid count {line.Count} for {line.DefName}");
        }
        return null;
    }

    private static CommandResult? ValidateTrader(ITrader? trader, string traderId)
    {
        if (trader == null) return CommandResult.Fail($"trader \"{traderId}\" was not found. List the traders again to get a valid id.");
        if (!trader.CanTradeNow) return CommandResult.Fail($"{TraderLabel(trader)} cannot trade right now.");
        if (trader.TradeCurrency != TradeCurrency.Silver) return CommandResult.Fail($"{TraderLabel(trader)} does not trade in silver.");
        return null;
    }

    private static CommandResult? ValidateNegotiator(Pawn? negotiator, ITrader trader)
    {
        if (negotiator == null) return CommandResult.Fail("negotiator was not found among the colonists.");
        if (negotiator.Downed) return CommandResult.Fail($"{negotiator.LabelShort} is downed and cannot negotiate.");
        // 沿用原版交易的門檻：要能說話、社交沒被禁用；軌道商人還要一台有電的通訊台。
        if (negotiator.health?.capacities?.CapableOf(PawnCapacityDefOf.Talking) == false)
            return CommandResult.Fail($"{negotiator.LabelShort} is incapable of talking and cannot negotiate.");
        if (negotiator.skills?.GetSkill(SkillDefOf.Social)?.TotallyDisabled == true)
            return CommandResult.Fail($"{negotiator.LabelShort} is incapable of social work and cannot negotiate.");
        if (trader is TradeShip && !Map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>().Any(c => c.CanUseCommsNow))
            return CommandResult.Fail("trading with an orbital trader needs a powered comms console.");
        return null;
    }

    private static ITrader? FindTrader(string id)
    {
        Map map = Map;
        TradeShip? ship = map.passingShipManager.passingShips.OfType<TradeShip>()
            .FirstOrDefault(s => s.GetUniqueLoadID() == id);
        if (ship != null) return ship;

        return map.mapPawns.AllPawnsSpawned
            .FirstOrDefault(p => p.trader != null && p.trader.traderKind != null && p.GetUniqueLoadID() == id);
    }
}
