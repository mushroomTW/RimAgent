using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>交易：商人庫存查詢、買入與賣出一次結清、組織商隊外出。</summary>
    public sealed class TradeTools
    {
        /// <summary>單一商人最多列出的品項數，超過則截斷並註明（省 token）。</summary>
        internal const int MaxGoodsListed = 80;

        private readonly IGameWorld _world;

        public TradeTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("list_orbital_traders", ToolCategory.Trade)]
        [Description("Lists the orbital traders around the colony and what each one is selling, with its silver reserve. Orbital traders are trade ships in orbit; do not confuse them with visiting caravans on the ground, which this tool never lists.")]
        public string ListOrbitalTraders()
        {
            List<TraderInfo> traders = _world.GetTraders()
                .Where(t => string.Equals(t.Kind, TraderInfo.OrbitalKind, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (traders.Count == 0)
            {
                return "There are no orbital traders around the colony right now. " +
                       "Do not invent one; tell the player there is nothing in orbit to trade with.";
            }
            return FormatTraders(traders, "orbital trader");
        }

        [AgentTool("list_visitor_caravans", ToolCategory.Trade)]
        [Description("Lists the visiting caravans at the colony and what each one is selling, with its silver reserve. Visiting caravans are traders on the ground; do not confuse them with orbital traders in orbit, which this tool never lists.")]
        public string ListVisitorCaravans()
        {
            List<TraderInfo> traders = _world.GetTraders()
                .Where(t => string.Equals(t.Kind, TraderInfo.VisitorKind, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (traders.Count == 0)
            {
                return "There are no visiting caravans at the colony right now. " +
                       "Do not invent one, and do not substitute an orbital trader; tell the player nobody is visiting to trade with.";
            }
            return FormatTraders(traders, "visiting caravan");
        }

        [AgentTool("execute_trade", ToolCategory.Trade)]
        [Description("Buys from and sells to one trader in a single deal: items and silver change hands at once with the game's trade prices. Ask for the trader's id from list_orbital_traders or list_visitor_caravans first so names and stock are exact. Only items are listed; silver moves automatically as payment.")]
        public string ExecuteTrade(
            [Description("Trader id in square brackets from list_orbital_traders or list_visitor_caravans.")] string trader,
            [Description("What to buy, e.g. \"ultratech medicine x12, spacer component x4\". Empty string buys nothing.")] string buys,
            [Description("What to sell, e.g. \"steel x100, corn x50\". Empty string sells nothing.")] string sells,
            [Description("Negotiator name, nickname or id. Empty string uses the colonist with the best social skill.")] string negotiator)
        {
            ResolveResult<List<ParsedLine>> buyLines = ParseTradeList(buys, "buys");
            if (!buyLines.Success) return buyLines.Error;
            ResolveResult<List<ParsedLine>> sellLines = ParseTradeList(sells, "sells");
            if (!sellLines.Success) return sellLines.Error;
            if (buyLines.Value.Count == 0 && sellLines.Value.Count == 0)
            {
                return ToolText.Error("nothing to buy or sell. Give at least one item, e.g. buys \"steel x50\".");
            }

            TraderInfo deal = _world.GetTraders()
                .FirstOrDefault(t => string.Equals(t.Id, (trader ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
            if (deal == null)
            {
                return ToolText.Error($"unknown trader \"{trader}\". Use list_orbital_traders or list_visitor_caravans to get a valid id.");
            }

            ColonistInfo pawn;
            if (string.IsNullOrWhiteSpace(negotiator))
            {
                // 交涉者預設：社交技能最高且未倒地者。
                pawn = _world.GetColonists().Where(c => !c.Downed).OrderByDescending(SocialLevel).FirstOrDefault();
                if (pawn == null) return ToolText.Error("no colonist is available to negotiate.");
            }
            else
            {
                ResolveResult<ColonistInfo> resolved = TargetResolver.ResolveColonist(negotiator, _world.GetColonists());
                if (!resolved.Success) return resolved.Error;
                if (resolved.Value.Downed) return ToolText.Error($"{resolved.Value.Name} is downed and cannot negotiate.");
                pawn = resolved.Value;
            }

            ResolveResult<List<TradeLine>> resolvedBuys = ResolveLines(buyLines.Value);
            if (!resolvedBuys.Success) return resolvedBuys.Error;
            ResolveResult<List<TradeLine>> resolvedSells = ResolveLines(sellLines.Value);
            if (!resolvedSells.Success) return resolvedSells.Error;
            var request = new TradeRequest
            {
                TraderId = deal.Id,
                NegotiatorPawnId = pawn.Id,
                Buys = resolvedBuys.Value,
                Sells = resolvedSells.Value
            };

            CommandResult result = _world.ExecuteTrade(request);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        [AgentTool("form_caravan", ToolCategory.Trade)]
        [Description("Forms the named colonists into a caravan that leaves the map toward a compass direction. Members must be named explicitly (\"all\" is refused); drafted members must be undrafted first. Pack animals are not included yet.")]
        public string FormCaravan(
            [Description("Colonist names or ids separated by commas, e.g. \"Ali, Bob\".")] string colonists,
            [Description("Compass heading: N, NE, E, SE, S, SW, W or NW.")] string heading)
        {
            if (IsEveryoneKeyword(colonists))
            {
                return ToolText.Error("name the caravan members explicitly, e.g. \"Ali, Bob\"; \"all\" would send the whole colony away.");
            }
            ResolveResult<List<ColonistInfo>> pawns = TargetResolver.ResolveColonists(colonists, _world.GetColonists());
            if (!pawns.Success) return pawns.Error;

            var request = new CaravanRequest
            {
                PawnIds = pawns.Value.Select(c => c.Id).ToList(),
                Heading = (heading ?? string.Empty).Trim()
            };
            CommandResult result = _world.FormCaravan(request);
            if (!result.Success) return ToolText.Error(result.Message);
            return result.Message;
        }

        private static bool IsEveryoneKeyword(string input)
        {
            string needle = (input ?? string.Empty).Trim();
            return string.Equals(needle, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "everyone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(needle, "*", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>品名解析到 defName；銀兩不用列（自動結算），列了直接擋下並說明。</summary>
        private ResolveResult<List<TradeLine>> ResolveLines(List<ParsedLine> parsed)
        {
            var lines = new List<TradeLine>();
            foreach (ParsedLine p in parsed)
            {
                ResolveResult<DefInfo> def = DefResolver.Resolve(p.Item, _world.TradeGoods, "trade goods");
                if (!def.Success) return ResolveResult<List<TradeLine>>.Fail(def.Error);
                if (string.Equals(def.Value.DefName, "Silver", StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveResult<List<TradeLine>>.Fail(ToolText.Error("silver moves automatically as payment; do not list it in buys or sells."));
                }
                lines.Add(new TradeLine { DefName = def.Value.DefName, Count = p.Count });
            }
            return ResolveResult<List<TradeLine>>.Ok(lines);
        }

        /// <summary>「品名 x 數量」一行；逗號、分號或換行分隔多行。</summary>
        internal sealed class ParsedLine
        {
            public string Item = string.Empty;
            public int Count;
        }

        internal static ResolveResult<List<ParsedLine>> ParseTradeList(string text, string what)
        {
            var lines = new List<ParsedLine>();
            if (string.IsNullOrWhiteSpace(text)) return ResolveResult<List<ParsedLine>>.Ok(lines);
            foreach (string raw in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match m = Regex.Match(raw.Trim(), @"^(.*?)\s*[x×*]\s*(\d+)\s*$");
                if (!m.Success)
                {
                    return ResolveResult<List<ParsedLine>>.Fail(ToolText.Error(
                        $"cannot understand \"{raw.Trim()}\" in {what}. Use \"name x count\", e.g. \"steel x100\"."));
                }
                int count;
                try
                {
                    count = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    return ResolveResult<List<ParsedLine>>.Fail(ToolText.Error($"count \"{m.Groups[2].Value}\" is not a number."));
                }
                string item = m.Groups[1].Value.Trim();
                if (item.Length == 0 || count < 1 || count > 100000)
                {
                    return ResolveResult<List<ParsedLine>>.Fail(ToolText.Error(
                        $"cannot understand \"{raw.Trim()}\" in {what}. Use \"name x count\" with count 1-100000."));
                }
                lines.Add(new ParsedLine { Item = item, Count = count });
            }
            return ResolveResult<List<ParsedLine>>.Ok(lines);
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
