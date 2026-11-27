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
    Notice,
    /// <summary>模型這一步的思考內容（推理模型回傳的 reasoning）；存檔以整數記，只能加在最後。</summary>
    Reasoning
}

/// <summary>玩家看不到底層 LLM 例外，只需要知道該去哪裡處理；UI 依此挑翻譯字串。</summary>
public enum AgentErrorKind
{
    Cancelled,
    Configuration,
    Transient,
    ContextTooLong,
    Unknown,
    /// <summary>供應商 429 配額限流；分不出每分鐘或每日，自主任務短暫停頓後再試。</summary>
    RateLimited
}

public sealed class TranscriptEntry
{
    public TranscriptKind Kind { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public string Text { get; set; } = string.Empty;
    /// <summary>所屬的那一句指令（由 SendAsync 配號；0 代表不屬於任何指令，如舊存檔載入的紀錄）。</summary>
    public int InstructionId { get; set; }
    /// <summary>User 專用：這句是排程器起的自主任務，不是玩家打的。</summary>
    public bool Autonomous { get; set; }
    /// <summary>User 專用：這句由獨立 goal 執行器續跑。</summary>
    public bool GoalRun { get; set; }
    /// <summary>User 專用：玩家在 agent 執行中插的話，屬於進行中的指令，不另起回合。</summary>
    public bool Interjection { get; set; }

    public string ToolName { get; set; } = string.Empty;
    public ToolCategory ToolCategory { get; set; }
    public ToolRisk ToolRisk { get; set; }
    public string Arguments { get; set; } = string.Empty;
    /// <summary>原始引數，執行紀錄才能存成結構化 JSON；不存檔。</summary>
    public IDictionary<string, object?>? ArgumentValues { get; set; }
    public string Result { get; set; } = string.Empty;
    public bool Failed { get; set; }
    /// <summary>因停止或取消而中斷（也算 <see cref="Failed"/>），統計成功率時應與工具本身的失敗分開。</summary>
    public bool Cancelled { get; set; }
    /// <summary>工具執行的實際耗時（毫秒），含分類器檢查；未實際執行（前置檢查擋下）為 0。</summary>
    public long DurationMs { get; set; }
    public bool BudgetConsumed { get; set; }
    /// <summary>自動模式的分類結果：low／high／unavailable；沒經分類器為空。</summary>
    public string RiskCheck { get; set; } = string.Empty;
    /// <summary>自動模式風險判定來源：llm／decision／fallback；沒經分類器為空。</summary>
    public string RiskCheckSource { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;

    public bool ToolsStripped { get; set; }
    public AgentErrorKind ErrorKind { get; set; }
}

/// <summary>工具在主線程執行、LLM 回覆的續行可能在任何執行緒，因此以鎖保護，UI 讀快照。</summary>
public sealed class AgentTranscript
{
    /// <summary>執行期保留上限；超過就整批丟掉最舊的，讓 UI 全量重建的次數維持稀疏（存檔另有 200 則上限）。</summary>
    public const int MaxEntries = 600;
    public const int TrimTo = 500;

    private readonly Lock _gate = new();
    private readonly List<TranscriptEntry> _entries = [];
    private int _instructionIds;
    private int _version;
    private int _epoch;
    // 子代理與主 Agent 共用同一組工具實例，靠呼叫流程上的 AsyncLocal 分辨是誰呼叫的；並行的子代理各有一份。
    private readonly AsyncLocal<string?> _agent = new();

    /// <summary>新增或更新項目後呼叫（第二個參數為 true 代表更新既有項目）；在鎖外呼叫，可能來自任何執行緒。</summary>
    public Action<TranscriptEntry, bool>? Observer { get; set; }

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

    /// <summary>存檔載入後把編號推過已載入的最大值；戰鬥提醒以編號去重，撞號會讓新指令的提醒被吞掉。</summary>
    public void EnsureInstructionIdAtLeast(int value)
    {
        lock (_gate)
        {
            if (value > _instructionIds) _instructionIds = value;
        }
    }

    /// <summary>目前這句指令的原文（最後一則非插話的 User 紀錄）加上其後的插話；分類器要看玩家原話，只看插話會把原指令要求的命令誤判成未要求。</summary>
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
                return string.Join('\n', parts);
            }
        }
    }

    public string CurrentAgent => _agent.Value ?? string.Empty;

    /// <summary>設定目前呼叫流程的子代理並回傳原值；呼叫端要在 finally 以原值還原。</summary>
    internal string? SwapCurrentAgent(string? name)
    {
        string? previous = _agent.Value;
        _agent.Value = name;
        return previous;
    }

    public void Add(TranscriptEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
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
        Observer?.Invoke(entry, false);
    }

    /// <summary>既有項目內容改變時遞增 Epoch，要求 UI 全量重建，避免確認執行後仍顯示舊的預覽結果。</summary>
    public void UpdateToolResult(TranscriptEntry entry, string result, bool failed)
    {
        ArgumentNullException.ThrowIfNull(entry);
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
        Observer?.Invoke(entry, true);
    }

    /// <summary>這句指令以哪種錯誤結束；沒出錯回傳 null（自主任務遇到設定類錯誤時據此暫停）。</summary>
    public AgentErrorKind? ErrorKindOf(int instructionId)
    {
        lock (_gate)
        {
            TranscriptEntry? error = _entries.LastOrDefault(e => e.Kind == TranscriptKind.Error && e.InstructionId == instructionId);
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
            return [.. _entries];
        }
    }

    public IReadOnlyList<TranscriptEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
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
