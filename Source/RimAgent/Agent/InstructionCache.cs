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
            key ??= string.Empty;
            // 已滿時仍允許更新既有鍵，只拒絕新增。
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key)) return;
            _entries[key] = value;
        }
    }

    /// <summary>
    /// 在同一把鎖內讀取舊值並寫入新值（原子的讀‧改‧寫）；快取已滿且為新鍵時不寫入。
    /// </summary>
    public void AddOrUpdate(string key, Func<bool, TValue, TValue> update)
    {
        lock (_gate)
        {
            key ??= string.Empty;
            bool exists = _entries.TryGetValue(key, out TValue previous);
            if (!exists && _entries.Count >= MaxEntries) return;
            _entries[key] = update(exists, previous);
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
