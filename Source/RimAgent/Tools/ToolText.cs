using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimAgent.Game;

namespace RimAgent.Tools
{
    /// <summary>工具回傳給模型的文字格式。簡潔、每行一筆、附 id 與座標，讓後續指令能精確引用。</summary>
    internal static class ToolText
    {
        public const string ErrorPrefix = "Error: ";

        public static string Error(string message)
        {
            return message.StartsWith(ErrorPrefix) ? message : ErrorPrefix + message;
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
    }
}
