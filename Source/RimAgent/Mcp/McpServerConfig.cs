using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RimAgent.Mcp
{
    /// <summary>servers.json 裡的一個 server 條目。</summary>
    public sealed class McpServerEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public List<string> Args { get; set; } = new List<string>();
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

        public static bool TryLoad(string path, out List<McpServerEntry> servers, out string error)
        {
            servers = new List<McpServerEntry>();
            error = string.Empty;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "no MCP servers configured (missing servers.json)";
                return false;
            }
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path) ?? string.Empty))
                {
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
                        string name = GetString(element, "name");
                        string command = GetString(element, "command");
                        if (name.Length == 0 || command.Length == 0) continue;
                        var entry = new McpServerEntry { Name = name, Command = command };
                        JsonElement args;
                        if (element.TryGetProperty("args", out args) && args.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement arg in args.EnumerateArray())
                            {
                                if (arg.ValueKind == JsonValueKind.String) entry.Args.Add(arg.GetString() ?? string.Empty);
                            }
                        }
                        JsonElement enabled;
                        if (element.TryGetProperty("enabled", out enabled)
                            && (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
                        {
                            entry.Enabled = enabled.GetBoolean();
                        }
                        int timeout = GetInt(element, "timeoutSeconds");
                        entry.TimeoutSeconds = timeout > 0 ? timeout : DefaultTimeoutSeconds;
                        servers.Add(entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                error = "cannot read servers.json: " + ex.Message;
                return false;
            }
            return true;
        }

        private static string GetString(JsonElement element, string property)
        {
            JsonElement value;
            if (element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String)
            {
                return (value.GetString() ?? string.Empty).Trim();
            }
            return string.Empty;
        }

        private static int GetInt(JsonElement element, string property)
        {
            JsonElement value;
            if (element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Number)
            {
                int parsed;
                if (value.TryGetInt32(out parsed)) return parsed;
            }
            return 0;
        }
    }
}
