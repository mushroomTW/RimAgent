using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Verse;

namespace RimAgent.Agent;

#pragma warning disable MAAI001
/// <summary>
/// FileMemoryProvider 的儲存層：只有平面檔名（provider 的工作資料夾固定為空字串），
/// 並在這裡限制檔數與長度。一個實例就是一個範圍的記憶——
/// 局內記憶跟著存檔走，全域記憶由 <see cref="ScopedMemoryStore"/> 另外寫進磁碟。
/// </summary>
public class AgentMemoryStore : AgentFileStore
{
    public const int MaxFiles = 30;
    public const int MaxFileChars = 4000;
    public const int MaxNameChars = 60;
    /// <summary>描述會逐行列進系統提示的索引，所以壓成單行並截短。</summary>
    public const int MaxDescriptionChars = 200;
    /// <summary>FileMemoryProvider 自動維護的索引檔。</summary>
    public const string IndexFileName = "memories.md";
    private const string DescriptionSuffix = "_description.md";

    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    // 工具在執行緒池上寫入，存檔在主執行緒讀取。
    private readonly object _gate = new();
    private readonly int _droppedOnLoad;

    public AgentMemoryStore(List<AgentMemoryEntry> entries = null)
    {
        if (entries == null) return;
        var dropped = new List<string>();
        foreach (AgentMemoryEntry entry in entries)
        {
            ProcessEntryOnLoad(entry, dropped);
        }
        _droppedOnLoad = dropped.Count;
        // 索引由 provider 在下次寫入時重建；在那之前先拿掉被丟棄檔案的列，免得模型去讀不存在的檔。
        PruneDroppedFilesFromIndex(dropped);
    }

    private void ProcessEntryOnLoad(AgentMemoryEntry entry, List<string> dropped)
    {
        if (entry == null || entry.content == null) return;
        string name = NormalizeName(entry.name);
        if (name.Length == 0 || _files.ContainsKey(name)) return;
        if (IsUserFile(name) && (InvalidUserFile(name, entry.content) != null || UserFileCount() >= MaxFiles))
        {
            dropped.Add(name);
            return;
        }
        _files[name] = IsDescription(name) ? OneLine(entry.content) : entry.content;
    }

    private void PruneDroppedFilesFromIndex(List<string> dropped)
    {
        if (dropped.Count == 0 || !_files.TryGetValue(IndexFileName, out string index)) return;

        _files[IndexFileName] = string.Join("\n", index.Split('\n')
            .Where(line => !dropped.Any(name => line.IndexOf("**" + name + "**", StringComparison.OrdinalIgnoreCase) >= 0)));
    }

    /// <summary>讀檔時因超出限制而捨棄的記憶檔數（合成兩個範圍時為兩者相加）。</summary>
    public virtual int DroppedOnLoad => _droppedOnLoad;

    public virtual List<AgentMemoryEntry> Snapshot()
    {
        lock (_gate) return [.. _files.Select(f => new AgentMemoryEntry { name = f.Key, content = f.Value })];
    }

    /// <summary>目前的記憶索引（檔名與描述）；沒有記憶時為空字串。</summary>
    public virtual string Index
    {
        get
        {
            lock (_gate) return _files.TryGetValue(IndexFileName, out string index) ? index : string.Empty;
        }
    }

    /// <summary>使用者記憶檔（不含索引與描述檔）的名稱與描述，依檔名排序；供合成兩個範圍的索引。</summary>
    public IReadOnlyList<MemoryFileInfo> DescribedFiles()
    {
        lock (_gate)
        {
            return _files.Keys.Where(IsUserFile)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new MemoryFileInfo(name, DescriptionOf(name)))
                .ToList();
        }
    }

    /// <summary>描述檔的檔名規則與 FileMemoryProvider 一致：去掉最後一段副檔名再加上後綴。</summary>
    private static string DescriptionFileName(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) + DescriptionSuffix : name + DescriptionSuffix;
    }

    private string DescriptionOf(string name)
    {
        return _files.TryGetValue(DescriptionFileName(name), out string description) ? description : string.Empty;
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        string name = NormalizeName(path);
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
        string line = string.Join(" ", (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > MaxDescriptionChars ? line.Substring(0, MaxDescriptionChars) + "..." : line;
    }

    public override Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_files.TryGetValue(NormalizeName(path), out string content) ? content : null);
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_files.Remove(NormalizeName(path)));
    }

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FileStoreEntry> entries;
        lock (_gate)
        {
            entries = NormalizeName(directory).Length > 0
                ? new FileStoreEntry[0]
                : [.. _files.Keys.Select(name => new FileStoreEntry(name, "file"))];
        }
        return Task.FromResult(entries);
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate) return Task.FromResult(_files.ContainsKey(NormalizeName(path)));
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

    /// <summary>檔名正規化：扁平命名空間，反斜線一律視為斜線。</summary>
    internal static string NormalizeName(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
    }
}
#pragma warning restore MAAI001

public sealed class AgentMemoryEntry : IExposable
{
    #pragma warning disable S1104 // Required for RimWorld Scribe serialization via ref parameter
    public string name;
    public string content;
    #pragma warning restore S1104

    public void ExposeData()
    {
        Scribe_Values.Look(ref name, "name");
        Scribe_Values.Look(ref content, "content");
    }
}
