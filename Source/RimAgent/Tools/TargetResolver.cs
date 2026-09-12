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

        private static readonly string[] EveryoneKeywords = { "all", "everyone", "*" };

        /// <summary>
        /// 以 id、全名、綽號、短名或其前綴指定一位殖民者；大小寫不敏感。
        /// 命中多於一位即回錯誤並列出候選。
        /// </summary>
        public static ResolveResult<ColonistInfo> ResolveColonist(string input, IReadOnlyList<ColonistInfo> colonists)
        {
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return ResolveResult<ColonistInfo>.Fail("Error: colonist is empty. Give a name or id.");
            }

            if (int.TryParse(needle, out int id))
            {
                ColonistInfo byId = colonists.FirstOrDefault(c => c.Id == id);
                return byId != null
                    ? ResolveResult<ColonistInfo>.Ok(byId)
                    : ResolveResult<ColonistInfo>.Fail($"Error: no colonist has id {id}. {ListAll(colonists)}");
            }

            List<ColonistInfo> exact = colonists.Where(c => Names(c).Any(n => Eq(n, needle))).ToList();
            if (exact.Count == 1) return ResolveResult<ColonistInfo>.Ok(exact[0]);
            if (exact.Count > 1) return Ambiguous(needle, exact);

            List<ColonistInfo> prefix = colonists.Where(c => Names(c).Any(n => n.StartsWith(needle, StringComparison.OrdinalIgnoreCase))).ToList();
            if (prefix.Count == 1) return ResolveResult<ColonistInfo>.Ok(prefix[0]);
            if (prefix.Count > 1) return Ambiguous(needle, prefix);

            List<ColonistInfo> contains = colonists.Where(c => Names(c).Any(n => n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (contains.Count == 1) return ResolveResult<ColonistInfo>.Ok(contains[0]);
            if (contains.Count > 1) return Ambiguous(needle, contains);

            return ResolveResult<ColonistInfo>.Fail($"Error: no colonist matches \"{needle}\". {ListAll(colonists)}");
        }

        /// <summary>逗號分隔的多位殖民者，或 all / everyone / * 代表全部。</summary>
        public static ResolveResult<List<ColonistInfo>> ResolveColonists(string input, IReadOnlyList<ColonistInfo> colonists)
        {
            string needle = (input ?? string.Empty).Trim();
            if (EveryoneKeywords.Any(k => Eq(k, needle)))
            {
                return colonists.Count > 0
                    ? ResolveResult<List<ColonistInfo>>.Ok(colonists.ToList())
                    : ResolveResult<List<ColonistInfo>>.Fail("Error: there are no colonists.");
            }

            var result = new List<ColonistInfo>();
            foreach (string part in needle.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ResolveResult<ColonistInfo> one = ResolveColonist(part, colonists);
                if (!one.Success) return ResolveResult<List<ColonistInfo>>.Fail(one.Error);
                if (!result.Contains(one.Value)) result.Add(one.Value);
            }

            return result.Count > 0
                ? ResolveResult<List<ColonistInfo>>.Ok(result)
                : ResolveResult<List<ColonistInfo>>.Fail("Error: colonist is empty. Give names, ids, or \"all\".");
        }

        public static ResolveResult<CellRectSpec> ResolveRect(int x1, int z1, int x2, int z2, MapBounds bounds)
        {
            CellRectSpec rect = CellRectSpec.FromCorners(x1, z1, x2, z2);
            if (!bounds.Contains(rect.MinX, rect.MinZ) || !bounds.Contains(rect.MaxX, rect.MaxZ))
            {
                return ResolveResult<CellRectSpec>.Fail(
                    $"Error: rectangle {rect} is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}.");
            }
            return ResolveResult<CellRectSpec>.Ok(rect);
        }

        /// <summary>座標在地圖內回傳 null，否則回傳可轉述給模型的錯誤訊息。</summary>
        public static string CheckCell(int x, int z, MapBounds bounds)
        {
            if (bounds.Contains(x, z)) return null;
            return $"Error: cell ({x},{z}) is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}.";
        }

        public static bool IsSelectionKeyword(string area)
        {
            return Eq((area ?? string.Empty).Trim(), SelectionKeyword);
        }

        private static IEnumerable<string> Names(ColonistInfo c)
        {
            if (!string.IsNullOrEmpty(c.Name)) yield return c.Name;
            if (!string.IsNullOrEmpty(c.Nickname)) yield return c.Nickname;
            if (!string.IsNullOrEmpty(c.FullName)) yield return c.FullName;
        }

        private static ResolveResult<ColonistInfo> Ambiguous(string needle, List<ColonistInfo> matches)
        {
            return ResolveResult<ColonistInfo>.Fail(
                $"Error: \"{needle}\" matches several colonists: {string.Join(", ", matches.Select(Describe))}. Use the id.");
        }

        private static string ListAll(IReadOnlyList<ColonistInfo> colonists)
        {
            return colonists.Count == 0
                ? "There are no colonists."
                : "Colonists: " + string.Join(", ", colonists.Select(Describe)) + ".";
        }

        private static string Describe(ColonistInfo c)
        {
            return $"[{c.Id}] {c.FullName}";
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
