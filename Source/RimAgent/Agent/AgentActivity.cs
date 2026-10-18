using System;
using System.Collections.Generic;

namespace RimAgent.Agent;

public enum AgentActivityKind
{
    /// <summary>模型正在產生下一步（等模型回覆）。</summary>
    Thinking,
    /// <summary>正在執行工具。</summary>
    Tool,
    /// <summary>安全分類器正在判斷命令風險。</summary>
    Checking,
    /// <summary>正在壓縮對話。</summary>
    Compacting
}

/// <summary>Agent 此刻實際在做的事，供視窗的執行中提示顯示；不可變。</summary>
public sealed class AgentActivity
{
    public AgentActivity(AgentActivityKind kind, string toolName)
    {
        Kind = kind;
        ToolName = toolName ?? string.Empty;
    }

    public AgentActivityKind Kind { get; }

    public string ToolName { get; }
}

/// <summary>
/// 進行中的活動清單，顯示最晚開始、仍未結束的那一個；全部結束時為 null（只顯示執行中）。
/// 每個活動各自結束，並行的子代理交錯開始與結束也不會互相蓋掉。
/// </summary>
public sealed class AgentActivityTracker
{
    private readonly object _gate = new();
    private readonly List<AgentActivity> _live = [];

    public AgentActivity Current
    {
        get
        {
            lock (_gate) return _live.Count == 0 ? null : _live[_live.Count - 1];
        }
    }

    /// <summary>開始一個活動；Dispose 結束它（要放在 using 或 finally）。</summary>
    public IDisposable Begin(AgentActivityKind kind, string toolName = null)
    {
        var activity = new AgentActivity(kind, toolName);
        lock (_gate) _live.Add(activity);
        return new Scope(this, activity);
    }

    private void End(AgentActivity activity)
    {
        lock (_gate) _live.Remove(activity);
    }

    private sealed class Scope : IDisposable
    {
        private AgentActivityTracker _owner;
        private readonly AgentActivity _activity;

        public Scope(AgentActivityTracker owner, AgentActivity activity)
        {
            _owner = owner;
            _activity = activity;
        }

        public void Dispose()
        {
            _owner?.End(_activity);
            _owner = null;
        }
    }
}
