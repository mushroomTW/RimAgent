using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RimAgent.Tools;

namespace RimAgent.Mcp
{
    /// <summary>
    /// 官方 SDK 的 stdio 用戶端：把設定條目起成子行程，initialize→list→call 全走 SDK。
    /// 連線是延遲的（第一次 list／call 才建），逾時與取消直接傳給 SDK。
    /// </summary>
    public sealed class SdkMcpServer : IMcpServer
    {
        private readonly McpServerEntry _entry;
        private readonly int _timeoutMs;
        private McpClient _client;
        private bool _disposed;

        public SdkMcpServer(McpServerEntry entry)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _timeoutMs = _entry.TimeoutSeconds > 0
                ? _entry.TimeoutSeconds * 1000
                : McpServerConfig.DefaultTimeoutSeconds * 1000;
        }

        public string Name => _entry.Name;

        public async Task<IList<McpServerTool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeout = LinkedTimeout(cancellationToken))
            {
                try
                {
                    McpClient client = await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);
                    IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                    var result = new List<McpServerTool>(tools.Count);
                    foreach (McpClientTool tool in tools)
                    {
                        result.Add(new McpServerTool(tool.Name, tool.Description));
                    }
                    return result;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("MCP server \"" + Name + "\" timed out.");
                }
            }
        }

        public async Task<string> CallAsync(string tool, string argsJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tool)) throw new ArgumentException("tool name is empty.", nameof(tool));
            using (CancellationTokenSource timeout = LinkedTimeout(cancellationToken))
            {
                try
                {
                    McpClient client = await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);
                    Dictionary<string, object> args = ParseArgs(argsJson);
                    CallToolResult result = await client.CallToolAsync(tool, args, cancellationToken: timeout.Token).ConfigureAwait(false);
                    return FormatResult(tool, result);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("MCP server \"" + Name + "\" timed out.");
                }
            }
        }

        /// <summary>單次呼叫逾時（timeoutSeconds 管的就是這裡）；外部取消原樣穿透。</summary>
        private CancellationTokenSource LinkedTimeout(CancellationToken cancellationToken)
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeoutMs);
            return timeout;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            McpClient client = _client;
            _client = null;
            if (client == null) return;
            // 遊戲主線程不等待：SDK 的 dispose 會關行程，OS 回收兜底。
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 關閉時的例外不影響遊戲，只求行程被收掉。
                }
            });
        }

        private async Task<McpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_disposed) throw new ObjectDisposedException(Name);
            if (_client != null) return _client;
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = _entry.Name,
                Command = _entry.Command,
                Arguments = _entry.Args
            });
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "RimAgent", Version = "1.0" },
                InitializationTimeout = TimeSpan.FromSeconds(_entry.TimeoutSeconds)
            };
            // transport 的生命週期交給建好的 client；建連失敗時沒有可清的東西，直接往上丟。
            McpClient client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            _client = client;
            return client;
        }

        internal static Dictionary<string, object> ParseArgs(string argsJson)
        {
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(argsJson)) return args;
            using (JsonDocument doc = JsonDocument.Parse(argsJson))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("arguments must be a JSON object.");
                }
                foreach (JsonProperty property in doc.RootElement.EnumerateObject())
                {
                    args[property.Name] = property.Value.Clone();
                }
            }
            return args;
        }

        internal static string FormatResult(string tool, CallToolResult result)
        {
            if (result == null) return ToolText.Error("MCP tool " + tool + " returned nothing.");
            var sb = new StringBuilder();
            if (result.Content != null)
            {
                foreach (ContentBlock block in result.Content)
                {
                    AppendContentBlock(sb, block);
                }
            }
            string output = sb.ToString().Trim();
            if (output.Length == 0) output = "(empty result)";
            if (result.IsError == true) return ToolText.Error(output);
            return output;
        }

        private static void AppendContentBlock(StringBuilder sb, ContentBlock block)
        {
            if (block is TextContentBlock text)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text.Text);
            }
            else if (block != null)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("[non-text content omitted: ").Append(block.Type).Append(']');
            }
        }
    }
}
