#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimAgent.InGameTests;

/// <summary>
/// 運行紀錄（RimAgent-runs.jsonl）統計：工具成功率、常見失敗原因、依模型與版本比較。
/// 純字串邏輯，讀檔與顯示由呼叫端（開發模式按鈕）處理。只隨開發伴隨模組出貨；單元測試以連結方式編進 RimAgent.Tests。
/// 停止／取消造成的中斷不算工具失敗（舊紀錄沒有 Cancelled 欄位時，以例外名稱判斷）。
/// 每筆紀錄的 TranscriptSteps 只保留最後幾十步，各工具明細只涵蓋保留下來的步驟；
/// 新版紀錄的 ToolCalls/ToolFailures/ToolCancellations 才是整句的完整計數。
/// </summary>
internal static class RunLogStats
{
    private static readonly Regex CancelPattern = new(@"^(TaskCanceledException|OperationCanceledException)\b", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Digits = new(@"\d+", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>把 jsonl 各行整理成統計報表；無法解析的行略過。</summary>
    /// <param name="lines">紀錄檔的每一行。</param>
    /// <param name="version">只算 ModVersion 含此字串的紀錄；null 或空白表示全部。</param>
    /// <param name="top">各表最多列幾行。</param>
    /// <returns>報表文字；沒有符合的紀錄時回傳說明。</returns>
    public static string Format(IEnumerable<string> lines, string? version = null, int top = 15)
    {
        List<JsonElement> runs = Load(lines, version);
        if (runs.Count == 0) return "No matching runs.";

        var sb = new StringBuilder();
        AppendOverview(sb, runs);
        AppendTools(sb, runs, top);
        AppendGroup(sb, "by model", runs, RunModels);
        AppendGroup(sb, "by mod version", runs, r => Str(r, "ModVersion") is { Length: > 0 } v ? v : "(unknown)");
        return sb.ToString().TrimEnd();
    }

    private static List<JsonElement> Load(IEnumerable<string> lines, string? version)
    {
        var runs = new List<JsonElement>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement run;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                run = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // 寫到一半被截斷的行（遊戲當掉時）不影響其他紀錄。
                continue;
            }
            if (!string.IsNullOrWhiteSpace(version) && !Str(run, "ModVersion").Contains(version, StringComparison.Ordinal)) continue;
            runs.Add(run);
        }
        return runs;
    }

    private static void AppendOverview(StringBuilder sb, List<JsonElement> runs)
    {
        sb.Append(runs.Count).Append(" runs, ").Append(Head(Str(runs[0], "StartedUtc"), 16))
          .Append(" ~ ").Append(Head(Str(runs[^1], "StartedUtc"), 16)).AppendLine();
        sb.Append("outcomes: ").AppendLine(string.Join(", ", runs
            .GroupBy(r => Str(r, "Outcome"), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key + " " + g.Count())));
        int calls = 0, fails = 0, cancels = 0;
        foreach (JsonElement run in runs)
        {
            (int c, int f, int x) = RunCounts(run);
            calls += c;
            fails += f;
            cancels += x;
        }
        sb.Append("tool calls ").Append(calls).Append(", failed ").Append(fails)
          .Append(" (").Append(Pct(fails, calls).Trim()).Append("), cancelled ").Append(cancels).AppendLine();
    }

    private static void AppendTools(StringBuilder sb, List<JsonElement> runs, int top)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        var failed = new Dictionary<string, int>(StringComparer.Ordinal);
        var durations = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (JsonElement step in runs.SelectMany(ToolSteps))
        {
            if (IsCancelled(step)) continue;
            string name = Str(step, "Tool");
            Bump(total, name);
            long ms = Long(step, "DurationMs");
            if (ms > 0) GetOrAdd(durations, name, () => []).Add(ms);
            if (!Bool(step, "Failed")) continue;
            Bump(failed, name);
            Bump(GetOrAdd(reasons, name, () => new Dictionary<string, int>(StringComparer.Ordinal)), NormalizeError(Str(step, "Result")));
        }

        sb.AppendLine().AppendLine("== tools (kept steps only, cancellations excluded)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{"tool",-28}{"calls",6}{"fail",6}{"fail%",8}{"avg ms",8}");
        foreach ((string name, int n) in Top(total, top))
        {
            int f = failed.GetValueOrDefault(name);
            string avg = durations.TryGetValue(name, out List<long>? ms) ? (ms.Sum() / ms.Count).ToString(CultureInfo.InvariantCulture) : "-";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{name,-28}{n,6}{f,6}{Pct(f, n),8}{avg,8}");
        }

        sb.AppendLine().AppendLine("== top failure reasons");
        foreach ((string name, int n) in Top(failed, top))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{name} ({n}/{total[name]})");
            foreach ((string reason, int count) in Top(reasons[name], 3))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {count,3}  {reason}");
            }
        }
    }

    private static void AppendGroup(StringBuilder sb, string title, List<JsonElement> runs, Func<JsonElement, string> key)
    {
        sb.AppendLine().Append("== ").AppendLine(title);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{"",-32}{"runs",5}{"calls",7}{"fail%",8}{"finished",10}{"avg in tok",12}");
        foreach (IGrouping<string, JsonElement> group in runs.GroupBy(key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int calls = 0, fails = 0;
            foreach (JsonElement run in group)
            {
                (int c, int f, _) = RunCounts(run);
                calls += c;
                fails += f;
            }
            int count = group.Count();
            int finished = group.Count(r => Str(r, "Outcome") is "finished" or "goal_reported_complete");
            long tokens = group.Sum(r => Array(r, "ModelSteps").Sum(m => Long(m, "InputTokens"))) / count;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{Head(group.Key, 31),-32}{count,5}{calls,7}{Pct(fails, calls),8}{Pct(finished, count),10}{tokens,12}");
        }
    }

    /// <summary>整句的工具呼叫、失敗、取消數；舊紀錄沒有計數欄位時改由保留的步驟推算。</summary>
    private static (int Calls, int Failed, int Cancelled) RunCounts(JsonElement run)
    {
        if (run.TryGetProperty("ToolCalls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Number)
        {
            return (calls.GetInt32(), (int)Long(run, "ToolFailures"), (int)Long(run, "ToolCancellations"));
        }
        List<JsonElement> steps = [.. ToolSteps(run)];
        int cancelled = steps.Count(IsCancelled);
        int failed = steps.Count(s => Bool(s, "Failed") && !IsCancelled(s));
        return (steps.Count, failed, cancelled);
    }

    private static IEnumerable<JsonElement> ToolSteps(JsonElement run)
    {
        return Array(run, "TranscriptSteps").Where(s => Str(s, "Tool").Length > 0);
    }

    private static bool IsCancelled(JsonElement step)
    {
        return Bool(step, "Cancelled") || CancelPattern.IsMatch(Str(step, "Result"));
    }

    private static string RunModels(JsonElement run)
    {
        string models = string.Join(", ", Array(run, "ModelSteps").Select(m => Str(m, "Model")).Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal));
        return models.Length > 0 ? models : "(unknown)";
    }

    /// <summary>失敗原因只取第一行、數字換成 N，同類錯誤才能歸在一起。</summary>
    internal static string NormalizeError(string text)
    {
        string first = (text ?? string.Empty).Split('\n')[0].TrimEnd('\r');
        return Head(Digits.Replace(first, "N"), 110);
    }

    private static string Pct(int part, int whole)
    {
        return whole == 0 ? "-" : (part * 100.0 / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    private static IEnumerable<(string Key, int Count)> Top(Dictionary<string, int> counts, int top)
    {
        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(top).Select(p => (p.Key, p.Value));
    }

    private static TValue GetOrAdd<TValue>(Dictionary<string, TValue> map, string key, Func<TValue> create)
    {
        if (!map.TryGetValue(key, out TValue? value))
        {
            value = create();
            map[key] = value;
        }
        return value;
    }

    private static void Bump(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private static string Head(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Str(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    }

    private static long Long(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
    }

    private static bool Bool(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
}
