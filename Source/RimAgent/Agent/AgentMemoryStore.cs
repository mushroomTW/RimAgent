using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Verse;

namespace RimAgent.Agent
{
#pragma warning disable MAAI001
    /// <summary>
    /// FileMemoryProvider 的儲存層：記憶檔跟著存檔走，讀回舊存檔時記憶一併回溯。
    /// 只有平面檔名（provider 的工作資料夾固定為空字串），並在這裡限制檔數與長度。
    /// </summary>
    public sealed class AgentMemoryStore : AgentFileStore
    {
        public const int MaxFiles = 30;
        public const int MaxFileChars = 4000;
        public const int MaxNameChars = 60;
        /// <summary>描述會逐行列進系統提示的索引，所以壓成單行並截短。</summary>
        public const int MaxDescriptionChars = 200;
        /// <summary>FileMemoryProvider 自動維護的索引檔。</summary>
        public const string IndexFileName = "memories.md";
        private const string DescriptionSuffix = "_description.md";

        private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 工具在執行緒池上寫入，存檔在主執行緒讀取。
        private readonly object _gate = new object();

        public AgentMemoryStore(List<AgentMemoryEntry> entries = null)
        {
            if (entries == null) return;
            var dropped = new List<string>();
            foreach (AgentMemoryEntry entry in entries)
            {
                if (entry == null || entry.content == null) continue;
                string name = Normalize(entry.name);
                if (name.Length == 0 || _files.ContainsKey(name)) continue;
                if (IsUserFile(name) && (InvalidUserFile(name, entry.content) != null || UserFileCount() >= MaxFiles))
                {
                    dropped.Add(name);
                    continue;
                }
                _files[name] = IsDescription(name) ? OneLine(entry.content) : entry.content;
            }
            DroppedOnLoad = dropped.Count;
            // 索引由 provider 在下次寫入時重建；在那之前先拿掉被丟棄檔案的列，免得模型去讀不存在的檔。
            if (dropped.Count > 0 && _files.TryGetValue(IndexFileName, out string index))
            {
                // Split(char) 只存在於 Mono；net472 要傳字元陣列。
                _files[IndexFileName] = string.Join("\n", index.Split(new[] { '\n' })
                    .Where(line => !dropped.Any(name => line.IndexOf("**" + name + "**", StringComparison.OrdinalIgnoreCase) >= 0)));
            }
        }

        /// <summary>讀檔時因超出限制而捨棄的記憶檔數。</summary>
        public int DroppedOnLoad { get; }

        public List<AgentMemoryEntry> Snapshot()
        {
            lock (_gate) return _files.Select(f => new AgentMemoryEntry { name = f.Key, content = f.Value }).ToList();
        }

        /// <summary>目前的記憶索引（檔名與描述）；沒有記憶時為空字串。</summary>
        public string Index
        {
            get
            {
                lock (_gate) return _files.TryGetValue(IndexFileName, out string index) ? index : string.Empty;
            }
        }

        public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            string name = Normalize(path);
            content = content ?? string.Empty;
            // provider 先寫內容、再寫描述、最後重建索引：只在內容這一步拒絕，
            // 描述一律截短而不丟例外，避免內容已存入卻沒進索引。
            if (IsDescription(name)) content = OneLine(content);
            lock (_gate)
            {
                if (IsUserFile(name))
                {
                    string invalid = InvalidUserFile(name, content);
                    if (invalid != null) throw new InvalidOperationException(invalid);
                    if (!_files.ContainsKey(name) && UserFileCount() >= MaxFiles)
                        throw new InvalidOperationException($"Memory is full ({MaxFiles} files). Merge or delete a file first.");
                }
                _files[name] = content;
            }
            return Task.CompletedTask;
        }

        private static string InvalidUserFile(string name, string content)
        {
            if (name.Length > MaxNameChars || name.Any(char.IsControl))
                return $"Memory file names must be 1-{MaxNameChars} characters without line breaks.";
            if (content.Length > MaxFileChars)
                return $"Memory file '{name}' exceeds {MaxFileChars} characters. Keep memories short or split them.";
            return null;
        }

        private static string OneLine(string text)
        {
            string line = string.Join(" ", (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return line.Length > MaxDescriptionChars ? line.Substring(0, MaxDescriptionChars) + "..." : line;
        }

        public override Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_files.TryGetValue(Normalize(path), out string content) ? content : null);
        }

        public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_files.Remove(Normalize(path)));
        }

        public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<FileStoreEntry> entries;
            lock (_gate)
            {
                entries = Normalize(directory).Length > 0
                    ? new FileStoreEntry[0]
                    : _files.Keys.Select(name => new FileStoreEntry(name, "file")).ToArray();
            }
            return Task.FromResult(entries);
        }

        public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_files.ContainsKey(Normalize(path)));
        }

        public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        private int UserFileCount()
        {
            return _files.Keys.Count(IsUserFile);
        }

        private static bool IsIndex(string name)
        {
            return string.Equals(name, IndexFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDescription(string name)
        {
            return name.EndsWith(DescriptionSuffix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUserFile(string name)
        {
            return !IsIndex(name) && !IsDescription(name);
        }

        private static string Normalize(string path)
        {
            // Trim(char) 只存在於 Mono；net472 只有 Trim(params char[])。
            return (path ?? string.Empty).Replace('\\', '/').Trim().Trim(new[] { '/' });
        }
    }
#pragma warning restore MAAI001

    public sealed class AgentMemoryEntry : IExposable
    {
        public string name;
        public string content;

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref content, "content");
        }
    }
}
