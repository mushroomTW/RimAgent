using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RimAgent.Tools;

namespace RimAgent.Agent
{
    /// <summary>單次指令可觀察到的執行過程；每個欄位與事件數都有上限。</summary>
    public sealed class RunLogRecord
    {
        private const int MaxFieldChars = 500;
        private const int MaxModelSteps = 16;
        private const int MaxEntries = 40;

        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime FinishedUtc { get; set; }
        public string Mode { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Instruction { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public List<RunLogModelStep> ModelSteps { get; set; } = new List<RunLogModelStep>();
        public List<RunLogTranscriptStep> TranscriptSteps { get; set; } = new List<RunLogTranscriptStep>();

        public void AddModelResponse(ChatResponse response)
        {
            if (response == null) return;
            if (ModelSteps.Count >= MaxModelSteps) ModelSteps.RemoveAt(0);
            ModelSteps.Add(new RunLogModelStep
            {
                Text = Limit(response.Text),
                ToolNames = response.Messages == null ? string.Empty : Limit(string.Join(", ",
                    response.Messages.Where(m => m?.Contents != null)
                        .SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.Name))),
                InputTokens = response.Usage?.InputTokenCount,
                OutputTokens = response.Usage?.OutputTokenCount
            });
        }

        public void CaptureTranscript(IReadOnlyList<TranscriptEntry> entries, int instructionId)
        {
            if (entries == null) return;
            foreach (TranscriptEntry entry in entries.Where(e => e.InstructionId == instructionId).Take(MaxEntries))
            {
                TranscriptSteps.Add(new RunLogTranscriptStep
                {
                    Kind = entry.Kind.ToString(),
                    Text = Limit(entry.Text),
                    Tool = Limit(entry.ToolName),
                    Arguments = Limit(entry.Arguments),
                    Result = Limit(entry.Result),
                    Risk = Limit(entry.RiskCheck),
                    Failed = entry.Failed
                });
            }
        }

        public static string Limit(string value)
        {
            return ToolText.TruncateField(value, MaxFieldChars);
        }
    }

    public sealed class RunLogModelStep
    {
        public string Text { get; set; } = string.Empty;
        public string ToolNames { get; set; } = string.Empty;
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
    }

    public sealed class RunLogTranscriptStep
    {
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Tool { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public string Risk { get; set; } = string.Empty;
        public bool Failed { get; set; }
    }

    /// <summary>
    /// 獨立 JSONL 日誌；最多 500 筆或 16 MiB。平常只在檔尾附加一行；
    /// 會超過上限時才整檔重寫，一次裁到 450 筆／九成大小，之後幾十筆又只需附加。
    /// </summary>
    public sealed class RunLogWriter
    {
        public const int MaxRuns = 500;
        /// <summary>超過上限時裁到的筆數：留一段餘裕，免得到頂後每筆都要重寫整個檔案。</summary>
        public const int TrimToRuns = 450;
        public const int MaxFileBytes = 16 * 1024 * 1024;
        private const long TrimToBytes = MaxFileBytes / 10 * 9;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly object _gate = new object();
        /// <summary>檔案目前的筆數；-1 代表還沒數過（第一次寫入時讀檔數一次）。</summary>
        private int _lineCount = -1;

        public RunLogWriter(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Log folder is required.", nameof(folder));
            FilePath = Path.Combine(folder, "RimAgent-runs.jsonl");
        }

        public string FilePath { get; }

        public void Append(RunLogRecord record)
        {
            if (record == null) return;
            string json = JsonSerializer.Serialize(record);
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                bool exists = File.Exists(FilePath);
                if (!exists) _lineCount = 0;
                else if (_lineCount < 0) _lineCount = File.ReadLines(FilePath, Utf8).Count();
                long size = exists ? new FileInfo(FilePath).Length : 0;
                string line = json + Environment.NewLine;
                if (_lineCount < MaxRuns && size + Utf8.GetByteCount(line) <= MaxFileBytes)
                {
                    File.AppendAllText(FilePath, line, Utf8);
                    _lineCount++;
                    return;
                }
                RewriteTrimmed(json);
            }
        }

        /// <summary>從最新往回保留，直到筆數或位元組碰到裁切目標。</summary>
        private void RewriteTrimmed(string newestJson)
        {
            List<string> lines = File.ReadAllLines(FilePath, Utf8).ToList();
            lines.Add(newestJson);
            int newlineBytes = Utf8.GetByteCount(Environment.NewLine);
            long bytes = 0;
            int keep = 0;
            for (int i = lines.Count - 1; i >= 0 && keep < TrimToRuns; i--)
            {
                long next = bytes + Utf8.GetByteCount(lines[i]) + newlineBytes;
                if (next > TrimToBytes) break;
                bytes = next;
                keep++;
            }
            File.WriteAllLines(FilePath, lines.GetRange(lines.Count - keep, keep), Utf8);
            _lineCount = keep;
        }
    }
}
