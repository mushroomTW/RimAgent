using System;
using System.Collections.Generic;

namespace RimAgent.Agent;

internal static class DecisionCriteriaParser
{
    /// <summary>解析 noul 區段：「true:」「false:」行取代對應敘述，其餘非空行合併為說明。</summary>
    internal static void ParseNoulSection(List<string> lines, ref string instructions, ref string whenTrue, ref string whenFalse)
    {
        var instLines = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("true:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("true：", StringComparison.OrdinalIgnoreCase))
            {
                whenTrue = trimmed.Substring(5).Trim();
            }
            else if (trimmed.StartsWith("false:", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("false：", StringComparison.OrdinalIgnoreCase))
            {
                whenFalse = trimmed.Substring(6).Trim();
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                instLines.Add(trimmed);
            }
        }
        if (instLines.Count > 0)
        {
            instructions = string.Join('\n', instLines);
        }
    }
}
