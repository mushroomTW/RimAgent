using System;
using System.Collections.Generic;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>
    /// 把玩家或模型給的名稱對到 Def。依序嘗試：精確 defName → 精確 label → 前綴 → 包含
    /// （雙向：輸入包含 label，或 label / defName 包含輸入）。同一層命中多於一個即視為歧義。
    /// </summary>
    public static class DefResolver
    {
        private const int MaxListedCandidates = 10;

        public static Result<DefInfo> Resolve(string input, IReadOnlyList<DefInfo> candidates, string kindLabel)
        {
            string needle = (input ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return Result<DefInfo>.Fail($"Error: {kindLabel} name is empty.");
            }

            List<DefInfo> exactDef = candidates.Where(c => string.Equals(c.DefName, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactDef.Count == 1) return Result<DefInfo>.Ok(exactDef[0]);

            List<DefInfo> exactLabel = candidates.Where(c => string.Equals(c.Label, needle, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exactLabel.Count == 1) return Result<DefInfo>.Ok(exactLabel[0]);
            if (exactLabel.Count > 1) return Ambiguous(needle, exactLabel, kindLabel);

            List<DefInfo> prefix = candidates
                .Where(c => StartsWith(c.DefName, needle) || StartsWith(c.Label, needle))
                .ToList();
            if (prefix.Count == 1) return Result<DefInfo>.Ok(prefix[0]);
            if (prefix.Count > 1) return Ambiguous(needle, prefix, kindLabel);

            List<DefInfo> contains = candidates
                .Where(c => Contains(c.DefName, needle) || Contains(c.Label, needle) ||
                            // 模組 def 常不填 label（Def.label 預設 null）。
                            (!string.IsNullOrEmpty(c.Label) && Contains(needle, c.Label)))
                .ToList();
            if (contains.Count == 1) return Result<DefInfo>.Ok(contains[0]);
            if (contains.Count > 1) return Ambiguous(needle, contains, kindLabel);

            return Result<DefInfo>.Fail($"Error: no {kindLabel} matches \"{needle}\".");
        }

        private static Result<DefInfo> Ambiguous(string needle, List<DefInfo> matches, string kindLabel)
        {
            string listed = string.Join(", ", matches.Take(MaxListedCandidates).Select(m => $"{m.DefName} ({m.Label})"));
            string more = matches.Count > MaxListedCandidates ? $" and {matches.Count - MaxListedCandidates} more" : string.Empty;
            return Result<DefInfo>.Fail($"Error: \"{needle}\" matches several {kindLabel}s: {listed}{more}. Use the exact defName.");
        }

        private static bool StartsWith(string text, string prefix)
        {
            return !string.IsNullOrEmpty(text) && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string text, string part)
        {
            return !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(part) && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
