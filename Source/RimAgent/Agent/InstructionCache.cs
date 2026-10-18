using System;
using System.Collections.Generic;

namespace RimAgent.Agent;

/// <summary>
/// 同一句指令內的快取：分類器判定與查詢結果共用此形狀（字串鍵＋上限＋句首清空），不跨指令。
/// </summary>
internal sealed class InstructionCache<TValue>
{
    private const int MaxEntries = 128;

    private readonly Dictionary<string, TValue> _entries =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryGet(string key, out TValue value)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key ?? string.Empty, out value);
        }
    }

    public void Store(string key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries) return;
            _entries[key ?? string.Empty] = value;
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
