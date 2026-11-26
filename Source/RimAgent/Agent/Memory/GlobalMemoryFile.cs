#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimAgent.Agent;

/// <summary>全域記憶的磁碟檔：跨所有存檔共用，玩家可直接檢視、備份或手動編輯。讀檔一律不丟例外：讀不了的檔案改名保留，再從空白的全域記憶開始。</summary>
public static class GlobalMemoryFile
{
    public const string FileName = "global-memory.json";
    public const string PreservedSuffix = ".invalid.txt";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>讀取全域記憶；<paramref name="warning"/> 非 null 代表檔案讀不了（原檔已改名保留）。</summary>
    public static AgentMemoryStore Load(string path, out string warning)
    {
        warning = null;
        if (string.IsNullOrEmpty(path)) return new AgentMemoryStore();
        lock (Gate)
        {
            if (!File.Exists(path)) return new AgentMemoryStore();
            try
            {
                var document = JsonSerializer.Deserialize<MemoryDocumentDto>(File.ReadAllText(path, Utf8) ?? string.Empty, Options);
                return new AgentMemoryStore(ToEntries(document));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                string preserved = path + PreservedSuffix;
                warning = "global memory " + path + " could not be read (" + ex.Message + "); starting from an empty global memory and keeping the file as " + preserved;
                Preserve(path, preserved);
                return new AgentMemoryStore();
            }
        }
    }

    /// <summary>全域記憶寫入鎖；呼叫端需要讓「取快照＋寫檔」不可分割時也用這把（可重入）。</summary>
    internal static readonly System.Threading.Lock Gate = new();

    /// <summary>寫回全域記憶；先寫暫存檔再原子換檔，任何時間點中斷都至少留著完整的舊檔或新檔。</summary>
    public static void Save(string path, IReadOnlyList<AgentMemoryEntry> entries)
    {
        if (string.IsNullOrEmpty(path)) return;
        var document = new MemoryDocumentDto();
        foreach (AgentMemoryEntry entry in entries ?? [])
        {
            if (entry == null || string.IsNullOrEmpty(entry.name) || entry.content == null) continue;
            document.Files.Add(new MemoryFileDto { Name = entry.name, Content = entry.content });
        }
        string folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        // 序列化存檔：多條寫入同時進來時，共用暫存檔會互相覆蓋或搶不到檔案。
        lock (Gate)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(document, Options), Utf8);
                if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private static List<AgentMemoryEntry> ToEntries(MemoryDocumentDto document)
    {
        var entries = new List<AgentMemoryEntry>();
        if (document?.Files == null) return entries;
        foreach (MemoryFileDto file in document.Files)
        {
            if (file == null || string.IsNullOrEmpty(file.Name)) continue;
            entries.Add(new AgentMemoryEntry { name = file.Name, content = file.Content ?? string.Empty });
        }
        return entries;
    }

    private static void Preserve(string path, string preserved)
    {
        try
        {
            if (File.Exists(preserved)) File.Delete(preserved);
            File.Move(path, preserved);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 保留不成就放棄：原檔還在，提示訊息裡有它的路徑。
        }
    }

    private sealed class MemoryDocumentDto
    {
        public List<MemoryFileDto> Files { get; set; } = [];
    }

    private sealed class MemoryFileDto
    {
        public string Name { get; set; }
        public string Content { get; set; }
    }
}
