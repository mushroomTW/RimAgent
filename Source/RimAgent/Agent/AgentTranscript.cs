using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RimAgent.Tools;

namespace RimAgent.Agent;

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
    /// <summary>User 專用：這句是排程器起的自主任務，不是玩家打的。</summary>
    public bool Autonomous { get; set; }
    /// <summary>User 專用：這句由獨立 goal 執行器續跑。</summary>
    public bool GoalRun { get; set; }
    /// <summary>User 專用：玩家在 agent 執行中插的話，屬於進行中的指令，不另起回合。</summary>
    public bool Interjection { get; set; }

    // Tool 專用
    public string ToolName { get; set; } = string.Empty;
    public ToolCategory ToolCategory { get; set; }
    public ToolRisk ToolRisk { get; set; }
    public string Arguments { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public bool Failed { get; set; }
    /// <summary>本次呼叫是否實際扣除自主模式的每日工具額度。</summary>
    public bool BudgetConsumed { get; set; }
    /// <summary>自動模式的分類結果：low／high／unavailable；沒經分類器為空。</summary>
    public string RiskCheck { get; set; } = string.Empty;
    /// <summary>自動模式風險判定來源：llm／decision／fallback；沒經分類器為空。</summary>
    public string RiskCheckSource { get; set; } = string.Empty;
    /// <summary>替哪個子代理執行；主 Agent 自己的呼叫為空。</summary>
    public string Agent { get; set; } = string.Empty;

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
    /// <summary>執行期保留上限；超過就整批丟掉最舊的，讓 UI 全量重建的次數維持稀疏（存檔另有 200 則上限）。</summary>
    public const int MaxEntries = 600;
    /// <summary>裁切後保留的則數。</summary>
    public const int TrimTo = 500;

    private readonly object _gate = new();
    private readonly List<TranscriptEntry> _entries = [];
    private int _instructionIds;
    private int _version;
    private int _epoch;
    // 子代理與主 Agent 共用同一組工具實例，靠呼叫流程上的 AsyncLocal 分辨是誰呼叫的；並行的子代理各有一份。
    private readonly AsyncLocal<string> _agent = new();

    /// <summary>每次 Add／Clear／既有紀錄更新時遞增；UI 以此判斷是否要更新渲染快取。</summary>
    public int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>在 Clear、裁掉舊紀錄或更新既有紀錄時遞增；同一 Epoch 內清單只會在尾端增加。</summary>
    public int Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

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

    /// <summary>
    /// 目前這句指令的原文（最後一則非插話的 User 紀錄），後面依序接上執行中的插話；沒有則為空字串。
    /// 分類器要看玩家原話：只看插話會把原指令要求的命令誤判成未要求。
    /// </summary>
    public string CurrentInstructionText
    {
        get
        {
            lock (_gate)
            {
                var parts = new List<string>();
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    TranscriptEntry entry = _entries[i];
                    if (entry.Kind != TranscriptKind.User) continue;
                    parts.Add(entry.Text);
                    if (!entry.Interjection) break;
                }
                parts.Reverse();
                return string.Join("\n", parts);
            }
        }
    }

    /// <summary>目前這條呼叫流程所屬的子代理名稱；主 Agent 為空字串。工具紀錄以此標記。</summary>
    public string CurrentAgent => _agent.Value ?? string.Empty;

    /// <summary>設定目前呼叫流程的子代理並回傳原值；呼叫端要在 finally 以原值還原。</summary>
    internal string SwapCurrentAgent(string name)
    {
        string previous = _agent.Value;
        _agent.Value = name;
        return previous;
    }

    public void Add(TranscriptEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - TrimTo);
                _epoch++;
            }
            _version++;
        }
    }

    /// <summary>
    /// 原子更新已加入清單的工具紀錄。既有項目內容改變時遞增 Epoch，要求 UI 全量重建，
    /// 避免確認執行後仍顯示舊的預覽結果。
    /// </summary>
    public void UpdateToolResult(TranscriptEntry entry, string result, bool failed)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        lock (_gate)
        {
            entry.Result = result ?? string.Empty;
            entry.Failed = failed;
            if (_entries.Contains(entry))
            {
                _version++;
                _epoch++;
            }
        }
    }

    public IReadOnlyList<TranscriptEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>這句指令以哪種錯誤結束；沒出錯回傳 null（自主任務遇到設定類錯誤時據此暫停）。</summary>
    public AgentErrorKind? ErrorKindOf(int instructionId)
    {
        lock (_gate)
        {
            TranscriptEntry error = _entries.LastOrDefault(e => e.Kind == TranscriptKind.Error && e.InstructionId == instructionId);
            return error?.ErrorKind;
        }
    }

    /// <summary>一次上鎖取得快照與對應的 Version／Epoch：分開讀會讓中間插進來的裁切把 Epoch 與清單對不上。</summary>
    public IReadOnlyList<TranscriptEntry> Snapshot(out int version, out int epoch)
    {
        lock (_gate)
        {
            version = _version;
            epoch = _epoch;
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
            _version++;
            _epoch++;
        }
    }
}
