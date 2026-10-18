using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimAgent.Mcp;

namespace RimAgent.Tools;

/// <summary>MCP 閘道：狀態、列工具、代呼叫。實際傳輸走官方 SDK，命令一律在本機行程執行。</summary>
public sealed class McpTools
{
    private readonly McpManager _manager;
    private readonly ConfirmationGate _gate;

    public McpTools(McpManager manager, ConfirmationGate gate = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _gate = gate;
    }

    [AgentTool("mcp_list_tools", ToolCategory.Mcp, ToolRisk.Query)]
    [Description("Shows the MCP servers configured in AgentPack/MCP/servers.json with their state (disabled, idle or running), then the tools of every enabled server with names and descriptions. Call it before mcp_call_tool for the exact server and tool name.")]
    public async Task<string> ListTools(CancellationToken cancellationToken)
    {
        string status = _manager.Status();
        try
        {
            string tools = await _manager.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            // 沒有設定任何 server 時清單就是狀態本身，不重複印。
            return tools == status ? status : status + "\n\n" + tools;
        }
        catch (Exception ex)
        {
            return status + "\n\n" + ToolText.Error("MCP list failed: " + ex.Message);
        }
    }

    [AgentTool("mcp_call_tool", ToolCategory.Mcp, ToolRisk.Confirm)]
    [Description("Calls one tool on one MCP server with a JSON object of arguments matching that tool's input schema.")]
    public async Task<string> CallTool(
        [Description("Server name from mcp_list_tools, e.g. \"fetch\".")] string server,
        [Description("Tool name from mcp_list_tools, e.g. \"fetch\".")] string tool,
        [Description("Tool arguments as a JSON object string, e.g. \"{\\\"url\\\": \\\"https://example.com\\\"}\".")] string args,
        CancellationToken cancellationToken)
    {
        string blocked = ToolText.Blocked(_gate, $"would call MCP tool {tool} on server {server} with {(string.IsNullOrWhiteSpace(args) ? "no arguments" : args)}.");
        if (blocked != null) return blocked;

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
