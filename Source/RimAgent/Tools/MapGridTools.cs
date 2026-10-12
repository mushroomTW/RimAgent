using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>
    /// 字元網格地圖：模型看不到截圖，inspect_area 只有計數，規劃房間、殺戮區與船體鄰接都需要空間圖。
    /// 世界層只分類每格各圖層的狀態（<see cref="MapGridCell"/>），圖層優先序、字元、座標軸與圖例都在這裡，
    /// 不碰遊戲也能單元測試。
    /// </summary>
    public sealed class MapGridTools
    {
        /// <summary>
        /// 一次最多畫的格數（50x50），與 inspect_area 同一額度：
        /// 滿額約 3,000 字元（2,500 格 + 每列的座標標籤），低於工具結果的 8,000 字元硬上限，不會被截掉中段。
        /// </summary>
        internal const int MaxCells = ObserveTools.MaxInspectedCells;

        /// <summary>X 軸每隔幾格標一次座標。</summary>
        private const int AxisStep = 5;

        internal const string MapLayer = "map";
        internal const string RoofLayer = "roof";
        internal const string BuildLayer = "build";

        /// <summary>三個圖層共用的矩形上限錯誤：角落含頭尾，51 格寬就超過了，要明講並給可直接照抄的例子。</summary>
        internal const string RectLimitHint = "at most 50 cells per side; corners are inclusive, so (0,0)-(49,49) is the largest square";

        private sealed class Glyph
        {
            public Glyph(char symbol, string meaning)
            {
                Symbol = symbol;
                Meaning = meaning;
            }

            public char Symbol { get; }
            public string Meaning { get; }
        }

        private static readonly Glyph Fog = new Glyph('?', "unexplored (fogged)");
        private static readonly Glyph Hostile = new Glyph('!', "hostile pawn");
        private static readonly Glyph Colonist = new Glyph('@', "colonist");
        private static readonly Glyph OtherPawn = new Glyph('o', "other pawn (animal, guest, prisoner, neutral)");
        private static readonly Glyph Wall = new Glyph('#', "wall");
        private static readonly Glyph Door = new Glyph('+', "door");
        private static readonly Glyph Turret = new Glyph('t', "turret");
        private static readonly Glyph Cover = new Glyph('c', "cover you can walk through (sandbags, barricade)");
        private static readonly Glyph OtherBuilding = new Glyph('B', "other building");
        private static readonly Glyph Frame = new Glyph('f', "frame (under construction)");
        private static readonly Glyph Blueprint = new Glyph('b', "blueprint (planned)");
        private static readonly Glyph Item = new Glyph('i', "item");
        private static readonly Glyph Rock = new Glyph('^', "natural rock");
        private static readonly Glyph DeepWater = new Glyph('W', "deep water (impassable)");
        private static readonly Glyph ShallowWater = new Glyph('~', "shallow water");
        private static readonly Glyph Tree = new Glyph('T', "tree");
        private static readonly Glyph OtherPlant = new Glyph('*', "other plant (crop, bush, flower)");
        private static readonly Glyph Stockpile = new Glyph('s', "stockpile zone");
        private static readonly Glyph Growing = new Glyph('g', "growing zone");
        private static readonly Glyph Floor = new Glyph('_', "constructed floor");
        private static readonly Glyph Ground = new Glyph('.', "bare ground");

        private static readonly Glyph MountainRoof = new Glyph('M', "thick rock roof (overhead mountain, cannot be removed)");
        private static readonly Glyph NaturalRoof = new Glyph('n', "thin natural rock roof");
        private static readonly Glyph BuiltRoof = new Glyph('r', "constructed roof");
        private static readonly Glyph NoRoof = new Glyph('.', "no roof");

        /// <summary>圖例的固定順序（只列出現的字元）；圖層優先序見 <see cref="Classify"/>。</summary>
        private static readonly Glyph Blocked = new Glyph('#', "taken: building, blueprint, frame or natural rock (cannot build over it)");
        private static readonly Glyph HeavySupport = new Glyph('H', "heavy-capable ground (ship parts, reactor, most large buildings fit)");
        private static readonly Glyph MediumSupport = new Glyph('m', "medium-capable ground only");
        private static readonly Glyph LightSupport = new Glyph('l', "light-capable ground only");
        private static readonly Glyph NoSupport = new Glyph('~', "no building support (deep water, etc.)");

        private static readonly Glyph[] MapOrder =
        {
            Fog, Hostile, Colonist, OtherPawn, Wall, Door, Turret, Cover, OtherBuilding, Frame, Blueprint, Item, Rock,
            DeepWater, ShallowWater, Tree, OtherPlant, Stockpile, Growing, Floor, Ground
        };

        private static readonly Glyph[] RoofOrder = { Fog, Wall, Door, MountainRoof, NaturalRoof, BuiltRoof, NoRoof };

        private static readonly Glyph[] BuildOrder = { Fog, Blocked, HeavySupport, MediumSupport, LightSupport, NoSupport };

        private readonly IGameWorld _world;

        public MapGridTools(IGameWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        [AgentTool("view_map_grid", ToolCategory.Observe, ToolRisk.Query)]
        [Description("Draws a rectangle of the current map as a character grid, one character per cell, so you can see rooms, walls, doors, kill boxes, hull shapes, water and zones. Rows run north (top, highest z) to south and columns west to east; x labels sit above the columns and a z label starts each row, so you can read exact cells for place_blueprint, add_zone or order_move. A legend lists only the characters that appear. Default layer \"map\" draws, by priority: unexplored fog, pawns (hostile, colonist, other), buildings (wall, door, turret, walk-through cover such as sandbags, other, then frame and blueprint of unfinished ones), items, natural rock, water, trees and other plants, stockpile and growing zones, constructed floor, bare ground. Not drawn: grass and other ground cover, filth, power conduits, floor blueprints. Layer \"roof\" instead shows roofs (thick rock, thin rock, constructed, none) with walls and doors for orientation, to find unroofed gaps in a room. Layer \"build\" shows where structures can go: # taken by a building, blueprint, frame or natural rock (floors, conduits, plants and items do not count; a multi-cell building marks every cell it covers), H heavy-capable ground, m medium only, l light only, ~ no support (deep water); fog stays ?. Ship parts and most large buildings (reactor, engines, etc.) need H on every cell they cover. Up to 50 cells per side (2500 cells); corners are inclusive, so (0,0)-(49,49) is the largest square. A rectangle partly outside the map is clamped. For counts, ids and room details use inspect_area and find_things.")]
        public string ViewMapGrid(
            [Description("Rectangle corner x.")] int x1,
            [Description("Rectangle corner z.")] int z1,
            [Description("Opposite corner x.")] int x2,
            [Description("Opposite corner z.")] int z2,
            [Description("\"map\" (default), \"roof\", or \"build\" (where heavy structures such as ship parts can be placed).")] string layer = null)
        {
            string wanted = (layer ?? string.Empty).Trim();
            string kind;
            if (wanted.Length == 0 || string.Equals(wanted, MapLayer, StringComparison.OrdinalIgnoreCase)) kind = MapLayer;
            else if (string.Equals(wanted, RoofLayer, StringComparison.OrdinalIgnoreCase)) kind = RoofLayer;
            else if (string.Equals(wanted, BuildLayer, StringComparison.OrdinalIgnoreCase)) kind = BuildLayer;
            else return ToolText.Error($"unknown layer \"{wanted}\". Use \"map\" (default), \"roof\" or \"build\".");

            CellRectSpec requested = CellRectSpec.FromCorners(x1, z1, x2, z2);
            Result<CellRectSpec> clamped = TargetResolver.ClampRect(x1, z1, x2, z2, _world.GetMapBounds());
            if (!clamped.Success) return clamped.Error;
            CellRectSpec rect = clamped.Value;
            if (rect.CellCount > MaxCells)
            {
                return ToolText.Error($"rectangle {rect} has {rect.CellCount} cells; the limit is {MaxCells} cells: {RectLimitHint}. Split it into smaller rectangles.");
            }

            IReadOnlyList<MapGridCell> cells = _world.GetMapGrid(rect);
            int width = rect.MaxX - rect.MinX + 1;
            int height = rect.MaxZ - rect.MinZ + 1;

            var sb = new StringBuilder();
            sb.Append(kind == RoofLayer ? "Roof grid " : kind == BuildLayer ? "Build grid " : "Map grid ").Append(rect).Append(": ").Append(width).Append('x').Append(height)
              .Append(" cells. Rows run north (top, highest z) to south, columns west to east.").AppendLine();
            if (!SameRect(requested, rect))
            {
                MapBounds bounds = _world.GetMapBounds();
                sb.Append("Requested ").Append(requested).Append(" reaches outside the map (valid x is 0-").Append(bounds.Width - 1)
                  .Append(", valid z is 0-").Append(bounds.Height - 1).Append("); clamped to ").Append(rect).Append('.').AppendLine();
            }

            int labelWidth = ("z" + rect.MaxZ.ToString(System.Globalization.CultureInfo.InvariantCulture)).Length;
            AppendAxis(sb, rect, labelWidth);

            var used = new HashSet<Glyph>();
            for (int z = rect.MaxZ; z >= rect.MinZ; z--)
            {
                sb.Append(("z" + z.ToString(System.Globalization.CultureInfo.InvariantCulture)).PadLeft(labelWidth)).Append(' ');
                int rowStart = (z - rect.MinZ) * width;
                for (int x = 0; x < width; x++)
                {
                    Glyph glyph = Classify(cells[rowStart + x], kind);
                    used.Add(glyph);
                    sb.Append(glyph.Symbol);
                }
                sb.AppendLine();
            }

            AppendAxis(sb, rect, labelWidth);
            sb.Append("Legend: ").Append(string.Join(", ", (kind == RoofLayer ? RoofOrder : kind == BuildLayer ? BuildOrder : MapOrder).Where(used.Contains).Select(g => g.Symbol + " " + g.Meaning)));
            return sb.ToString();
        }

        /// <summary>
        /// 一格畫哪個字元。地圖層的優先序（高到低）：迷霧 → pawn（敵對 > 殖民者 > 其他）→ 建築（牆 > 門 > 砲塔 > 掩體 > 其他 > 框架 > 藍圖）
        /// → 物品 → 天然岩石 → 水 → 植物（樹 > 其他）→ 區域 → 地板 → 裸地。
        /// 區域排在地板之前（與地形並列時區域贏）：倉庫常鋪在水泥地上，空的儲藏區若被地板蓋掉，模型就看不到它。
        /// 屋頂不在地圖層：疊上去會蓋掉上面任何一層，所以另開 roof 層，只留牆與門當參照。
        /// build 層回答「重型建築放得下哪裡」：迷霧 → 已被占走（建築、藍圖、框架或天然岩石，得先移除；多格的每一格都標）→ 地形承載力（H > m > l > 無）。
        /// </summary>
        private static Glyph Classify(MapGridCell c, string layer)
        {
            if (c.Fogged) return Fog;
            if (layer == BuildLayer)
            {
                if (c.Blocked) return Blocked;
                switch (c.BuildSupport)
                {
                    case GridAffordance.Heavy: return HeavySupport;
                    case GridAffordance.Medium: return MediumSupport;
                    case GridAffordance.Light: return LightSupport;
                    default: return NoSupport;
                }
            }
            if (layer == RoofLayer)
            {
                if (c.Building == GridBuilding.Wall) return Wall;
                if (c.Building == GridBuilding.Door) return Door;
                switch (c.Roof)
                {
                    case GridRoof.Mountain: return MountainRoof;
                    case GridRoof.Natural: return NaturalRoof;
                    case GridRoof.Constructed: return BuiltRoof;
                    default: return NoRoof;
                }
            }

            switch (c.Pawn)
            {
                case GridPawn.Hostile: return Hostile;
                case GridPawn.Colonist: return Colonist;
                case GridPawn.Other: return OtherPawn;
            }
            switch (c.Building)
            {
                case GridBuilding.Wall: return Wall;
                case GridBuilding.Door: return Door;
                case GridBuilding.Turret: return Turret;
                case GridBuilding.Cover: return Cover;
                case GridBuilding.Other: return OtherBuilding;
                case GridBuilding.Frame: return Frame;
                case GridBuilding.Blueprint: return Blueprint;
            }
            if (c.HasItem) return Item;
            if (c.Rock) return Rock;
            if (c.Terrain == GridTerrain.DeepWater) return DeepWater;
            if (c.Terrain == GridTerrain.ShallowWater) return ShallowWater;
            if (c.Plant == GridPlant.Tree) return Tree;
            if (c.Plant == GridPlant.Other) return OtherPlant;
            if (c.Zone == GridZone.Stockpile) return Stockpile;
            if (c.Zone == GridZone.Growing) return Growing;
            return c.Terrain == GridTerrain.Floor ? Floor : Ground;
        }

        /// <summary>X 軸：座標數字的第一位數對準該欄，下一行的 | 標出確切的欄。</summary>
        private static void AppendAxis(StringBuilder sb, CellRectSpec rect, int labelWidth)
        {
            int width = rect.MaxX - rect.MinX + 1;
            var numbers = new StringBuilder();
            var ticks = new StringBuilder();
            for (int i = 0; i < width; i++)
            {
                int x = rect.MinX + i;
                if (x % AxisStep != 0) continue;
                string label = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
                numbers.Append(' ', Math.Max(0, i - numbers.Length)).Append(label);
                ticks.Append(' ', Math.Max(0, i - ticks.Length)).Append('|');
            }
            string pad = new string(' ', labelWidth + 1);
            sb.Append(pad).Append(numbers.ToString()).AppendLine();
            sb.Append(pad).Append(ticks.ToString()).AppendLine();
        }

        private static bool SameRect(CellRectSpec a, CellRectSpec b)
        {
            return a.MinX == b.MinX && a.MinZ == b.MinZ && a.MaxX == b.MaxX && a.MaxZ == b.MaxZ;
        }
    }
}
