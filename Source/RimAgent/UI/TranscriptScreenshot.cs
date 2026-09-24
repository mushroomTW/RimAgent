using System;
using System.IO;
using System.Text.RegularExpressions;
using RimAgent.Agent;

namespace RimAgent.UI
{
    /// <summary>僅接受截圖工具成功回傳、且位於遊戲截圖資料夾的 PNG 路徑。</summary>
    internal static class TranscriptScreenshot
    {
        private static readonly Regex SavedResult = new Regex(
            @"^Screenshot saved: (.+) \([0-9]+x[0-9]+\)$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string TryGetPath(TranscriptEntry entry, string screenshotFolder)
        {
            if (entry == null || entry.Failed || entry.ToolName != "take_screenshot"
                || string.IsNullOrWhiteSpace(screenshotFolder)) return null;

            Match match = SavedResult.Match(entry.Result ?? string.Empty);
            if (!match.Success) return null;

            try
            {
                string fullPath = Path.GetFullPath(match.Groups[1].Value);
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
}
