#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.AI;
using RimAgent.Tools;

namespace RimAgent.Agent;

public sealed class RunLogHeader
{
    public int InstructionId { get; init; }
    public string Mode { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Instruction { get; init; } = string.Empty;
    public AutonomousRunInfo Autonomous { get; init; }
}

/// <summary>指令文字會被截斷（共用通則在前），觸發原因要在這裡另記，才能事後比對重試與施工進度。</summary>
public sealed class AutonomousRunInfo
{
    public string Trigger { get; set; } = string.Empty;
    public int? Attempt { get; set; }
    public int GameTick { get; set; }
    public AutonomousSignals? Signals { get; set; }
}

/// <summary>一句指令的紀錄檔：一行一個事件，發生當下就寫出，遊戲當掉也留得住已發生的部分。</summary>
public sealed class RunLog
{
    private const int MaxFieldChars = 2000;

    private readonly Lock _gate = new();
    private readonly RunLogWriter _owner;
    private readonly string _file;
    private readonly DateTime _startedUtc;
    private readonly Dictionary<TranscriptEntry, int> _calls = [];
    private readonly Dictionary<string, int> _lastStepOf = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _models = new(StringComparer.Ordinal);
    private StreamWriter _stream;
    private string _lastModelText;
    private string _reasoning;
    private int _steps;
    private long _inputTokens;
    private long _outputTokens;

    internal RunLog(RunLogWriter owner, string file, DateTime startedUtc, FileStream stream, RunLogHeader header)
    {
        _owner = owner;
        _file = file;
        _startedUtc = startedUtc;
        _stream = new StreamWriter(stream, RunLogWriter.Utf8) { AutoFlush = true };
        Header = header;
        Write(new
        {
            t = "run",
            ts = startedUtc,
            tick = owner.Tick(),
            src = header.Source,
            mode = header.Mode,
            ver = owner.ModVersion,
            text = Limit(header.Instruction),
            autonomous = header.Autonomous
        });
    }

    public RunLogHeader Header { get; }

    /// <summary>這句實際用的思考強度；<paramref name="route"/> 是自動分流的結果，固定強度時為 null。</summary>
    public void AddReasoning(ReasoningLevel level, DecisionRoute route)
    {
        lock (_gate)
        {
            if (_stream == null) return;
            _reasoning = level.ToString();
            Write(new
            {
                t = "reasoning",
                at = Elapsed(),
                level = _reasoning,
                auto = route != null,
                fallback = route?.IsFallback,
                confidence = route?.Confidence,
                reason = NullIfEmpty(Limit(route?.Reason))
            });
        }
    }

    public void AddModelResponse(string agent, ChatResponse response)
    {
        if (response == null) return;
        lock (_gate)
        {
            if (_stream == null) return;
            _steps++;
            _lastStepOf[agent ?? string.Empty] = _steps;
            _lastModelText = response.Text?.Trim();
            if (!string.IsNullOrEmpty(response.ModelId)) _models.Add(response.ModelId);
            _inputTokens += response.Usage?.InputTokenCount ?? 0;
            _outputTokens += response.Usage?.OutputTokenCount ?? 0;
            string[] calls = [.. (response.Messages ?? []).Where(m => m?.Contents != null)
                .SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.Name)];
            Write(new
            {
                t = "model",
                at = Elapsed(),
                n = _steps,
                agent = NullIfEmpty(agent),
                model = NullIfEmpty(response.ModelId),
                @in = response.Usage?.InputTokenCount,
                @out = response.Usage?.OutputTokenCount,
                calls = calls.Length > 0 ? calls : null,
                text = NullIfEmpty(Limit(response.Text))
            });
        }
    }

    public void AddEntry(TranscriptEntry entry)
    {
        lock (_gate)
        {
            if (_stream == null || entry.InstructionId != Header.InstructionId) return;
            switch (entry.Kind)
            {
                // 推理模型每步都有一則，量大且模型事件已有該步的輸出。
                case TranscriptKind.Reasoning:
                // 指令本身在 run 事件、最終回覆在最後一個 model 事件，不重複寫。
                case TranscriptKind.User when !entry.Interjection:
                case TranscriptKind.Assistant when string.Equals(entry.Text, _lastModelText, StringComparison.Ordinal):
                    return;
                case TranscriptKind.Tool:
                    AddTool(entry);
                    return;
                default:
                    Write(new
                    {
                        t = "msg",
                        at = Elapsed(),
                        kind = entry.Interjection ? "interjection" : entry.Kind.ToString().ToLowerInvariant(),
                        error = entry.Kind == TranscriptKind.Error ? entry.ErrorKind.ToString() : null,
                        text = Limit(entry.Text)
                    });
                    return;
            }
        }
    }

    /// <summary>工具項目事後改了結果（子代理跑完、玩家確認後執行）：同一個 call 再寫一行，以最後一行為準。</summary>
    public void UpdateTool(TranscriptEntry entry)
    {
        lock (_gate)
        {
            if (_stream == null || entry.InstructionId != Header.InstructionId) return;
            AddTool(entry);
        }
    }

    private void AddTool(TranscriptEntry entry)
    {
        if (!_calls.TryGetValue(entry, out int call))
        {
            call = _calls.Count + 1;
            _calls[entry] = call;
        }
        Write(new
        {
            t = "tool",
            at = Elapsed(),
            tick = _owner.Tick(),
            n = _lastStepOf.TryGetValue(entry.Agent ?? string.Empty, out int step) ? step : (int?)null,
            call,
            agent = NullIfEmpty(entry.Agent),
            tool = entry.ToolName,
            args = Arguments(entry),
            ok = !entry.Failed,
            cancelled = entry.Cancelled ? true : (bool?)null,
            ms = entry.DurationMs,
            risk = NullIfEmpty(entry.RiskCheck),
            riskSrc = NullIfEmpty(entry.RiskCheckSource),
            result = Limit(entry.Result)
        });
    }

    /// <summary>寫結尾並收進日期資料夾；重複呼叫無作用。</summary>
    public void End(string outcome)
    {
        string summary;
        lock (_gate)
        {
            if (_stream == null) return;
            summary = Serialize(new
            {
                t = "end",
                ts = _startedUtc,
                file = _file,
                tick = _owner.Tick(),
                src = Header.Source,
                mode = Header.Mode,
                trigger = Header.Autonomous?.Trigger,
                attempt = Header.Autonomous?.Attempt,
                ver = _owner.ModVersion,
                text = ToolText.TruncateField(Header.Instruction, RunLogWriter.MaxIndexTextChars),
                outcome,
                secs = Elapsed(),
                models = _models,
                reasoning = _reasoning,
                steps = _steps,
                @in = _inputTokens,
                @out = _outputTokens,
                calls = _calls.Count,
                fail = _calls.Keys.Count(e => e.Failed && !e.Cancelled),
                cancel = _calls.Keys.Count(e => e.Cancelled)
            });
            _stream.WriteLine(summary);
            _stream.Dispose();
            _stream = null;
        }
        _owner.Archive(_file, summary);
    }

    /// <summary>寫入失敗後放掉檔案；留在 active 的部分由下次啟動收尾。</summary>
    internal void Abort()
    {
        lock (_gate)
        {
            // 不 Dispose StreamWriter：它會再試著寫出緩衝而重複丟例外。
            _stream?.BaseStream.Dispose();
            _stream = null;
        }
    }

    public static string Limit(string value) => ToolText.TruncateField(value, MaxFieldChars);

    private void Write(object value) => _stream.WriteLine(Serialize(value));

    private static string Serialize(object value) => JsonSerializer.Serialize(value, RunLogWriter.Json);

    private double Elapsed() => Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1);

    private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static object Arguments(TranscriptEntry entry)
    {
        if (entry.ArgumentValues is { Count: > 0 } values)
        {
            try
            {
                JsonElement json = JsonSerializer.SerializeToElement(values, RunLogWriter.Json);
                if (json.GetRawText().Length <= MaxFieldChars) return json;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                // NaN 等存不成 JSON 的值：退回文字，不能因此丟掉整句紀錄。
            }
        }
        return NullIfEmpty(Limit(entry.Arguments));
    }
}

/// <summary>
/// 執行紀錄資料夾：<c>index.jsonl</c> 每句一行摘要，<c>日期/時間-來源.jsonl</c> 每句一檔，
/// 進行中的在 <c>active/</c>；啟動時還留在 active 的就是上次當機的那幾句。
/// </summary>
public sealed class RunLogWriter
{
    public const int DefaultMaxRuns = 50_000;
    public const string FolderName = "runs";
    public const string IndexName = "index.jsonl";
    internal const int MaxIndexTextChars = 120;
    private const string EndPrefix = "{\"t\":\"end\"";
    private const string ActiveName = "active";
    private const char DaySeparator = '/';
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    /// <summary>預設編碼器會把中文轉成 \uXXXX，搜不到也費 token；紀錄不會嵌進 HTML，可用寬鬆編碼。</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lock _gate = new();
    private readonly string _activeFolder;
    /// <summary>索引目前的執行筆數與 note 筆數；-1 代表還沒數過。note 另計，才不會擠掉執行紀錄。</summary>
    private int _runs = -1;
    private int _notes;
    private bool _recovered;

    /// <summary>遊戲存檔資料夾下的 RimAgent 資料夾；運行紀錄、benchmark 結果與統計報表都放這裡。</summary>
    public static string DefaultFolder => Path.Combine(Verse.GenFilePaths.SaveDataFolderPath, "RimAgent");

    public RunLogWriter(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Log folder is required.", nameof(folder));
        Folder = Path.GetFullPath(Path.Combine(folder, FolderName));
        _activeFolder = Path.Combine(Folder, ActiveName);
        IndexPath = Path.Combine(Folder, IndexName);
    }

    public string Folder { get; }
    public string IndexPath { get; }
    public string ModVersion { get; init; } = string.Empty;
    public Func<int?> GameTick { get; init; }
    public int MaxRuns { get; init; } = DefaultMaxRuns;

    internal int? Tick()
    {
        try
        {
            return GameTick?.Invoke();
        }
        catch (Exception)
        {
            // 背景執行緒收尾時遊戲可能正在拆局；缺 tick 好過丟掉整句紀錄。
            return null;
        }
    }

    public RunLog Start(RunLogHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        lock (_gate)
        {
            Directory.CreateDirectory(_activeFolder);
            if (!_recovered)
            {
                _recovered = true;
                Recover();
            }
            DateTime started = DateTime.UtcNow;
            string file = FileFor(started, header.Source);
            while (File.Exists(ActivePath(file)) || File.Exists(ArchivedPath(file)))
            {
                started = started.AddMilliseconds(1);
                file = FileFor(started, header.Source);
            }
            var stream = new FileStream(ActivePath(file), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            return new RunLog(this, file, started, stream, header);
        }
    }

    /// <summary>沒有過程可記的一筆（System One 判斷、捨棄確認）：只進索引。</summary>
    public void Note(RunLogHeader header, string outcome)
    {
        ArgumentNullException.ThrowIfNull(header);
        string json = JsonSerializer.Serialize(new
        {
            t = "note",
            ts = DateTime.UtcNow,
            tick = Tick(),
            src = header.Source,
            mode = header.Mode,
            ver = ModVersion,
            text = RunLog.Limit(header.Instruction),
            outcome = RunLog.Limit(outcome)
        }, Json);
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            AppendIndex(json);
        }
    }

    internal void Archive(string file, string summary)
    {
        lock (_gate)
        {
            ArchiveLocked(file, summary);
        }
    }

    private void ArchiveLocked(string file, string summary)
    {
        string active = ActivePath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivedPath(file)));
        // 先搬再記索引，記不進去就搬回來：兩步任一失敗都留在 active，下次啟動收尾時補記，不會遺漏或重複。
        File.Move(active, ArchivedPath(file));
        try
        {
            AppendIndex(summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(ArchivedPath(file), active);
            throw;
        }
    }

    private void Recover()
    {
        foreach (string path in Directory.GetFiles(_activeFolder, "*.jsonl"))
        {
            try
            {
                // 另一個遊戲程序正在寫的檔案打不開，略過。
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
                // 來源名稱可能含底線（system_one），只換日期後的第一個。
                string name = Path.GetFileName(path);
                int split = name.IndexOf('_');
                if (split <= 0) continue;
                string file = name[..split] + DaySeparator + name[(split + 1)..];
                ArchiveLocked(file, Summarize(path, file));
            }
            catch (Exception)
            {
                // 一個收不了不影響其他的，下次啟動再試。
            }
        }
    }

    /// <summary>正常結束時最後一行就是索引內容；當機的沒有結尾，由已寫出的事件補出同樣的欄位。</summary>
    private static string Summarize(string path, string file)
    {
        string first = null, last = null, reasoning = null;
        var tools = new Dictionary<int, JsonObject>();
        var models = new SortedSet<string>(StringComparer.Ordinal);
        int steps = 0;
        long input = 0, output = 0;
        double secs = 0;
        foreach (string line in File.ReadLines(path, Utf8))
        {
            first ??= line;
            last = line;
            JsonObject e = Parse(line);
            try
            {
                secs = (double?)e["at"] ?? secs;
                switch ((string)e["t"])
                {
                    case "tool":
                        tools[(int)e["call"]] = e;
                        break;
                    case "reasoning":
                        reasoning = (string)e["level"];
                        break;
                    case "model":
                        steps++;
                        input += (long?)e["in"] ?? 0;
                        output += (long?)e["out"] ?? 0;
                        if ((string)e["model"] is { } model) models.Add(model);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or NullReferenceException)
            {
                // 欄位缺漏或型別不符的行：略過，其餘照算。
            }
        }
        if (last != null && last.StartsWith(EndPrefix, StringComparison.Ordinal) && Parse(last).Count > 0) return last;
        JsonObject run = Parse(first);
        int cancelled = tools.Values.Count(IsCancelled);
        return JsonSerializer.Serialize(new
        {
            t = "end",
            ts = run["ts"],
            file,
            tick = run["tick"],
            src = run["src"],
            mode = run["mode"],
            trigger = run["autonomous"]?["trigger"],
            attempt = run["autonomous"]?["attempt"],
            ver = run["ver"],
            text = ToolText.TruncateField((string)run["text"], MaxIndexTextChars),
            outcome = "crashed",
            secs,
            models,
            reasoning,
            steps,
            @in = input,
            @out = output,
            calls = tools.Count,
            fail = tools.Values.Count(t => (bool?)t["ok"] == false && !IsCancelled(t)),
            cancel = cancelled
        }, Json);
    }

    private static bool IsCancelled(JsonObject tool) => (bool?)tool["cancelled"] == true;

    private static JsonObject Parse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? [] : JsonNode.Parse(line) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // 當機時寫到一半的行。
            return [];
        }
    }

    private void AppendIndex(string json)
    {
        if (_runs < 0)
        {
            string[] existing = File.Exists(IndexPath) ? File.ReadAllLines(IndexPath, Utf8) : [];
            _notes = existing.Count(l => !IsRun(l));
            _runs = existing.Length - _notes;
        }
        AppendLine(json);
        if (IsRun(json)) _runs++;
        else _notes++;
        if (_runs <= MaxRuns && _notes <= MaxRuns) return;
        try
        {
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 索引正被別的程式讀：這筆已記入，下一筆再裁。
        }
    }

    private static bool IsRun(string line) => line.StartsWith(EndPrefix, StringComparison.Ordinal);

    private void AppendLine(string json)
    {
        using var stream = new FileStream(IndexPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
        long start = stream.Length;
        if (start > 0)
        {
            stream.Position = start - 1;
            // 上次寫到一半的殘行：補換行隔開，這筆才不會跟著壞掉。
            if (stream.ReadByte() != '\n') json = "\n" + json;
        }
        try
        {
            stream.Write(Utf8.GetBytes(json + "\n"));
        }
        catch (IOException)
        {
            stream.SetLength(start);
            throw;
        }
    }

    /// <summary>一次多裁 1%，免得到頂後每筆都要重寫索引；量小才不會讓遊戲明顯停頓。</summary>
    private void Trim()
    {
        string[] lines = File.ReadAllLines(IndexPath, Utf8);
        int notes = lines.Count(l => !IsRun(l));
        int runs = lines.Length - notes;
        int dropRuns = DropCount(runs), dropNotes = DropCount(notes);
        var kept = new List<string>(lines.Length);
        var dropped = new List<string>();
        foreach (string line in lines)
        {
            bool drop = IsRun(line) ? dropRuns-- > 0 : dropNotes-- > 0;
            (drop ? dropped : kept).Add(line);
        }
        // 先寫暫存檔再換檔：寫到一半失敗或當機時，原檔仍完整。
        string temp = IndexPath + ".tmp";
        File.WriteAllLines(temp, kept, Utf8);
        File.Move(temp, IndexPath, overwrite: true);
        _notes = kept.Count(l => !IsRun(l));
        _runs = kept.Count - _notes;
        string[] files = [.. dropped.Select(l => Parse(l)["file"]?.ToString()).Where(f => !string.IsNullOrEmpty(f))
            .Select(ArchivedPath)
            // 索引可能被手改過，只刪紀錄資料夾底下的檔案。
            .Where(p => p.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
        foreach (string path in files) TryDelete(() => File.Delete(path));
        foreach (string day in files.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 還有檔案的資料夾刪不掉，正好略過。
            TryDelete(() => Directory.Delete(day));
        }
    }

    private int DropCount(int count) => count <= MaxRuns ? 0 : count - MaxRuns + MaxRuns / 100;

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 刪不掉只是多佔空間。
        }
    }

    private static string FileFor(DateTime utc, string source) =>
        utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + DaySeparator +
        utc.ToString("HHmmssfff", CultureInfo.InvariantCulture) + "-" + source + ".jsonl";

    private string ActivePath(string file) => Path.Combine(_activeFolder, file.Replace(DaySeparator, '_'));

    private string ArchivedPath(string file) => Path.GetFullPath(Path.Combine(Folder, file));
}
