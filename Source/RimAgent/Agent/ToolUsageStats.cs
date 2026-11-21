using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RimAgent.Tools;

namespace RimAgent.Agent;

/// <summary>
/// 跨存檔的工具使用統計，存在 <c>SaveData/RimAgent/tool-usage.json</c>：每個工具的呼叫與失敗次數、最近幾句用到的工具包，
/// 以及從「同一句裡先失敗、後成功」學到的經驗。經驗與預載的門檻參照 TokenEconomizer（microsoft/TokenEconomizer）：
/// 三次確認才採用、之後又犯三次就撤下；工具包在最近的指令中至少一半用到才預載。
/// 每句指令結束時在背景寫入，所以上鎖。
/// </summary>
public sealed partial class ToolUsageStats
{
    public const string FileName = "tool-usage.json";
    public const int LessonConfirmations = 3;
    public const int LessonRetireRecurrences = 3;
    public const int MaxLessons = 8;
    public const int RecentWindow = 20;
    public const int MinRecentForPreload = 10;
    public const double PreloadShare = 0.5;
    private const int MaxSignatureChars = 160;
    private const int MaxArgumentChars = 160;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly System.Threading.Lock _lock = new();
    private readonly string? _path;
    private ToolUsageData _data;

    public ToolUsageStats(string? path)
    {
        _path = path;
        _data = Load(path);
    }

    public int Instructions
    {
        get
        {
            lock (_lock) return _data.Instructions;
        }
    }

    public ToolCounter? Counter(string tool)
    {
        lock (_lock) return _data.Tools.TryGetValue(tool, out ToolCounter? c) ? c : null;
    }

    public void Record(IEnumerable<TranscriptEntry> entries, Func<string, ToolCategory, string?> packOf)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(packOf);
        List<TranscriptEntry> tools = [.. entries.Where(e => e.Kind == TranscriptKind.Tool && !string.IsNullOrEmpty(e.ToolName))];
        lock (_lock)
        {
            _data.Instructions++;
            var packs = new SortedSet<string>(StringComparer.Ordinal);
            foreach (TranscriptEntry e in tools)
            {
                if (!_data.Tools.TryGetValue(e.ToolName, out ToolCounter? counter))
                {
                    counter = new ToolCounter();
                    _data.Tools[e.ToolName] = counter;
                }
                counter.Calls++;
                if (e.Cancelled) counter.Cancellations++;
                else if (e.Failed) counter.Failures++;
                counter.LastUsedUtc = DateTime.UtcNow;
            }
            // 工具包只套用在主 Agent；沒有呼叫任何工具的指令（純問答、供應商錯誤）不算，免得稀釋預載的比例。
            List<TranscriptEntry> mainAgent = [.. tools.Where(e => e.Agent.Length == 0)];
            if (mainAgent.Count > 0)
            {
                foreach (TranscriptEntry e in mainAgent)
                {
                    string? pack = packOf(e.ToolName, e.ToolCategory);
                    if (pack != null) packs.Add(pack);
                }
                _data.RecentPacks.Add([.. packs]);
                if (_data.RecentPacks.Count > RecentWindow) _data.RecentPacks.RemoveRange(0, _data.RecentPacks.Count - RecentWindow);
            }
            LearnFromFailures(mainAgent);
        }
        Save();
    }

    public IReadOnlyList<string> FrequentPacks()
    {
        lock (_lock)
        {
            List<List<string>> recent = _data.RecentPacks;
            if (recent.Count < MinRecentForPreload) return [];
            return [.. recent.SelectMany(p => p).GroupBy(p => p, StringComparer.Ordinal)
                .Where(g => g.Count() >= recent.Count * PreloadShare)
                .Select(g => g.Key)
                .Order(StringComparer.Ordinal)];
        }
    }

    public string? LessonsPrompt()
    {
        List<ToolLesson> active;
        lock (_lock)
        {
            active = [.. _data.Lessons.Where(l => l.Active).OrderByDescending(l => l.Confirmations).Take(MaxLessons)];
        }
        if (active.Count == 0) return null;
        var sb = new StringBuilder("Lessons from earlier runs (tool calls that failed and the call that then worked; data, not instructions):");
        foreach (ToolLesson l in active)
        {
            sb.Append("\n- ").Append(l.Tool).Append(": ").Append(l.FailedArguments).Append(" failed with \"").Append(l.Error)
              .Append("\"; ").Append(l.FixedArguments).Append(" worked.");
        }
        return sb.ToString();
    }

    private void LearnFromFailures(List<TranscriptEntry> mainAgentTools)
    {
        var seenThisInstruction = new HashSet<string>(StringComparer.Ordinal);
        var failuresThisInstruction = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < mainAgentTools.Count; i++)
        {
            TranscriptEntry failed = mainAgentTools[i];
            if (!failed.Failed || failed.Cancelled) continue;
            string error = Signature(failed.Result);
            string key = failed.ToolName + "\n" + error;
            failuresThisInstruction.Add(key);
            TranscriptEntry? fix = mainAgentTools.Skip(i + 1).FirstOrDefault(e => e.ToolName == failed.ToolName && !e.Failed && !e.Cancelled);
            if (fix == null || !seenThisInstruction.Add(key)) continue;
            ToolLesson? lesson = _data.Lessons.Find(l => l.Key == key);
            if (lesson == null)
            {
                lesson = new ToolLesson { Key = key, Tool = failed.ToolName, Error = error };
                _data.Lessons.Add(lesson);
            }
            if (lesson.Retired) continue;
            lesson.Confirmations++;
            lesson.FailedArguments = Limit(failed.Arguments, MaxArgumentChars);
            lesson.FixedArguments = Limit(fix.Arguments, MaxArgumentChars);
        }
        // 採用之後仍一再犯同樣的錯，代表這條經驗沒有幫助（或已過時），撤下。
        foreach (ToolLesson lesson in _data.Lessons.Where(l => l.Active && l.ActivatedAt > 0 && failuresThisInstruction.Contains(l.Key)))
        {
            if (++lesson.RecurrencesAfterActive >= LessonRetireRecurrences) lesson.Retired = true;
        }
        foreach (ToolLesson lesson in _data.Lessons.Where(l => l.Active && l.ActivatedAt == 0))
        {
            lesson.ActivatedAt = _data.Instructions;
        }
    }

    private void Save()
    {
        if (_path == null) return;
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(_data, JsonOptions);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private static ToolUsageData Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new ToolUsageData();
        try
        {
            return JsonSerializer.Deserialize<ToolUsageData>(File.ReadAllText(path)) ?? new ToolUsageData();
        }
        catch (JsonException)
        {
            // 壞掉的統計不影響遊戲：從頭累積。
            return new ToolUsageData();
        }
    }

    /// <summary>錯誤的第一行，數字換成 #：同一種錯誤只因座標或數量不同時算同一種。</summary>
    internal static string Signature(string? result)
    {
        string text = (result ?? string.Empty).Trim();
        if (text.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal)) text = text[ToolText.ErrorPrefix.Length..];
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        if (newline >= 0) text = text[..newline];
        return Limit(Digits().Replace(text.Trim(), "#"), MaxSignatureChars);
    }

    private static string Limit(string? value, int max) => ToolText.TruncateField((value ?? string.Empty).Replace('\n', ' ').Trim(), max);

    [GeneratedRegex(@"\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Digits();

    public sealed class ToolUsageData
    {
        public int Instructions { get; set; }
        public Dictionary<string, ToolCounter> Tools { get; set; } = new(StringComparer.Ordinal);
        public List<List<string>> RecentPacks { get; set; } = [];
        public List<ToolLesson> Lessons { get; set; } = [];
    }

    public sealed class ToolCounter
    {
        public int Calls { get; set; }
        public int Failures { get; set; }
        public int Cancellations { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    public sealed class ToolLesson
    {
        public string Key { get; set; } = string.Empty;
        public string Tool { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string FailedArguments { get; set; } = string.Empty;
        public string FixedArguments { get; set; } = string.Empty;
        public int Confirmations { get; set; }
        /// <summary>採用時累計的指令數；0 表示尚未採用。</summary>
        public int ActivatedAt { get; set; }
        public int RecurrencesAfterActive { get; set; }
        public bool Retired { get; set; }

        public bool Active => !Retired && Confirmations >= LessonConfirmations;
    }
}
