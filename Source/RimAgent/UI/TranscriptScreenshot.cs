using System;
using System.IO;
using System.Text.RegularExpressions;
using RimAgent.Agent;

namespace RimAgent.UI;

internal static partial class TranscriptScreenshot
{

    public static string? TryParseSavedPath(string? result)
    {
        Match match = SavedResult.Match(result ?? string.Empty);
        return match.Success ? match.Groups["path"].Value : null;
    }

    public static string? TryParseScriptSavedPath(string? scriptResult)
    {
        MatchCollection matches = ScriptSavedLine.Matches(scriptResult ?? string.Empty);
        return matches.Count == 0 ? null : matches[matches.Count - 1].Groups["path"].Value;
    }

    public static string? TryGetPath(TranscriptEntry? entry, string? screenshotFolder)
    {
        if (entry == null || entry.Failed || entry.ToolName != "take_screenshot") return null;
        return ResolveInFolder(TryParseSavedPath(entry.Result), screenshotFolder);
    }

    /// <summary>工具結果與模型輸出都不可信，讀檔前一律經此檢查，避免被拿來讀任意檔案。</summary>
    public static string? ResolveInFolder(string? path, string? screenshotFolder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(screenshotFolder)) return null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string folder = Path.GetFullPath(screenshotFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), folder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>截圖結果的第一行；地圖範圍截圖在後面另有格線說明。</summary>
    [GeneratedRegex(@"\AScreenshot saved: (?<path>.+) \([0-9]+x[0-9]+\)\r?$", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SavedResult { get; }
    /// <summary>run_script 結果中截圖那一步的行，格式為「N. take_screenshot(參數): Screenshot saved: 路徑 (寬x高)」。</summary>
    [GeneratedRegex(@"^[0-9]+\. take_screenshot\(.*?\): Screenshot saved: (?<path>.+) \([0-9]+x[0-9]+\)\r?$", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScriptSavedLine { get; }
}
