using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>把模型給的殖民者 / 座標 / 區域描述對到遊戲物件。</summary>
    public static class TargetResolver
    {
        /// <summary>代表「玩家目前框選的東西」的區域關鍵字。</summary>
        public const string SelectionKeyword = "selection";

        public static readonly string[] EveryoneKeywords = { "all", "everyone", "*" };

        public static bool IsEveryoneKeyword(string input)
        {
            string needle = (input ?? string.Empty).Trim();
            return EveryoneKeywords.Any(k => string.Equals(k, needle, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 以 id、全名、綽號、短名或其前綴指定一位殖民者；大小寫不敏感。
        /// 命中多於一位即回錯誤並列出候選。
        /// </summary>
        public static Result<ColonistInfo> ResolveColonist(string input, IReadOnlyList<ColonistInfo> colonists)
        {
            return ResolveEntity(
                input,
                colonists,
                c => c.Id,
                Names,
                Describe,
                "colonist");
        }

        /// <summary>
        /// 命令的地圖目標：物件 id 或格子座標擇一（沒給的欄位為 -1）。都沒給回成功但值為 null，
        /// 由命令決定缺目標算不算錯；兩種都給或格子只給一個座標就報錯。
        /// </summary>
        public static Result<MapTarget> ResolveMapTarget(int targetId, int x, int z)
        {
            bool hasThing = targetId >= 0;
            bool hasCell = x >= 0 || z >= 0;
            if (hasThing && hasCell)
                return Result<MapTarget>.Fail("Error: give either a target thing id or a target cell (x and z), not both.");
            if (hasCell && (x < 0 || z < 0))
                return Result<MapTarget>.Fail("Error: a target cell needs both targetX and targetZ.");
            if (hasThing) return Result<MapTarget>.Ok(MapTarget.OfThing(targetId));
            return Result<MapTarget>.Ok(hasCell ? MapTarget.OfCell(x, z) : null);
        }

        /// <summary>逗號分隔的多位殖民者，或 all / everyone / * 代表全部。</summary>
        public static Result<List<ColonistInfo>> ResolveColonists(string input, IReadOnlyList<ColonistInfo> colonists)
        {
            string needle = (input ?? string.Empty).Trim();
            if (IsEveryoneKeyword(needle))
            {
                return colonists.Count > 0
                    ? Result<List<ColonistInfo>>.Ok(colonists.ToList())
                    : Result<List<ColonistInfo>>.Fail("Error: there are no colonists.");
            }

            var result = new List<ColonistInfo>();
            foreach (string part in needle.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Result<ColonistInfo> one = ResolveColonist(part, colonists);
                if (!one.Success) return Result<List<ColonistInfo>>.Fail(one.Error);
                if (!result.Contains(one.Value)) result.Add(one.Value);
            }

            return result.Count > 0
                ? Result<List<ColonistInfo>>.Ok(result)
                : Result<List<ColonistInfo>>.Fail("Error: colonist is empty. Give names, ids, or \"all\".");
        }

        /// <summary>通用實體解析：id 精確比對 → 名稱精確比對 → 前綴比對 → 包含比對；撞名或歧義報錯並列候選。</summary>
        public static Result<T> ResolveEntity<T>(
            string input,
            IReadOnlyList<T> candidates,
            Func<T, int> getId,
            Func<T, IEnumerable<string>> getNames,
            Func<T, string> describe,
            string kindLabel,
            string emptyErrorMessage = null) where T : class
        {
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return Result<T>.Fail(emptyErrorMessage ?? $"Error: {kindLabel} is empty. Give a name or id.");
            }

            if (int.TryParse(needle, out int id))
            {
                T byId = candidates.FirstOrDefault(c => getId(c) == id);
                return byId != null
                    ? Result<T>.Ok(byId)
                    : Result<T>.Fail($"Error: no {kindLabel} has id {id}. {FormatListAll(candidates, describe, kindLabel)}");
            }

            List<T> exact = candidates.Where(c => getNames(c).Any(n => string.Equals(n, needle, StringComparison.OrdinalIgnoreCase))).ToList();
            if (exact.Count == 1) return Result<T>.Ok(exact[0]);
            if (exact.Count > 1) return FormatAmbiguous(needle, exact, describe, kindLabel);

            List<T> prefix = candidates.Where(c => getNames(c).Any(n => n != null && n.StartsWith(needle, StringComparison.OrdinalIgnoreCase))).ToList();
            if (prefix.Count == 1) return Result<T>.Ok(prefix[0]);
            if (prefix.Count > 1) return FormatAmbiguous(needle, prefix, describe, kindLabel);

            List<T> contains = candidates.Where(c => getNames(c).Any(n => n != null && n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (contains.Count == 1) return Result<T>.Ok(contains[0]);
            if (contains.Count > 1) return FormatAmbiguous(needle, contains, describe, kindLabel);

            return Result<T>.Fail($"Error: no {kindLabel} matches \"{needle}\". {FormatListAll(candidates, describe, kindLabel)}");
        }

        private static Result<T> FormatAmbiguous<T>(string needle, List<T> matches, Func<T, string> describe, string kindLabel)
        {
            string plural = PluralOf(kindLabel);
            return Result<T>.Fail(
                $"Error: \"{needle}\" matches several {plural}: {string.Join(", ", matches.Select(describe))}. Use the id.");
        }

        private static string FormatListAll<T>(IReadOnlyList<T> items, Func<T, string> describe, string kindLabel)
        {
            string plural = PluralOf(kindLabel);
            string capPlural = char.ToUpperInvariant(plural[0]) + plural.Substring(1);
            return items.Count == 0
                ? $"There are no {plural}."
                : $"{capPlural}: " + string.Join(", ", items.Select(describe)) + ".";
        }

        private static string PluralOf(string kindLabel)
        {
            if (string.IsNullOrEmpty(kindLabel)) return string.Empty;
            if (kindLabel.EndsWith("s", StringComparison.OrdinalIgnoreCase)) return kindLabel + "es";
            return kindLabel + "s";
        }

        public static Result<CellRectSpec> ResolveRect(int x1, int z1, int x2, int z2, MapBounds bounds)
        {
            CellRectSpec rect = CellRectSpec.FromCorners(x1, z1, x2, z2);
            if (!bounds.Contains(rect.MinX, rect.MinZ) || !bounds.Contains(rect.MaxX, rect.MaxZ))
            {
                return Result<CellRectSpec>.Fail(
                    $"Error: rectangle {rect} is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}.");
            }
            return Result<CellRectSpec>.Ok(rect);
        }

        /// <summary>矩形與地圖有重疊就裁到地圖內，完全在地圖外才報錯（純看的工具用，不像 <see cref="ResolveRect"/> 一格越界就拒絕）。</summary>
        public static Result<CellRectSpec> ClampRect(int x1, int z1, int x2, int z2, MapBounds bounds)
        {
            CellRectSpec rect = CellRectSpec.FromCorners(x1, z1, x2, z2);
            if (rect.MaxX < 0 || rect.MaxZ < 0 || rect.MinX >= bounds.Width || rect.MinZ >= bounds.Height)
            {
                return Result<CellRectSpec>.Fail(
                    $"Error: rectangle {rect} is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}.");
            }
            return Result<CellRectSpec>.Ok(new CellRectSpec(
                Math.Max(0, rect.MinX), Math.Max(0, rect.MinZ), Math.Min(bounds.Width - 1, rect.MaxX), Math.Min(bounds.Height - 1, rect.MaxZ)));
        }

        /// <summary>座標在地圖內回傳 null，否則回傳可轉述給模型的錯誤訊息。</summary>
        public static string CheckCell(int x, int z, MapBounds bounds)
        {
            if (bounds.Contains(x, z)) return null;
            return $"Error: cell ({x},{z}) is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}.";
        }

        public static bool IsSelectionKeyword(string area)
        {
            return string.Equals((area ?? string.Empty).Trim(), SelectionKeyword, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> Names(ColonistInfo c)
        {
            if (!string.IsNullOrEmpty(c.Name)) yield return c.Name;
            if (!string.IsNullOrEmpty(c.Nickname)) yield return c.Nickname;
            if (!string.IsNullOrEmpty(c.FullName)) yield return c.FullName;
        }

        private static string Describe(ColonistInfo c)
        {
            return $"[{c.Id}] {c.FullName}";
        }
    }
}
