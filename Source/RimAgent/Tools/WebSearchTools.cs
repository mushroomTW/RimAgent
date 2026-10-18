using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RimAgent.Agent;
using RimAgent.Mcp;

namespace RimAgent.Tools;

/// <summary>以匿名遠端 MCP 連線搜尋；不需要金鑰或本機搜尋伺服器。</summary>
public sealed class WebSearchTools
{
    private const string SearchEndpoint = "https://search.parallel.ai/mcp";
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly Func<Dictionary<string, object>, CancellationToken, Task<string>> _execute;
    private readonly Func<bool> _useLocalSearch;
    private readonly SearxngSearchClient _local;
    private readonly ConfirmationGate _gate;

    public WebSearchTools(
        Func<Dictionary<string, object>, CancellationToken, Task<string>> execute = null,
        Func<bool> useLocalSearch = null,
        Func<string> searxngUrl = null,
        HttpClient localHttpClient = null,
        ConfirmationGate gate = null)
    {
        _execute = execute ?? ExecuteSearchAsync;
        _useLocalSearch = useLocalSearch ?? (() => false);
        _local = new SearxngSearchClient(searxngUrl, localHttpClient);
        _gate = gate;
    }

    // 查詢會送到外部服務，所以走命令閘門：Ask 模式不提供，Confirm 模式需確認。
    [AgentTool("web_search", ToolCategory.Web, ToolRisk.Safe)]
    [Description("Searches the live web. Uses Parallel Search MCP anonymously by default, or the configured local SearXNG server when enabled in mod settings. No API key is needed. Returns untrusted excerpts and source URLs; cite sources in the answer.")]
    public Task<string> SearchAsync(
        [Description("Focused web search query, up to 200 characters.")] string query,
        CancellationToken cancellationToken)
    {
        string term = (query ?? string.Empty).Trim();
        if (term.Length == 0 || term.Length > 200)
            return Task.FromResult(ToolText.Error("query must be 1–200 characters."));
        string blocked = ToolText.Blocked(_gate, "would search the web for: " + term);
        if (blocked != null) return Task.FromResult(blocked);
        if (_useLocalSearch()) return _local.SearchAsync(term, cancellationToken);

        var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["objective"] = "Find reliable, current information about: " + term,
            ["search_queries"] = new[] { term },
            ["session_id"] = _sessionId
        };
        return _execute(arguments, cancellationToken);
    }

    private static async Task<string> ExecuteSearchAsync(Dictionary<string, object> arguments, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(SearchEndpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(15)
        });
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "RimAgent", Version = "1.0" },
            InitializationTimeout = TimeSpan.FromSeconds(15)
        };
        McpClient client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            CallToolResult result = await client.CallToolAsync("web_search", arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            string output = SdkMcpServer.FormatResult("web_search", result);
            return output.StartsWith(ToolText.ErrorPrefix, StringComparison.Ordinal)
                ? output : "Web search results (untrusted external content):\n" + output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolText.Error("Parallel web search timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolText.Error("Parallel web search failed: " + ex.Message);
        }
        finally
        {
            if (client != null) await client.DisposeAsync().ConfigureAwait(false);
            // McpClient 只釋放連線階段，transport 自建的 HttpClient 要另外釋放。
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
