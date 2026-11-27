using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace RimAgent.Tests;

/// <summary>
/// 架構邊界的 lint 測試：工具層與 MCP 層只能透過 <c>IGameWorld</c> 存取遊戲，
/// 遊戲 API（Verse、RimWorld、UnityEngine、LudeonTK）只出現在 Game/ 與 UI/。
/// </summary>
[TestFixture]
public partial class ArchitectureBoundaryTests
{
    [GeneratedRegex(
        @"\busing\s+(static\s+)?(global::)?(Verse|RimWorld|UnityEngine|LudeonTK)\b" +
        @"|\b(global::)?(Verse|RimWorld|UnityEngine|LudeonTK)\.[A-Z]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex GameApiReferenceRegex { get; }

    [GeneratedRegex(@"^\s*#\s*nullable\s+disable\b", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex NullableDisableRegex { get; }

    [TestCase("RimAgent")]
    [TestCase("RimAgent.A2A")]
    public void NoNullableDisableDirectivesInProductionCode(string projectDirectory)
    {
        string sourceDir = Path.GetDirectoryName(FindSourceRoot());
        string root = Path.Combine(sourceDir, projectDirectory);
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string code = StripCommentsAndLiterals(File.ReadAllText(file));
            if (NullableDisableRegex.IsMatch(code))
            {
                violations.Add(Path.GetRelativePath(sourceDir, file));
            }
        }

        Assert.That(violations, Is.Empty,
            "產品程式碼已全面啟用 Nullable，不得再新增 #nullable disable。");
    }

    [TestCase("Tools")]
    [TestCase("Mcp")]
    public void DirectoryDoesNotReferenceGameApi(string directory)
    {
        string root = Path.Combine(FindSourceRoot(), directory);
        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = StripCommentsAndLiterals(File.ReadAllText(file)).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = GameApiReferenceRegex.Match(lines[i]);
                if (match.Success)
                {
                    violations.Add(Path.GetRelativePath(FindSourceRoot(), file) + ":" + (i + 1).ToString(CultureInfo.InvariantCulture) + " " + match.Value.Trim());
                }
            }
        }

        Assert.That(violations, Is.Empty,
            directory + "/ 直接參考了遊戲 API。請把需要的操作加到 IGameWorld，在 Game/RimWorldGameWorld 實作，" +
            "工具只呼叫 IGameWorld（測試用 FakeGameWorld）。");
    }

    [Test]
    public void StripperRemovesCommentsAndLiteralsButKeepsCode()
    {
        const string Source = "// Verse.Log\nvar a = \"Verse.Log\"; /* RimWorld.Find */ Verse.Log.Warning(@\"x\"\"y\");\nchar c = '\"';";

        string code = StripCommentsAndLiterals(Source);

        Assert.That(GameApiReferenceRegex.Matches(code).Select(m => m.Value), Is.EqualTo((string[])["Verse.L"]));
        Assert.That(code.Split('\n'), Has.Length.EqualTo(3));
    }

    [Test]
    public void StripperKeepsInterpolationHoles()
    {
        const string Source = "var a = $\"{{Verse.X}} {RimWorld.Faction.OfPlayer} {F(\"}\")}\"; var b = $@\"{Verse.Find.Maps}\"; var c = $$\"\"\"{Verse.Log} {{UnityEngine.Time}}\"\"\";";

        string code = StripCommentsAndLiterals(Source);

        Assert.That(GameApiReferenceRegex.Matches(code).Select(m => m.Value),
            Is.EqualTo((string[])["RimWorld.F", "Verse.F", "UnityEngine.T"]));
        Assert.That(code, Has.Length.EqualTo(Source.Length));
    }

    /// <summary>把註解、字串與字元常值換成空白並保留換行，讓比對只看程式碼且行號不變；插值字串的 {…} 是程式碼，保留。</summary>
    private static string StripCommentsAndLiterals(string source)
    {
        var result = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            i = AppendCode(source, i, result, stopAtCloseBrace: false);
            if (i < source.Length) i = Blank(source, i, i + 1, result);
        }
        return result.ToString();
    }

    /// <summary>複製程式碼直到結尾，或 <paramref name="stopAtCloseBrace"/> 時停在同層的 <c>}</c>（插值洞的結尾）。</summary>
    private static int AppendCode(string source, int i, StringBuilder result, bool stopAtCloseBrace)
    {
        int depth = 0;
        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                int end = source.IndexOf('\n', i);
                i = Blank(source, i, end < 0 ? source.Length : end, result);
            }
            else if (c == '/' && next == '*')
            {
                int end = source.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                i = Blank(source, i, end < 0 ? source.Length : end + 2, result);
            }
            else if (c == '"' || c == '\'' || ((c == '@' || c == '$') && (next == '"' || next == '@' || next == '$')))
            {
                i = AppendLiteral(source, i, result);
            }
            else if (stopAtCloseBrace && c == '}' && depth == 0)
            {
                return i;
            }
            else
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                result.Append(c);
                i++;
            }
        }
        return i;
    }

    /// <summary>字串或字元常值換成空白，插值洞交回 <see cref="AppendCode"/>；回傳常值結束後的位置。</summary>
    private static int AppendLiteral(string source, int start, StringBuilder result)
    {
        int i = start;
        bool verbatim = false;
        int dollars = 0;
        while (i < source.Length && source[i] is '@' or '$')
        {
            verbatim |= source[i] == '@';
            if (source[i] == '$') dollars++;
            i++;
        }
        if (i >= source.Length) return Blank(source, start, i, result);

        char quote = source[i];
        int quotes = 1;
        bool raw = string.CompareOrdinal(source, i, "\"\"\"", 0, 3) == 0;
        if (raw)
        {
            while (i + quotes < source.Length && source[i + quotes] == '"') quotes++;
        }
        i = Blank(source, start, i + quotes, result);
        string closing = new(quote, quotes);
        // 原始插值字串的洞要連續 dollars 個大括號；一般插值字串的 {{ 是跳脫。
        int holeBraces = raw ? dollars : 1;

        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (!raw && !verbatim && c == '\\')
            {
                i = Blank(source, i, Math.Min(i + 2, source.Length), result);
            }
            else if (!raw && verbatim && c == quote && next == quote)
            {
                i = Blank(source, i, i + 2, result);
            }
            else if (string.CompareOrdinal(source, i, closing, 0, quotes) == 0)
            {
                return Blank(source, i, i + quotes, result);
            }
            else if (!raw && !verbatim && c == '\n')
            {
                return i;
            }
            else if (dollars > 0 && !raw && c == '{' && next == '{')
            {
                i = Blank(source, i, i + 2, result);
            }
            else if (dollars > 0 && c == '{' && string.CompareOrdinal(source, i, new string('{', holeBraces), 0, holeBraces) == 0)
            {
                int run = 0;
                while (i + run < source.Length && source[i + run] == '{') run++;
                i = Blank(source, i, i + run, result);
                i = AppendCode(source, i, result, stopAtCloseBrace: true);
            }
            else
            {
                i = Blank(source, i, i + 1, result);
            }
        }
        return i;
    }

    private static int Blank(string source, int from, int to, StringBuilder result)
    {
        for (int j = from; j < to; j++)
        {
            result.Append(source[j] == '\n' ? '\n' : ' ');
        }
        return to;
    }

    /// <summary>從本測試檔的編譯期路徑往上找，測試輸出到其他目錄（例如 <c>dotnet test -o</c>）時也找得到。</summary>
    private static string FindSourceRoot([CallerFilePath] string testFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(testFile)); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Source", "RimAgent");
            if (File.Exists(Path.Combine(candidate, "RimAgent.csproj")))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("找不到 Source/RimAgent");
    }
}
