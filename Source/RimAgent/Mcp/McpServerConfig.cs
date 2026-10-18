using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RimAgent.Mcp;

/// <summary>servers.json 裡的一個 server 條目。</summary>
public sealed class McpServerEntry
{
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public List<string> Args { get; set; } = [];
    public bool Enabled { get; set; }
    public int TimeoutSeconds { get; set; } = McpServerConfig.DefaultTimeoutSeconds;
}

/// <summary>
/// 讀 AgentPack/MCP/servers.json。缺檔、壞檔一律回報原因而不丟例外，
/// 缺名或缺命令的條目直接跳過。
/// </summary>
public static class McpServerConfig
{
    public const int DefaultTimeoutSeconds = 30;

    private static readonly JsonSerializerOptions DtoOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    #pragma warning disable S1144, S3459 // 由 System.Text.Json 反序列化賦值
    /// <summary>servers.json 條目的反序列化形態；缺欄位一律給預設，由呼叫端映射並容錯。</summary>
    private sealed class ServerDto
    {
        public string Name { get; set; }
        public string Command { get; set; }
        public List<JsonElement> Args { get; set; }
        public bool Enabled { get; set; }
        public int TimeoutSeconds { get; set; }
    }
    #pragma warning restore S1144, S3459

    public static bool TryLoad(string path, out List<McpServerEntry> servers, out string error)
    {
        servers = [];
        error = string.Empty;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            error = "no MCP servers configured (missing servers.json)";
            return false;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path) ?? string.Empty);
            JsonElement root = doc.RootElement;
            JsonElement list;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("servers", out list)
                || list.ValueKind != JsonValueKind.Array)
            {
                error = "servers.json has no \"servers\" array";
                return false;
            }
            foreach (JsonElement element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                McpServerEntry entry = ParseServerEntry(element);
                if (entry != null) servers.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        {
            error = "cannot read servers.json: " + ex.Message;
            return false;
        }
        return true;
    }

    /// <summary>單條反序列化失敗（型別不合等）視同壞條目跳過，不連累整檔。</summary>
    private static McpServerEntry ParseServerEntry(JsonElement element)
    {
        ServerDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ServerDto>(element.GetRawText(), DtoOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        string name = (dto?.Name ?? string.Empty).Trim();
        string command = (dto?.Command ?? string.Empty).Trim();
        if (name.Length == 0 || command.Length == 0) return null;

        var entry = new McpServerEntry { Name = name, Command = command, Enabled = dto.Enabled };
        if (dto.Args != null)
        {
            foreach (JsonElement arg in dto.Args)
            {
                if (arg.ValueKind == JsonValueKind.String) entry.Args.Add(arg.GetString() ?? string.Empty);
            }
        }
        entry.TimeoutSeconds = dto.TimeoutSeconds > 0 ? dto.TimeoutSeconds : DefaultTimeoutSeconds;
        return entry;
    }
}
