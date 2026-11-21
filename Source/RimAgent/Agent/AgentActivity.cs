#nullable disable

using System;
using System.Collections.Generic;

namespace RimAgent.Agent;

public enum AgentActivityKind
{
    Thinking,
    Tool,
    Checking,
    Compacting
}

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

/// <summary>進行中的活動清單；並行的子代理交錯開始與結束時，各活動各自結束、不互相蓋掉。</summary>
public sealed class AgentActivityTracker
{
    private readonly System.Threading.Lock _gate = new();
    private readonly List<AgentActivity> _live = [];

    public AgentActivity Current
    {
        get
        {
            lock (_gate) return _live.Count == 0 ? null : _live[_live.Count - 1];
        }
    }

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
