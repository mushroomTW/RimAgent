using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimAgent.Game;

namespace RimAgent.Tools;

/// <summary>
/// 把玩家或模型給的名稱對到 Def。依序嘗試：精確 defName → 精確 label → 前綴 → 包含
/// （雙向：輸入包含 label，或 label / defName 包含輸入）→ 忽略分隔符的包含。同一層命中多於一個即視為歧義；
/// 全部落空時附上最接近的候選。
/// </summary>
public static class DefResolver
{
    private const int MaxListedCandidates = 10;
    private const int MaxSuggestions = 5;
    private const int MinWordLength = 3;
    private const string NoMatchPrefix = "no ";

    /// <summary>拆字詞：駝峰（FueledResearchBench）、全大寫縮寫、數字、非 ASCII 連續字（中文名）。</summary>
    private static readonly Regex Words = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+|[^\x00-\x7F]+", RegexOptions.CultureInvariant);

    public static Result<DefInfo> Resolve(string input, IReadOnlyList<DefInfo> candidates, string kindLabel)
    {
        string needle = (input ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return Result<DefInfo>.Fail($"Error: {kindLabel} name is empty.");
        }

        List<DefInfo> exactDef = [.. candidates.Where(c => string.Equals(c.DefName, needle, StringComparison.OrdinalIgnoreCase))];
        if (exactDef.Count == 1) return Result<DefInfo>.Ok(exactDef[0]);

        List<DefInfo> exactLabel = [.. candidates.Where(c => string.Equals(c.Label, needle, StringComparison.OrdinalIgnoreCase))];
        if (exactLabel.Count == 1) return Result<DefInfo>.Ok(exactLabel[0]);
        if (exactLabel.Count > 1) return Ambiguous(needle, exactLabel, kindLabel);

        List<DefInfo> prefix = [.. candidates.Where(c => StartsWith(c.DefName, needle) || StartsWith(c.Label, needle))];
        if (prefix.Count == 1) return Result<DefInfo>.Ok(prefix[0]);
        if (prefix.Count > 1) return Ambiguous(needle, prefix, kindLabel);

        List<DefInfo> contains = [.. candidates
            .Where(c => Contains(c.DefName, needle) || Contains(c.Label, needle) ||
                        // 模組 def 常不填 label（Def.label 預設 null）。
                        (!string.IsNullOrEmpty(c.Label) && Contains(needle, c.Label)))];
        if (contains.Count == 1) return Result<DefInfo>.Ok(contains[0]);
        if (contains.Count > 1) return Ambiguous(needle, contains, kindLabel);

        // 忽略空白、底線、連字號再比一次：非英文遊戲的 label 是在地語言，英文只能對 defName，
        // 模型常把 SimpleResearchBench 寫成 "research bench"、Plant_Rice 寫成 "plant rice"。
        // defName 與 label 的去分隔字串只算一次，比對與建議共用（換行隔開，輸入不會跨兩者命中）。
        List<KeyValuePair<DefInfo, string>> compacted = [.. candidates.Select(c => new KeyValuePair<DefInfo, string>(c, Compact(c.DefName) + "\n" + Compact(c.Label)))];
        string compactNeedle = Compact(needle);
        if (compactNeedle.Length > 0)
        {
            List<DefInfo> compact = [.. compacted
                .Where(c => Contains(c.Value, compactNeedle))
                .Select(c => c.Key)];
            if (compact.Count == 1) return Result<DefInfo>.Ok(compact[0]);
            if (compact.Count > 1) return Ambiguous(needle, compact, kindLabel);
        }

        return Result<DefInfo>.Fail($"Error: {NoMatchPrefix}{kindLabel} matches \"{needle}\".{Closest(needle, compacted)}");
    }

    /// <summary>解析失敗是「一個都對不到」（而不是名稱為空或多個候選的歧義）。</summary>
    public static bool IsNoMatch(Result<DefInfo> result)
    {
        return !result.Success && result.Error != null
            && result.Error.StartsWith("Error: " + NoMatchPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 找不到時附上最像的幾個（以輸入拆出的字詞命中數排序），讓模型換對名稱而不是盲猜。
    /// 一個字詞都沒命中就不附，免得列出無關的東西。
    /// </summary>
    private static string Closest(string needle, List<KeyValuePair<DefInfo, string>> compacted)
    {
        List<string> words = [.. Words.Matches(needle).Cast<Match>()
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length >= MinWordLength)
            .Distinct()];
        if (words.Count == 0) return string.Empty;

        List<DefInfo> closest = [.. compacted
            .Select(c => new { Def = c.Key, Hits = words.Count(w => Contains(c.Value, w)) })
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Def.DefName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(x => x.Def)];
        if (closest.Count == 0) return string.Empty;
        return " Closest: " + string.Join(", ", closest.Select(d => $"{d.DefName} ({d.Label})")) + ". Use one of these defNames.";
    }

    private static string Compact(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch != ' ' && ch != '_' && ch != '-') sb.Append(ch);
        }
        return sb.ToString();
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
