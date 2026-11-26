#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;

namespace RimAgent.Agent;

#pragma warning disable MAAI001 // AF 1.28 將 AgentFileStore 標記為評估中；已鎖定套件版本並以回歸測試保護。
/// <summary>
/// agent 的一組記憶工具背後只有一個儲存，這裡把它分成兩個範圍：
/// 以 global_ 開頭的檔名屬全域記憶（跨存檔，改動後寫回磁碟），其餘屬局內記憶（跟著存檔序列化）。
/// 索引由兩邊的檔名與描述即時合成，所以換了存檔或別的存檔剛改過的全域記憶，下一次指令就看得到。
/// </summary>
public sealed class ScopedMemoryStore : AgentMemoryStore
{
    private readonly AgentMemoryStore _saveScope;
    private readonly AgentMemoryStore _globalScope;
    private readonly Action _persistGlobal;

    public ScopedMemoryStore(AgentMemoryStore saveScope, AgentMemoryStore globalScope, Action persistGlobal = null)
        : base((List<AgentMemoryEntry>)null)
    {
        _saveScope = saveScope ?? new AgentMemoryStore();
        _globalScope = globalScope ?? new AgentMemoryStore();
        _persistGlobal = persistGlobal;
    }

    public AgentMemoryStore SaveScope => _saveScope;

    public AgentMemoryStore GlobalScope => _globalScope;

    public override int DroppedOnLoad => _saveScope.DroppedOnLoad + _globalScope.DroppedOnLoad;

    /// <summary>存檔只留局內記憶；全域記憶另外寫在模組資料夾。</summary>
    public override List<AgentMemoryEntry> Snapshot()
    {
        return _saveScope.Snapshot();
    }

    /// <summary>兩個範圍合起來的索引（檔名與描述）；索引檔本身算局內，內容不影響這裡。</summary>
    public override string Index
    {
        get { return MemoryIndex.Compose(_saveScope.DescribedFiles().Concat(_globalScope.DescribedFiles())); }
    }

    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        AgentMemoryStore scope = ScopeOf(path, out bool global);
        await scope.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
        if (global) PersistGlobal();
    }

    public override Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        return ScopeOf(path, out _).ReadAsync(path, cancellationToken);
    }

    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        AgentMemoryStore scope = ScopeOf(path, out bool global);
        bool removed = await scope.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        if (global && removed) PersistGlobal();
        return removed;
    }

    /// <summary>列檔要合併兩個範圍：provider 重建索引時才看得到全域記憶。</summary>
    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FileStoreEntry> saveScope = await _saveScope.ListChildrenAsync(directory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FileStoreEntry> globalScope = await _globalScope.ListChildrenAsync(directory, cancellationToken).ConfigureAwait(false);
        if (globalScope.Count == 0) return saveScope;
        return [.. saveScope, .. globalScope];
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        return ScopeOf(path, out _).FileExistsAsync(path, cancellationToken);
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>檔名決定範圍；同一個檔名永遠落在同一個範圍，列檔與讀寫因此一致。</summary>
    private AgentMemoryStore ScopeOf(string path, out bool global)
    {
        global = MemoryScopes.IsGlobal(path);
        return global ? _globalScope : _saveScope;
    }

    private void PersistGlobal()
    {
        // 回寫動作內含「取快照＋寫檔」；整段持鎖，避免舊快照晚於新快照寫入而覆蓋掉新內容。
        lock (GlobalMemoryFile.Gate)
        {
            _persistGlobal?.Invoke();
        }
    }
}
#pragma warning restore MAAI001
