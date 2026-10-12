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
    /// <summary>交易：商人庫存查詢、買入與賣出一次結清。組商隊在 <see cref="CaravanTools"/>。</summary>
    public sealed class TradeTools
    {
        /// <summary>單一商人最多列出的品項數，超過則截斷並註明（省 token）。</summary>
        internal const int MaxGoodsListed = 80;

        private const string SilverDefName = "Silver";

        private readonly IGameWorld _world;
        private readonly ConfirmationGate _gate;

        public TradeTools(IGameWorld world, ConfirmationGate gate = null)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _gate = gate;
        }

        [AgentTool("list_traders", ToolCategory.Trade, ToolRisk.Query)]
        [Description("Lists every trader the colony can deal with right now: trade ships in orbit (need a powered comms console) and trader caravans visiting on the ground, each with id, goods for sale and silver reserve.")]
        public string ListTraders()
        {
            List<TraderInfo> traders = _world.GetTraders().ToList();
            List<TraderInfo> orbital = traders.Where(t => string.Equals(t.Kind, TraderInfo.OrbitalKind, StringComparison.OrdinalIgnoreCase)).ToList();
            List<TraderInfo> visitors = traders.Where(t => string.Equals(t.Kind, TraderInfo.VisitorKind, StringComparison.OrdinalIgnoreCase)).ToList();
            if (orbital.Count == 0 && visitors.Count == 0)
            {
                return "There are no traders around the colony right now: no orbital traders and no visiting caravans. " +
                       "Do not invent one; tell the player there is nobody to trade with.";
            }
            return (orbital.Count > 0 ? FormatTraders(orbital, "orbital trader") : "No orbital traders.") + "\n" +
                   (visitors.Count > 0 ? FormatTraders(visitors, "visiting caravan") : "No visiting caravans.");
        }

        [AgentTool("execute_trade", ToolCategory.Trade, ToolRisk.Confirm)]
        [Description("Buys from and sells to one trader in a single deal at the game's prices; goods and silver change hands at once. Take the trader id and exact item names from list_traders. Silver settles the deal automatically, so list goods only.")]
        public string ExecuteTrade(
            [Description("Trader id in square brackets from list_traders.")] string trader,
            [Description("Goods to buy as \"name x count\", comma-separated, e.g. \"medicine x12, component x4\"; empty buys nothing.")] string buys,
            [Description("Goods to sell as \"name x count\", comma-separated, e.g. \"steel x100, corn x50\"; empty sells nothing.")] string sells,
            [Description("Negotiator name, nickname or id; empty picks the colonist with the highest Social skill.")] string negotiator)
        {
            Result<List<ParsedLine>> buyLines = ParseTradeList(buys, "buys");
            if (!buyLines.Success) return buyLines.Error;
            Result<List<ParsedLine>> sellLines = ParseTradeList(sells, "sells");
            if (!sellLines.Success) return sellLines.Error;
            if (buyLines.Value.Count == 0 && sellLines.Value.Count == 0)
            {
                return ToolText.Error("nothing to buy or sell. Give at least one item, e.g. buys \"steel x50\".");
            }

            TraderInfo deal = _world.GetTraders()
                .FirstOrDefault(t => string.Equals(t.Id, (trader ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (deal == null)
            {
                return ToolText.Error($"unknown trader \"{trader}\". Use list_traders to get a valid id.");
            }

            Result<ColonistInfo> negotiatorResult = ResolveNegotiator(negotiator);
            if (!negotiatorResult.Success) return negotiatorResult.Error;
            ColonistInfo pawn = negotiatorResult.Value;

            // 買入只在這位商人的貨品裡找：模組包的全遊戲物品目錄太大，常撞名或對到商人沒賣的東西。
            // 銀兩留在候選裡，列了才會得到「銀兩自動結算」的說明。
            var offered = new HashSet<string>(deal.Goods.Select(g => g.DefName), StringComparer.OrdinalIgnoreCase) { SilverDefName };
            Result<List<TradeLine>> resolvedBuys = ResolveLines(buyLines.Value, _world.TradeGoods.Where(d => offered.Contains(d.DefName)).ToList(),
                $" Only the goods {deal.Name} [{deal.Id}] sells were searched.");
            if (!resolvedBuys.Success) return resolvedBuys.Error;
            Result<List<TradeLine>> resolvedSells = ResolveLines(sellLines.Value, _world.TradeGoods, string.Empty);
            if (!resolvedSells.Success) return resolvedSells.Error;
            var request = new TradeRequest
            {
                TraderId = deal.Id,
                NegotiatorPawnId = pawn.Id,
                Buys = resolvedBuys.Value,
                Sells = resolvedSells.Value
            };

            var parts = new List<string>();
            if (request.Buys.Count > 0) parts.Add("buy " + string.Join(", ", request.Buys.Select(l => $"{l.DefName} x{l.Count}")));
            if (request.Sells.Count > 0) parts.Add("sell " + string.Join(", ", request.Sells.Select(l => $"{l.DefName} x{l.Count}")));
            string blocked = ToolText.Blocked(_gate, $"would trade with {deal.Name} [{deal.Id}] (negotiator {pawn.Name}): {string.Join("; ", parts)}.");
            if (blocked != null) return blocked;

            CommandResult result = _world.ExecuteTrade(request);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        /// <summary>品名解析到 defName；銀兩不用列（自動結算），列了直接擋下並說明。</summary>
        private static Result<List<TradeLine>> ResolveLines(List<ParsedLine> parsed, IReadOnlyList<DefInfo> candidates, string missHint)
        {
            var lines = new List<TradeLine>();
            foreach (ParsedLine p in parsed)
            {
                Result<DefInfo> def = DefResolver.Resolve(p.Item, candidates, "trade goods");
                if (!def.Success) return Result<List<TradeLine>>.Fail(def.Error + missHint);
                if (string.Equals(def.Value.DefName, SilverDefName, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<List<TradeLine>>.Fail(ToolText.Error("silver moves automatically as payment; do not list it in buys or sells."));
                }
                lines.Add(new TradeLine { DefName = def.Value.DefName, Count = p.Count });
            }
            return Result<List<TradeLine>>.Ok(lines);
        }

        /// <summary>「品名 x 數量」一行；逗號、分號或換行分隔多行。</summary>
        internal sealed class ParsedLine
        {
            public string Item = string.Empty;
            public int Count;
        }

        internal static Result<List<ParsedLine>> ParseTradeList(string text, string what)
        {
            var lines = new List<ParsedLine>();
            if (string.IsNullOrWhiteSpace(text)) return Result<List<ParsedLine>>.Ok(lines);
            foreach (string raw in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match m = Regex.Match(raw.Trim(), @"^(.*?)\s*[x×*]\s*(\d+)\s*$", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!m.Success)
                {
                    return Result<List<ParsedLine>>.Fail(ToolText.Error(
                        $"cannot understand \"{raw.Trim()}\" in {what}. Use \"name x count\", e.g. \"steel x100\"."));
                }
                int count;
                try
                {
                    count = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    return Result<List<ParsedLine>>.Fail(ToolText.Error($"count \"{m.Groups[2].Value}\" is not a number."));
                }
                string item = m.Groups[1].Value.Trim();
                if (item.Length == 0 || count < 1 || count > 100000)
                {
                    return Result<List<ParsedLine>>.Fail(ToolText.Error(
                        $"cannot understand \"{raw.Trim()}\" in {what}. Use \"name x count\" with count 1-100000."));
                }
                lines.Add(new ParsedLine { Item = item, Count = count });
            }
            return Result<List<ParsedLine>>.Ok(lines);
        }

        private Result<ColonistInfo> ResolveNegotiator(string negotiator)
        {
            if (string.IsNullOrWhiteSpace(negotiator))
            {
                // 交涉者預設：社交技能最高且未倒地者。
                ColonistInfo defaultPawn = _world.GetColonists().Where(c => !c.Downed).OrderByDescending(SocialLevel).FirstOrDefault();
                if (defaultPawn == null) return Result<ColonistInfo>.Fail(ToolText.Error("no colonist is available to negotiate."));
                return Result<ColonistInfo>.Ok(defaultPawn);
            }
            Result<ColonistInfo> resolved = TargetResolver.ResolveColonist(negotiator, _world.GetColonists());
            if (!resolved.Success) return resolved;
            if (resolved.Value.Downed) return Result<ColonistInfo>.Fail(ToolText.Error($"{resolved.Value.Name} is downed and cannot negotiate."));
            return resolved;
        }

        private static int SocialLevel(ColonistInfo c)
        {
            SkillInfo social = c.Skills.FirstOrDefault(s => string.Equals(s.DefName, "Social", StringComparison.OrdinalIgnoreCase));
            return social != null ? social.Level : 0;
        }

        /// <param name="kindLabel">單數稱謂，必含完整詞彙（orbital trader／visiting caravan），不單用 caravan。</param>
        private static string FormatTraders(List<TraderInfo> traders, string kindLabel)
        {
            var sb = new StringBuilder();
            sb.Append(traders.Count).Append(traders.Count == 1 ? " " + kindLabel + ":" : " " + kindLabel + "s:").AppendLine();
            foreach (TraderInfo t in traders)
            {
                sb.Append("- [").Append(t.Id).Append("] ").Append(t.Name);
                if (!string.IsNullOrEmpty(t.Faction)) sb.Append(" (").Append(t.Faction).Append(')');
                sb.Append(" | silver reserve: ").Append(ToolText.Num(t.Silver)).AppendLine();
                if (t.Goods.Count == 0)
                {
                    sb.Append("  carries no goods.").AppendLine();
                }
                else
                {
                    foreach (TradeGoodsInfo g in t.Goods.Take(MaxGoodsListed))
                    {
                        sb.Append("  - ").Append(g.Label).Append(" (").Append(g.DefName).Append(") x").Append(g.Count).AppendLine();
                    }
                    if (t.Goods.Count > MaxGoodsListed)
                    {
                        sb.Append("  ... and ").Append(t.Goods.Count - MaxGoodsListed).Append(" more.").AppendLine();
                    }
                }
            }
            return sb.ToString().Trim();
        }
    }
}
