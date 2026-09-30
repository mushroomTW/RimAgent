using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RimAgent.Agent
{
    /// <summary>
    /// 全域記憶的磁碟檔：跨所有存檔共用，放在 RimAgent 資料夾，玩家可直接檢視、備份或手動編輯。
    /// 讀檔一律不丟例外：讀不了的檔案改名保留，再從空白的全域記憶開始。
    /// </summary>
    public static class GlobalMemoryFile
    {
        public const string FileName = "global-memory.json";
        /// <summary>讀不了的檔案改存這個後綴，玩家還找得回來。</summary>
        public const string PreservedSuffix = ".invalid.txt";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>讀取全域記憶；<paramref name="warning"/> 非 null 代表檔案讀不了（原檔已改名保留）。</summary>
        public static AgentMemoryStore Load(string path, out string warning)
        {
            warning = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new AgentMemoryStore();
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

        /// <summary>寫回全域記憶；先寫暫存檔再換檔，中途失敗不會留下半個檔。</summary>
        public static void Save(string path, IReadOnlyList<AgentMemoryEntry> entries)
        {
            if (string.IsNullOrEmpty(path)) return;
            var document = new MemoryDocumentDto();
            foreach (AgentMemoryEntry entry in entries ?? Array.Empty<AgentMemoryEntry>())
            {
                if (entry == null || string.IsNullOrEmpty(entry.name) || entry.content == null) continue;
                document.Files.Add(new MemoryFileDto { Name = entry.name, Content = entry.content });
            }
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Options), Utf8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
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

        /// <summary>保留讀不了的檔案；連改名都失敗就只能放棄，不讓它擋住遊戲。</summary>
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

        #pragma warning disable S1144, S3459 // 由 System.Text.Json 反序列化賦值
        private sealed class MemoryDocumentDto
        {
            public List<MemoryFileDto> Files { get; set; } = new List<MemoryFileDto>();
        }

        private sealed class MemoryFileDto
        {
            public string Name { get; set; }
            public string Content { get; set; }
        }
        #pragma warning restore S1144, S3459
    }
}
