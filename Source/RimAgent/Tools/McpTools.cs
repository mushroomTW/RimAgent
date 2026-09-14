using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Mcp;

namespace RimAgent.Tools
{
    /// <summary>MCP 閘道：狀態、列工具、代呼叫。實際傳輸走官方 SDK，命令一律在本機行程執行。</summary>
    public sealed class McpTools
    {
        private readonly McpManager _manager;

        public McpTools(McpManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        [AgentTool("mcp_status", ToolCategory.Mcp)]
        [Description("Shows the configured MCP servers and whether each one is disabled, idle or running. Servers come from AgentPack/MCP/servers.json.")]
        public string Status()
        {
            return _manager.Status();
        }

        [AgentTool("mcp_list_tools", ToolCategory.Mcp)]
        [Description("Lists the tools offered by every enabled MCP server, with names and descriptions. Call this before mcp_call_tool to learn exact tool names and arguments.")]
        public async Task<string> ListTools(CancellationToken cancellationToken)
        {
            try
            {
                return await _manager.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolText.Error("MCP list failed: " + ex.Message);
            }
        }

        [AgentTool("mcp_call_tool", ToolCategory.Mcp)]
        [Description("Calls one tool on one MCP server. Arguments must be a single JSON object string matching that tool's input schema.")]
        public async Task<string> CallTool(
            [Description("Server name from mcp_status, e.g. \"fetch\".")] string server,
            [Description("Tool name from mcp_list_tools, e.g. \"fetch\".")] string tool,
            [Description("Tool arguments as a JSON object string, e.g. \"{\\\"url\\\": \\\"https://example.com\\\"}\".")] string args,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _manager.CallAsync(server, tool, args, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolText.Error("MCP call failed: " + ex.Message);
            }
        }
    }
}
