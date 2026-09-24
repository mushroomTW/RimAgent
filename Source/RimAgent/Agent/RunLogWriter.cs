using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

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
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length > MaxFieldChars ? value.Substring(0, MaxFieldChars) + "…" : value;
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

    /// <summary>獨立 JSONL 日誌；最多 500 筆或 16 MiB，先達上限就移除最舊紀錄。</summary>
    public sealed class RunLogWriter
    {
        public const int MaxRuns = 500;
        public const int MaxFileBytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public RunLogWriter(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Log folder is required.", nameof(folder));
            FilePath = Path.Combine(folder, "RimAgent-runs.jsonl");
        }

        public string FilePath { get; }

        public void Append(RunLogRecord record)
        {
            if (record == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath, Utf8).ToList() : new List<string>();
            lines.Add(JsonSerializer.Serialize(record));
            int newlineBytes = Utf8.GetByteCount(Environment.NewLine);
            while (lines.Count > MaxRuns || lines.Sum(line => (long)Utf8.GetByteCount(line) + newlineBytes) > MaxFileBytes)
            {
                lines.RemoveAt(0);
            }
            File.WriteAllLines(FilePath, lines, Utf8);
        }
    }
}
