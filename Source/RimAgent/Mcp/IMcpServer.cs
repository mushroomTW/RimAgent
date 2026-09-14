using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Mcp
{
    /// <summary>一個 MCP server 提供的工具摘要（SDK 型別的精簡版，方便測試替身）。</summary>
    public sealed class McpServerTool
    {
        public McpServerTool(string name, string description)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public string Name { get; }
        public string Description { get; }
    }

    /// <summary>
    /// 已連線的 MCP server。實作分兩種：SdkMcpServer（官方 SDK＋stdio 行程）與測試替身。
    /// </summary>
    public interface IMcpServer : IDisposable
    {
        string Name { get; }
        Task<IList<McpServerTool>> ListToolsAsync(CancellationToken cancellationToken);
        /// <summary>呼叫工具並回傳純文字結果；失敗丟例外（由呼叫端轉成錯誤訊息）。</summary>
        Task<string> CallAsync(string tool, string argsJson, CancellationToken cancellationToken);
    }
}
