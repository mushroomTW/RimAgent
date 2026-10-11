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
        /// <summary>自主任務的起跑脈絡；玩家指令與目標為 null。</summary>
        public AutonomousRunInfo Autonomous { get; set; }
        public List<RunLogModelStep> ModelSteps { get; set; } = new List<RunLogModelStep>();
        public List<RunLogTranscriptStep> TranscriptSteps { get; set; } = new List<RunLogTranscriptStep>();
        /// <summary>超過上限而丟掉的最早幾步；兩個清單都只留最後的部分，彼此對得上、也看得到結尾。</summary>
        public int OmittedModelSteps { get; set; }
        public int OmittedTranscriptSteps { get; set; }

        public void AddModelResponse(ChatResponse response)
        {
            if (response == null) return;
            if (ModelSteps.Count >= MaxModelSteps)
            {
                ModelSteps.RemoveAt(0);
                OmittedModelSteps++;
            }
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
            List<TranscriptEntry> own = entries.Where(e => e.InstructionId == instructionId).ToList();
            int skip = Math.Max(0, own.Count - MaxEntries);
            OmittedTranscriptSteps += skip;
            foreach (TranscriptEntry entry in own.Skip(skip))
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

    /// <summary>
    /// 自主任務起跑時的觸發、嘗試次數、遊戲 tick 與排程器看到的訊號。
    /// 指令文字會被截斷（共用通則在前），觸發原因要在這裡另記，才能事後比對重試與施工進度。
    /// </summary>
    public sealed class AutonomousRunInfo
    {
        public string Trigger { get; set; } = string.Empty;
        /// <summary>該條件的第幾次嘗試（含首次）；定期巡視與抉擇不走重試表，為 null。</summary>
        public int? Attempt { get; set; }
        public int GameTick { get; set; }
        /// <summary>觸發當下排程器評估用的訊號；抉擇不經排程器評估，為 null。</summary>
        public AutonomousSignals? Signals { get; set; }
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
