using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace RimAgent.Game;

/// <summary>
/// 從存檔讀出 RimAgent 的殖民地代號與遊戲 tick，不載入整份存檔。
/// autosave 不分殖民地，只能開檔才知道是哪一局；串流讀到兩個欄位就停，結果依檔案與寫入時間快取。
/// 純檔案 IO，不碰 Verse：可在背景執行緒跑，也能在單元測試直接用。
/// </summary>
public static class SaveFileScanner
{
    public const string ColonyIdNode = "rimAgentColonyId";
    private const string TicksNode = "ticksGame";
    private const string TickManagerNode = "tickManager";

    private static readonly System.Threading.Lock Gate = new();
    private static readonly Dictionary<string, KeyValuePair<long, Scan>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public readonly struct Scan
    {
        public Scan(string? colonyId, int tick)
        {
            ColonyId = colonyId ?? string.Empty;
            Tick = tick;
        }

        public string ColonyId { get; }
        public int Tick { get; }
    }

    public static Scan Read(string path, long writtenUtcTicks)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out KeyValuePair<long, Scan> hit) && hit.Key == writtenUtcTicks) return hit.Value;
        }
        Scan scan;
        try
        {
            scan = ReadUncached(path);
        }
        catch (Exception ex) when (ex is IOException || ex is XmlException || ex is UnauthorizedAccessException)
        {
            // 可能只是存檔寫到一半：不快取失敗，下次列出時重讀。
            return new Scan(string.Empty, 0);
        }
        lock (Gate) Cache[path] = new KeyValuePair<long, Scan>(writtenUtcTicks, scan);
        return scan;
    }

    private static Scan ReadUncached(string path)
    {
        string? colonyId = null;
        int? tick = null;
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit };
        using XmlReader reader = XmlReader.Create(path, settings);
        // 只記父節點名稱：ticksGame 在別的物件裡也會出現，要認 tickManager 底下的那個。
        var parents = new List<string>();
        // ReadElementContentAsString 讀完已停在下一個節點，那一圈不能再 Read，否則會跳過它。
        bool more = reader.Read();
        while (more && (colonyId == null || tick == null))
        {
            more = ProcessXmlNode(reader, parents, ref colonyId, ref tick);
        }
        return new Scan(colonyId ?? string.Empty, tick ?? 0);
    }

    private static bool ProcessXmlNode(XmlReader reader, List<string> parents, ref string? colonyId, ref int? tick)
    {
        switch (reader.NodeType)
        {
            case XmlNodeType.Element:
                return ProcessXmlElement(reader, parents, ref colonyId, ref tick);
            case XmlNodeType.EndElement:
                if (parents.Count > 0) parents.RemoveAt(parents.Count - 1);
                break;
        }
        return reader.Read();
    }

    private static bool ProcessXmlElement(XmlReader reader, List<string> parents, ref string? colonyId, ref int? tick)
    {
        string name = reader.Name;
        if (colonyId == null && name == ColonyIdNode)
        {
            colonyId = reader.ReadElementContentAsString().Trim();
            return !reader.EOF;
        }
        string? parent = parents.Count > 0 ? parents[parents.Count - 1] : null;
        if (tick == null && name == TicksNode && parent == TickManagerNode)
        {
            tick = int.TryParse(reader.ReadElementContentAsString().Trim(), CultureInfo.InvariantCulture, out int value) ? value : 0;
            return !reader.EOF;
        }
        if (!reader.IsEmptyElement) parents.Add(name);
        return reader.Read();
    }
}
