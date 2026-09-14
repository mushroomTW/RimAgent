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
        /// <summary>
        /// 所屬的那一句指令（由 SendAsync 配號；0 代表不屬於任何指令，如舊存檔載入的紀錄）。
        /// 戰鬥延遲提醒以此為單位只顯示一次。
        /// </summary>
        public int InstructionId { get; set; }

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
        private int _instructionIds;

        /// <summary>
        /// 開啟新的一句指令並取號。工具在執行時讀 <see cref="CurrentInstructionId"/>
        /// 標記自己的紀錄，UI 再以此把「每句一次」的提醒去重。
        /// </summary>
        public int BeginInstruction()
        {
            lock (_gate)
            {
                _instructionIds++;
                return _instructionIds;
            }
        }

        public int CurrentInstructionId
        {
            get
            {
                lock (_gate)
                {
                    return _instructionIds;
                }
            }
        }

        /// <summary>
        /// 存檔載入後把編號推過已載入的最大值，避免新指令的編號撞到舊紀錄
        /// （戰鬥提醒以編號去重，撞號會讓新指令的提醒被吞掉）。
        /// </summary>
        public void EnsureInstructionIdAtLeast(int value)
        {
            lock (_gate)
            {
                if (value > _instructionIds) _instructionIds = value;
            }
        }

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
