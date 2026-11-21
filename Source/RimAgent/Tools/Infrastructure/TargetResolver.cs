#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimAgent.Game;

namespace RimAgent.Tools;

public static class TargetResolver
{
    public const string SelectionKeyword = "selection";

    public static readonly string[] EveryoneKeywords = ["all", "everyone", "*"];

    public static bool IsEveryoneKeyword(string input)
    {
        string needle = (input ?? string.Empty).Trim();
        return EveryoneKeywords.Any(k => string.Equals(k, needle, StringComparison.OrdinalIgnoreCase));
    }

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

    /// <summary>命令的地圖目標：都沒給回成功但值為 null，由命令決定缺目標算不算錯。</summary>
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

    public static Result<List<ColonistInfo>> ResolveColonists(string input, IReadOnlyList<ColonistInfo> colonists)
    {
        string needle = (input ?? string.Empty).Trim();
        if (IsEveryoneKeyword(needle))
        {
            return colonists.Count > 0
                ? Result<List<ColonistInfo>>.Ok([.. colonists])
                : Result<List<ColonistInfo>>.Fail("Error: there are no colonists.");
        }

        var result = new List<ColonistInfo>();
        foreach (string part in needle.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            Result<ColonistInfo> one = ResolveColonist(part, colonists);
            if (!one.Success) return Result<List<ColonistInfo>>.Fail(one.Error);
            if (!result.Contains(one.Value)) result.Add(one.Value);
        }

        return result.Count > 0
            ? Result<List<ColonistInfo>>.Ok(result)
            : Result<List<ColonistInfo>>.Fail("Error: colonist is empty. Give names, ids, or \"all\".");
    }

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

        if (int.TryParse(StripIdDecoration(needle), CultureInfo.InvariantCulture, out int id))
        {
            T byId = candidates.FirstOrDefault(c => getId(c) == id);
            return byId != null
                ? Result<T>.Ok(byId)
                : Result<T>.Fail(string.Create(CultureInfo.InvariantCulture, $"Error: no {kindLabel} has id {id}. {FormatListAll(candidates, describe, kindLabel)}"));
        }

        List<T> exact = [.. candidates.Where(c => getNames(c).Any(n => string.Equals(n, needle, StringComparison.OrdinalIgnoreCase)))];
        if (exact.Count == 1) return Result<T>.Ok(exact[0]);
        if (exact.Count > 1) return FormatAmbiguous(needle, exact, describe, kindLabel);

        List<T> prefix = [.. candidates.Where(c => getNames(c).Any(n => n != null && n.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))];
        if (prefix.Count == 1) return Result<T>.Ok(prefix[0]);
        if (prefix.Count > 1) return FormatAmbiguous(needle, prefix, describe, kindLabel);

        List<T> contains = [.. candidates.Where(c => getNames(c).Any(n => n != null && n.Contains(needle, StringComparison.OrdinalIgnoreCase)))];
        if (contains.Count == 1) return Result<T>.Ok(contains[0]);
        if (contains.Count > 1) return FormatAmbiguous(needle, contains, describe, kindLabel);

        return Result<T>.Fail($"Error: no {kindLabel} matches \"{needle}\". {FormatListAll(candidates, describe, kindLabel)}");
    }

    private static string StripIdDecoration(string needle)
    {
        if (needle.Length > 2 && needle[0] == '[' && needle[needle.Length - 1] == ']')
        {
            return needle.Substring(1, needle.Length - 2).Trim();
        }
        if (needle.Length > 1 && needle[0] == '#') return needle.Substring(1).Trim();
        return needle;
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
                string.Create(CultureInfo.InvariantCulture, $"Error: rectangle {rect} is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}."));
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
                string.Create(CultureInfo.InvariantCulture, $"Error: rectangle {rect} is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}."));
        }
        return Result<CellRectSpec>.Ok(new CellRectSpec(
            Math.Max(0, rect.MinX), Math.Max(0, rect.MinZ), Math.Min(bounds.Width - 1, rect.MaxX), Math.Min(bounds.Height - 1, rect.MaxZ)));
    }

    public static string CheckCell(int x, int z, MapBounds bounds)
    {
        if (bounds.Contains(x, z)) return null;
        return string.Create(CultureInfo.InvariantCulture, $"Error: cell ({x},{z}) is outside the map. Valid x is 0-{bounds.Width - 1}, valid z is 0-{bounds.Height - 1}.");
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
        return string.Create(CultureInfo.InvariantCulture, $"[{c.Id}] {c.FullName}");
    }
}
