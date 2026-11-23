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
/// 運行紀錄（runs/index.jsonl 與各句的事件檔）統計：工具成功率、常見失敗原因、依模型與版本比較。
/// 純字串邏輯，讀檔與顯示由呼叫端（開發模式按鈕）處理。只隨開發伴隨模組出貨；單元測試以連結方式編進 RimAgent.Tests。
/// 停止／取消造成的中斷不算工具失敗。
/// </summary>
internal static partial class RunLogStats
{

    /// <summary>把索引各行整理成統計報表；無法解析的行略過。</summary>
    /// <param name="index">索引檔的每一行。</param>
    /// <param name="readRun">依索引的 file 欄位讀該句事件檔的各行。</param>
    /// <param name="version">只算 ver 含此字串的紀錄；null 或空白表示全部。</param>
    /// <param name="top">各表最多列幾行。</param>
    /// <returns>報表文字；沒有符合的紀錄時回傳說明。</returns>
    public static string Format(IEnumerable<string> index, Func<string, IEnumerable<string>> readRun, string? version = null, int top = 15)
    {
        List<JsonElement> runs = [.. Load(index).Where(r => Str(r, "t") == "end" && (string.IsNullOrWhiteSpace(version) || Str(r, "ver").Contains(version, StringComparison.Ordinal)))];
        if (runs.Count == 0) return "No matching runs.";

        var sb = new StringBuilder();
        AppendOverview(sb, runs);
        AppendTools(sb, runs, readRun, top);
        AppendGroup(sb, "by model", runs, RunModels);
        AppendGroup(sb, "by mod version", runs, r => Str(r, "ver") is { Length: > 0 } v ? v : "(unknown)");
        return sb.ToString().TrimEnd();
    }

    private static List<JsonElement> Load(IEnumerable<string> lines)
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
            runs.Add(run);
        }
        return runs;
    }

    private static void AppendOverview(StringBuilder sb, List<JsonElement> runs)
    {
        sb.Append(runs.Count).Append(" runs, ").Append(Head(Str(runs[0], "ts"), 16))
          .Append(" ~ ").Append(Head(Str(runs[^1], "ts"), 16)).AppendLine();
        sb.Append("outcomes: ").AppendJoin(", ", runs
            .GroupBy(r => Str(r, "outcome"), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key + " " + g.Count().ToString(CultureInfo.InvariantCulture))).AppendLine();
        long calls = runs.Sum(r => Long(r, "calls"));
        long fails = runs.Sum(r => Long(r, "fail"));
        long cancels = runs.Sum(r => Long(r, "cancel"));
        sb.Append("tool calls ").Append(calls).Append(", failed ").Append(fails)
          .Append(" (").Append(Pct(fails, calls).Trim()).Append("), cancelled ").Append(cancels).AppendLine();
    }

    private static void AppendTools(StringBuilder sb, List<JsonElement> runs, Func<string, IEnumerable<string>> readRun, int top)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        var failed = new Dictionary<string, int>(StringComparer.Ordinal);
        var durations = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var reasons = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (JsonElement step in runs.SelectMany(r => ToolSteps(Str(r, "file"), readRun)))
        {
            if (Bool(step, "cancelled")) continue;
            string name = Str(step, "tool");
            Bump(total, name);
            long ms = Long(step, "ms");
            if (ms > 0) GetOrAdd(durations, name, () => []).Add(ms);
            if (Bool(step, "ok")) continue;
            Bump(failed, name);
            Bump(GetOrAdd(reasons, name, () => new Dictionary<string, int>(StringComparer.Ordinal)), NormalizeError(Str(step, "result")));
        }

        sb.AppendLine().AppendLine("== tools (cancellations excluded)");
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
            long calls = group.Sum(r => Long(r, "calls"));
            long fails = group.Sum(r => Long(r, "fail"));
            int count = group.Count();
            int finished = group.Count(r => Str(r, "outcome") is "finished" or "goal_reported_complete");
            long tokens = group.Sum(r => Long(r, "in")) / count;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{Head(group.Key, 31),-32}{count,5}{calls,7}{Pct(fails, calls),8}{Pct(finished, count),10}{tokens,12}");
        }
    }

    /// <summary>同一個 call 可能寫多行（子代理跑完才有結果），以最後一行為準。</summary>
    private static IEnumerable<JsonElement> ToolSteps(string file, Func<string, IEnumerable<string>> readRun)
    {
        if (file.Length == 0) return [];
        return Load(readRun(file)).Where(e => Str(e, "t") == "tool").GroupBy(e => Long(e, "call")).Select(g => g.Last());
    }

    private static string RunModels(JsonElement run)
    {
        string models = string.Join(", ", Array(run, "models").Select(m => m.GetString()));
        return models.Length > 0 ? models : "(unknown)";
    }

    /// <summary>失敗原因只取第一行、數字換成 N，同類錯誤才能歸在一起。</summary>
    internal static string NormalizeError(string text)
    {
        string first = (text ?? string.Empty).Split('\n')[0].TrimEnd('\r');
        return Head(Digits.Replace(first, "N"), 110);
    }

    private static string Pct(long part, long whole)
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

    [GeneratedRegex(@"\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Digits { get; }
}
