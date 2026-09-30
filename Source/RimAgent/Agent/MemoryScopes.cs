using System;
using System.Collections.Generic;
using System.Linq;

namespace RimAgent.Agent
{
    /// <summary>
    /// 記憶的兩個範圍。模型只看到一組 file_memory_* 工具，範圍由檔名決定：
    /// 以 <see cref="GlobalPrefix"/> 開頭者屬全域記憶（跨存檔，存在模組資料夾），其餘屬局內記憶（跟著單一存檔）。
    /// </summary>
    public static class MemoryScopes
    {
        /// <summary>全域記憶的檔名前綴。</summary>
        public const string GlobalPrefix = "global_";
        /// <summary>系統提示裡的索引筆數上限；與 FileMemoryProvider 自己重建的索引上限一致。</summary>
        public const int MaxIndexEntries = 50;

        /// <summary>這個檔名是否屬全域記憶。</summary>
        public static bool IsGlobal(string name)
        {
            return AgentMemoryStore.NormalizeName(name).StartsWith(GlobalPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 從存檔讀回的記憶裡挑出以 global_ 開頭者交還全域記憶（舊存檔可能如此命名），
        /// 讓「存檔只放局內記憶」的規則在讀檔後也成立。挑出的條目會從 <paramref name="entries"/> 移除。
        /// </summary>
        public static List<AgentMemoryEntry> TakeGlobalEntries(List<AgentMemoryEntry> entries)
        {
            var taken = new List<AgentMemoryEntry>();
            if (entries == null) return taken;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                AgentMemoryEntry entry = entries[i];
                if (entry == null || !IsGlobal(entry.name)) continue;
                taken.Add(entry);
                entries.RemoveAt(i);
            }
            taken.Reverse();
            return taken;
        }
    }

    /// <summary>索引的一筆：記憶檔名與它的描述（沒有描述時為空字串）。</summary>
    public sealed class MemoryFileInfo
    {
        public MemoryFileInfo(string name, string description)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public string Name { get; }
        public string Description { get; }
    }

    /// <summary>
    /// 把多個範圍的記憶檔組成一份索引；格式與 FileMemoryProvider 重建的 memories.md 相同，
    /// 依檔名排序並截到上限，所以同一組記憶每次組出來的字串都一樣（提示詞前綴快取不受影響）。
    /// </summary>
    public static class MemoryIndex
    {
        public static string Compose(IEnumerable<MemoryFileInfo> files, int maxEntries = MemoryScopes.MaxIndexEntries)
        {
            var lines = (files ?? Enumerable.Empty<MemoryFileInfo>())
                .Where(file => file != null && file.Name.Length > 0)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxEntries)
                .Select(file => file.Description.Length > 0
                    ? "- **" + file.Name + "**: " + file.Description
                    : "- **" + file.Name + "**");
            // 逐行組再 join：net472 沒有無參數的 TrimEnd（.NET Core 才追加），不比對尾端換行。
            return string.Join("\n", lines);
        }
    }
}
