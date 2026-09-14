using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>工具回傳給模型的文字格式。簡潔、每行一筆、附 id 與座標，讓後續指令能精確引用。</summary>
    internal static class ToolText
    {
        public const string ErrorPrefix = "Error: ";

        /// <summary>「需確認」模式擋下命令時的前綴：是預覽不是失敗，模型不該重試，應改為向玩家摘要待辦。</summary>
        public const string ConfirmationPrefix = "Needs confirmation: ";

        public static string Error(string message)
        {
            return message.StartsWith(ErrorPrefix) ? message : ErrorPrefix + message;
        }

        public static string Confirmation(string preview)
        {
            return preview.StartsWith(ConfirmationPrefix) ? preview : ConfirmationPrefix + preview;
        }

        public static string Pct(float fraction)
        {
            return ((int)(fraction * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        public static string Num(float value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        public static string Cell(int x, int z)
        {
            return "(" + x.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static string Thing(ThingInfo t)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(t.Id).Append("] ").Append(t.Label).Append(" (").Append(t.DefName).Append(") at ").Append(Cell(t.X, t.Z));
            if (t.StackCount > 1) sb.Append(" x").Append(t.StackCount);
            if (t.Kind == ThingKind.Plant && t.Harvestable) sb.Append(" harvestable");
            if (t.Kind == ThingKind.Building) sb.Append(t.PlayerOwned ? " player-owned" : " not player-owned");
            if (t.Designations.Count > 0) sb.Append(" [marked: ").Append(string.Join(", ", t.Designations)).Append(']');
            return sb.ToString();
        }

        public static string Colonist(ColonistInfo c)
        {
            return "[" + c.Id + "] " + c.Name;
        }

        public static string Names(IEnumerable<ColonistInfo> colonists)
        {
            return string.Join(", ", colonists.Select(c => c.Name));
        }

        public static string Skill(SkillInfo s)
        {
            if (s.Disabled) return s.Label + " (disabled)";
            string passion = s.Passion >= 2 ? "**" : s.Passion == 1 ? "*" : string.Empty;
            return s.Label + " " + s.Level + passion;
        }

        private static readonly Regex BoldAsterisks = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Singleline);
        private static readonly Regex BoldUnderscores = new Regex(@"__(.+?)__", RegexOptions.Singleline);
        private static readonly Regex InlineCode = new Regex(@"`(.+?)`", RegexOptions.Singleline);
        private static readonly Regex Strikethrough = new Regex(@"~~(.+?)~~", RegexOptions.Singleline);
        private static readonly Regex Link = new Regex(@"\[(.+?)\]\(.+?\)", RegexOptions.Singleline);
        private static readonly Regex HeaderLine = new Regex(@"(?m)^\s*#{1,6}\s?");

        /// <summary>
        /// 把模型回覆裡的 Markdown 標記還原成純文字再顯示（遊戲內 UI 不渲染 Markdown）。
        /// 只處理無歧義的成對標記與連結；單個 * 或 _（如 defName、數字運算）一律保留。
        /// </summary>
        public static string StripMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            string stripped = BoldAsterisks.Replace(text, "$1");
            stripped = BoldUnderscores.Replace(stripped, "$1");
            stripped = InlineCode.Replace(stripped, "$1");
            stripped = Strikethrough.Replace(stripped, "$1");
            stripped = Link.Replace(stripped, "$1");
            stripped = HeaderLine.Replace(stripped, string.Empty);
            return stripped;
        }
    }
}
