using System;
using System.IO;
using System.Text.RegularExpressions;
using RimAgent.Agent;

namespace RimAgent.UI;

/// <summary>僅接受截圖工具成功回傳、且位於遊戲截圖資料夾的 PNG 路徑。</summary>
internal static class TranscriptScreenshot
{
    private static readonly Regex SavedResult = new(
        @"^Screenshot saved: (.+) \([0-9]+x[0-9]+\)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>run_script 結果中截圖那一步的行，格式為「N. take_screenshot(參數): Screenshot saved: 路徑 (寬x高)」。</summary>
    private static readonly Regex ScriptSavedLine = new(
        @"^[0-9]+\. take_screenshot\(.*?\): Screenshot saved: (.+) \([0-9]+x[0-9]+\)\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline,
        TimeSpan.FromSeconds(1));

    /// <summary>從截圖工具的結果取出存檔路徑（未驗證資料夾）；格式不符回傳 null。</summary>
    public static string TryParseSavedPath(string result)
    {
        Match match = SavedResult.Match(result ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>從 run_script 的合併結果取出最後一次截圖的存檔路徑（未驗證資料夾）；沒有截圖步驟回傳 null。</summary>
    public static string TryParseScriptSavedPath(string scriptResult)
    {
        MatchCollection matches = ScriptSavedLine.Matches(scriptResult ?? string.Empty);
        return matches.Count == 0 ? null : matches[matches.Count - 1].Groups[1].Value;
    }

    public static string TryGetPath(TranscriptEntry entry, string screenshotFolder)
    {
        if (entry == null || entry.Failed || entry.ToolName != "take_screenshot") return null;
        return ResolveInFolder(TryParseSavedPath(entry.Result), screenshotFolder);
    }

    /// <summary>
    /// 路徑位在截圖資料夾（不含子資料夾）且是 PNG 時回傳完整路徑，否則 null。
    /// 工具結果與模型輸出都不可信，讀檔前一律經此檢查，避免被拿來讀任意檔案。
    /// </summary>
    public static string ResolveInFolder(string path, string screenshotFolder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(screenshotFolder)) return null;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string folder = Path.GetFullPath(screenshotFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(fullPath), folder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase)) return null;
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }
}
