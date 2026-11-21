#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimAgent.Agent;
using RimAgent.Game;

namespace RimAgent.Tools;

internal static class ToolText
{
    public const string ErrorPrefix = "Error: ";

    /// <summary>「需確認」模式擋下命令時的前綴：是預覽不是失敗，模型不該重試，應改為向玩家摘要待辦。</summary>
    public const string ConfirmationPrefix = "Needs confirmation: ";

    public static string Error(string message)
    {
        return message.StartsWith(ErrorPrefix, StringComparison.Ordinal) ? message : ErrorPrefix + message;
    }

    public static string Confirmation(string preview)
    {
        return preview.StartsWith(ConfirmationPrefix, StringComparison.Ordinal) ? preview : ConfirmationPrefix + preview;
    }

    /// <summary>呼叫點應在所有參數解析之後、真正動世界之前。</summary>
    public static string Blocked(ConfirmationGate gate, string preview)
    {
        if (gate == null || gate.AllowCommands()) return null;
        string blocked = Confirmation(preview);
        gate.NoteBlocked(blocked);
        return blocked;
    }

    /// <summary>殖民者有段落因其他模組出錯而讀不到時的註記；讓模型知道那些欄位是空的而不是「沒有」。沒有則回傳空字串。</summary>
    public static string Unreadable(ColonistInfo c)
    {
        if (c.UnreadableParts.Count == 0) return string.Empty;
        return $" [could not read {string.Join(", ", c.UnreadableParts)}: another mod threw an error; do not treat them as empty]";
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

    public static string TruncateField(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length > max ? value[..max] + "…" : value;
    }

    public static string Thing(ThingInfo t)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(t.Id).Append("] ").Append(t.Label).Append(" (").Append(t.DefName).Append(") at ").Append(Cell(t.X, t.Z));
        if (t.StackCount > 1) sb.Append(" x").Append(t.StackCount);
        if (t.Kind == ThingKind.Plant && t.Harvestable) sb.Append(" harvestable");
        if (t.Kind == ThingKind.Building) sb.Append(t.PlayerOwned ? " player-owned" : " not player-owned");
        if (t.Forbidden) sb.Append(" forbidden");
        if (t.Designations.Count > 0) sb.Append(" [marked: ").AppendJoin(", ", t.Designations).Append(']');
        return sb.ToString();
    }

    public static string Colonist(ColonistInfo c)
    {
        return "[" + c.Id.ToString(CultureInfo.InvariantCulture) + "] " + c.Name;
    }

    public static string Names(IEnumerable<ColonistInfo> colonists)
    {
        return string.Join(", ", colonists.Select(c => c.Name));
    }

    public static string Skill(SkillInfo s)
    {
        if (s.Disabled) return s.Label + " (disabled)";
        string passion = string.Empty;
        if (s.Passion >= 2) passion = "**";
        else if (s.Passion == 1) passion = "*";
        return s.Label + " " + s.Level.ToString(CultureInfo.InvariantCulture) + passion;
    }
}
