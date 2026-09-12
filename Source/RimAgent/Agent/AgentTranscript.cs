using System;
using System.Collections.Generic;
using RimAgent.Tools;

namespace RimAgent.Agent
{
    public enum TranscriptKind
    {
        User,
        Assistant,
        Tool,
        Error,
        Notice
    }

    /// <summary>玩家看不到底層 LLM 例外，只需要知道該去哪裡處理；UI 依此挑翻譯字串。</summary>
    public enum AgentErrorKind
    {
        Cancelled,
        /// <summary>金鑰、額度、供應商離線——要去 RimLLM Framework 設定頁處理。</summary>
        Configuration,
        /// <summary>逾時、限流、網路——稍後再試。</summary>
        Transient,
        ContextTooLong,
        Unknown
    }

    public sealed class TranscriptEntry
    {
        public TranscriptKind Kind { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        public string Text { get; set; } = string.Empty;

        // Tool 專用
        public string ToolName { get; set; } = string.Empty;
        public ToolCategory ToolCategory { get; set; }
        public string Arguments { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
        public bool Failed { get; set; }

        // Assistant / Error 專用
        public bool ToolsStripped { get; set; }
        public AgentErrorKind ErrorKind { get; set; }
    }

    /// <summary>
    /// 對話與行動紀錄的單一有序清單，UI 每幀讀取。
    /// 工具在主線程執行、LLM 回覆的續行可能在任何執行緒，因此以鎖保護，UI 讀快照。
    /// </summary>
    public sealed class AgentTranscript
    {
        private readonly object _gate = new object();
        private readonly List<TranscriptEntry> _entries = new List<TranscriptEntry>();

        public void Add(TranscriptEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        public IReadOnlyList<TranscriptEntry> Snapshot()
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }
        }
    }
}
