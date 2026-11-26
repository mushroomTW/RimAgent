using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimAgent.Game;

namespace RimAgent.Tools;

public static partial class DefResolver
{
    private const int MaxListedCandidates = 10;
    private const int MaxSuggestions = 5;
    private const int MinWordLength = 3;
    private const string NoMatchPrefix = "no ";

    public static Result<DefInfo> Resolve(string input, IReadOnlyList<DefInfo> candidates, string kindLabel, string defPrefix = "")
    {
        return Resolve(input, candidates, kindLabel, defPrefix, out _);
    }

    /// <param name="ambiguous">名稱對到多個時的所有候選；其他結果為空。</param>
    /// <param name="abbreviations">接受省略中間字詞的 defName；會動手的工具不要開，免得做到別的東西上。</param>
    public static Result<DefInfo> Resolve(string input, IReadOnlyList<DefInfo> candidates, string kindLabel, string defPrefix, out IReadOnlyList<DefInfo> ambiguous, bool abbreviations = false)
    {
        ambiguous = [];
        string needle = (input ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return Result<DefInfo>.Fail($"Error: {kindLabel} name is empty.");
        }

        List<DefInfo> exactDef = [.. candidates.Where(c => string.Equals(c.DefName, needle, StringComparison.OrdinalIgnoreCase))];
        if (exactDef.Count == 1) return Result<DefInfo>.Ok(exactDef[0]);

        List<DefInfo> exactLabel = [.. candidates.Where(c => string.Equals(c.Label, needle, StringComparison.OrdinalIgnoreCase))];
        if (exactLabel.Count == 1) return Result<DefInfo>.Ok(exactLabel[0]);
        if (exactLabel.Count > 1) return Ambiguous(needle, exactLabel, kindLabel, out ambiguous);

        // 「simple research bench」要的是 SimpleResearchBench，不是也含這幾個字的 AncientSimpleResearchBench；
        // 有 defPrefix 時「corn」要的是 Plant_Corn，不是 Plant_Fibercorn；混合清單不能拿掉前綴，否則會蓋掉 RawCorn。
        string compactExact = Compact(needle);
        List<DefInfo> sameName = [.. candidates.Where(c => IsSameCompactName(c, compactExact, Compact(defPrefix)))];
        if (sameName.Count == 1) return Result<DefInfo>.Ok(sameName[0]);

        List<DefInfo> prefix = [.. candidates.Where(c => StartsWith(c.DefName, needle) || StartsWith(c.Label, needle))];
        if (prefix.Count == 1) return Result<DefInfo>.Ok(prefix[0]);
        if (prefix.Count > 1) return Ambiguous(needle, prefix, kindLabel, out ambiguous);

        List<DefInfo> contains = TryMatchContains(candidates, needle);
        if (contains.Count == 1) return Result<DefInfo>.Ok(contains[0]);
        if (contains.Count > 1) return Ambiguous(needle, contains, kindLabel, out ambiguous);

        // 忽略空白、底線、連字號再比一次：非英文遊戲的 label 是在地語言，英文只能對 defName，
        // 模型常把 SimpleResearchBench 寫成 "research bench"、Plant_Rice 寫成 "plant rice"。
        List<KeyValuePair<DefInfo, string>> compacted = [.. candidates.Select(c => new KeyValuePair<DefInfo, string>(c, Compact(c.DefName) + "\n" + Compact(c.Label)))];
        List<DefInfo> compact = TryMatchCompact(compacted, needle);
        if (compact.Count == 1) return Result<DefInfo>.Ok(compact[0]);
        if (compact.Count > 1) return Ambiguous(needle, compact, kindLabel, out ambiguous);

        // 「wood floor」要的是 WoodPlankFloor：首尾字詞相同、中間省略，且只有一個這樣的才算。
        List<DefInfo> abbreviated = TryMatchAbbreviation(candidates, needle, abbreviations);
        if (abbreviated.Count == 1) return Result<DefInfo>.Ok(abbreviated[0]);

        return Result<DefInfo>.Fail($"Error: {NoMatchPrefix}{kindLabel} matches \"{needle}\".{Closest(needle, compacted)}");
    }

    private static List<DefInfo> TryMatchAbbreviation(IReadOnlyList<DefInfo> candidates, string needle, bool abbreviations)
    {
        if (!abbreviations) return [];
        string[] parts = SplitWords(needle);
        if (parts.Length <= 1) return [];
        return [.. candidates.Where(c => IsAbbreviation(parts, SplitWords(c.DefName)))];
    }

    private static bool IsSameCompactName(DefInfo candidate, string compactNeedle, string compactPrefix)
    {
        if (compactNeedle.Length == 0) return false;
        return string.Equals(Compact(candidate.DefName), compactNeedle, StringComparison.OrdinalIgnoreCase)
            || (compactPrefix.Length > 0 && string.Equals(Compact(candidate.DefName), compactPrefix + compactNeedle, StringComparison.OrdinalIgnoreCase))
            || string.Equals(Compact(candidate.Label), compactNeedle, StringComparison.OrdinalIgnoreCase);
    }

    private static List<DefInfo> TryMatchContains(IReadOnlyList<DefInfo> candidates, string needle)
    {
        return [.. candidates
            .Where(c => Contains(c.DefName, needle) || Contains(c.Label, needle) ||
                        // 模組 def 常不填 label（Def.label 預設 null）。
                        (!string.IsNullOrEmpty(c.Label) && Contains(needle, c.Label)))];
    }

    private static List<DefInfo> TryMatchCompact(List<KeyValuePair<DefInfo, string>> compacted, string needle)
    {
        string compactNeedle = Compact(needle);
        if (compactNeedle.Length == 0) return [];
        return [.. compacted
            .Where(c => Contains(c.Value, compactNeedle))
            .Select(c => c.Key)];
    }

    public static bool IsNoMatch(Result<DefInfo> result)
    {
        return !result.Success && result.Error != null
            && result.Error.StartsWith("Error: " + NoMatchPrefix, StringComparison.Ordinal);
    }

    /// <summary>找不到時附上最像的候選讓模型換對名稱；一個字詞都沒命中就不附，免得列出無關的東西。</summary>
    private static string Closest(string needle, List<KeyValuePair<DefInfo, string>> compacted)
    {
        List<string> words = [.. Words.Matches(needle).Cast<Match>()
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length >= MinWordLength)
            .Distinct(StringComparer.Ordinal)];
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
        return new string([.. text.Where(ch => ch != ' ' && ch != '_' && ch != '-')]);
    }

    private static Result<DefInfo> Ambiguous(string needle, List<DefInfo> matches, string kindLabel, out IReadOnlyList<DefInfo> ambiguous)
    {
        ambiguous = matches;
        string listed = string.Join(", ", matches.Take(MaxListedCandidates).Select(m => $"{m.DefName} ({m.Label}{(m.Note.Length > 0 ? ", " + m.Note : string.Empty)})"));
        string more = matches.Count > MaxListedCandidates ? string.Create(CultureInfo.InvariantCulture, $" and {matches.Count - MaxListedCandidates} more") : string.Empty;
        return Result<DefInfo>.Fail($"Error: \"{needle}\" matches several {kindLabel}s: {listed}{more}. Use the exact defName.");
    }

    private static string[] SplitWords(string text) => [.. Words.Matches(text).Select(m => m.Value.ToLowerInvariant())];

    /// <summary>首尾字詞相同，其餘依序出現。</summary>
    private static bool IsAbbreviation(string[] parts, string[] words)
    {
        if (words.Length < parts.Length || words[0] != parts[0] || words[^1] != parts[^1]) return false;
        int next = 0;
        foreach (string word in words)
        {
            if (next < parts.Length && word == parts[next]) next++;
        }
        return next == parts.Length;
    }

    private static bool StartsWith(string text, string prefix)
    {
        return !string.IsNullOrEmpty(text) && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string text, string part)
    {
        return !string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(part) && text.Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+|[^\x00-\x7F]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Words { get; }
}
