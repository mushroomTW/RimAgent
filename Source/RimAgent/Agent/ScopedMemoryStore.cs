using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;

namespace RimAgent.Agent
{
#pragma warning disable MAAI001
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

        /// <param name="saveScope">局內記憶的儲存；null 則視為空的局內記憶。</param>
        /// <param name="globalScope">全域記憶的儲存；null 則視為空的全域記憶。</param>
        /// <param name="persistGlobal">全域記憶改動後的回寫動作；null 則只留在記憶體。</param>
        public ScopedMemoryStore(AgentMemoryStore saveScope, AgentMemoryStore globalScope, Action persistGlobal = null)
            : base((List<AgentMemoryEntry>)null)
        {
            _saveScope = saveScope ?? new AgentMemoryStore();
            _globalScope = globalScope ?? new AgentMemoryStore();
            _persistGlobal = persistGlobal;
        }

        /// <summary>局內記憶的儲存（存檔序列化的來源）。</summary>
        public AgentMemoryStore SaveScope => _saveScope;

        /// <summary>全域記憶的儲存。</summary>
        public AgentMemoryStore GlobalScope => _globalScope;

        /// <summary>兩個範圍各自在讀檔時捨棄的檔數。</summary>
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
            return saveScope.Concat(globalScope).ToList();
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
            _persistGlobal?.Invoke();
        }
    }
#pragma warning restore MAAI001
}
