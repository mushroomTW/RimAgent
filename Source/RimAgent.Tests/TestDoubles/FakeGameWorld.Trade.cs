using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimAgent.Game;

namespace RimAgent.Tests;

internal sealed partial class FakeGameWorld
{
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
