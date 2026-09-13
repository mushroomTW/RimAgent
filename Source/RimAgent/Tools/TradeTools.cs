using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>交易：查詢商人庫存。買入賣出在後續 issue 補上。</summary>
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

        /// <param name="kindLabel">單數稱謂，必含完整詞彙（orbital trader／visiting caravan），不單用 caravan。</param>
        private static string FormatTraders(List<TraderInfo> traders, string kindLabel)
        {
            var sb = new StringBuilder();
            sb.Append(traders.Count).Append(traders.Count == 1 ? " " + kindLabel + ":" : " " + kindLabel + "s:").AppendLine();
            foreach (TraderInfo t in traders)
            {
                sb.Append("- ").Append(t.Name);
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
