using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Mcp;

/// <summary>
/// 一場遊戲期間的 MCP server 管理：設定讀一次，server 全部延遲連線，
/// 閒置逾時掃蕩，關閉不阻塞主線程。缺設定時所有操作回報原因。
/// </summary>
public sealed class McpManager
{
    /// <summary>server 閒置多久視為可關（分鐘）。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly string _configPath;
    private readonly Func<McpServerEntry, IMcpServer> _factory;
    private readonly object _gate = new();
    private List<McpServerEntry> _entries;
    private bool _loaded;
    private string _loadError = string.Empty;
    private readonly Dictionary<string, IMcpServer> _clients =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastUsed =
        new(StringComparer.OrdinalIgnoreCase);

    public McpManager(string configPath) : this(configPath, entry => new SdkMcpServer(entry))
    {
    }

    internal McpManager(string configPath, Func<McpServerEntry, IMcpServer> factory)
    {
        _configPath = configPath;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>設定與連線狀態的人類可讀摘要（mcp_list_tools 用）。</summary>
    public string Status()
    {
        List<McpServerEntry> entries = Entries();
        if (entries.Count == 0)
        {
            return string.IsNullOrEmpty(_loadError)
                ? "No MCP servers configured."
                : "MCP unavailable: " + _loadError;
        }
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (McpServerEntry entry in entries)
            {
                string state;
                if (!entry.Enabled)
                {
                    state = "disabled";
                }
                else if (_clients.ContainsKey(entry.Name))
                {
                    state = "running";
                }
                else
                {
                    state = "idle";
                }
                sb.Append('[').Append(entry.Name).Append("] ").Append(state).AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>列出已啟用 server 的工具（會連線；失敗的 server 內嵌原因）。</summary>
    public async Task<string> ListToolsAsync(CancellationToken cancellationToken)
    {
        SweepIdle();
        List<McpServerEntry> entries = Entries();
        if (entries.Count == 0) return Status();
        var sb = new StringBuilder();
        foreach (McpServerEntry entry in entries)
        {
            if (!entry.Enabled) continue;
            try
            {
                // 首次連線時探測那一趟已經拿到清單，直接用，不多跑一次 list。
                Tuple<IMcpServer, IList<McpServerTool>> connection = await EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false);
                IList<McpServerTool> tools = connection.Item2
                                              ?? await connection.Item1.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                sb.Append('[').Append(entry.Name).Append("] ").Append(tools.Count).Append(" tool(s):").AppendLine();
                foreach (McpServerTool tool in tools)
                {
                    sb.Append("  - ").Append(tool.Name);
                    if (tool.Description.Length > 0) sb.Append(": ").Append(tool.Description);
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.Append('[').Append(entry.Name).Append("] failed: ").Append(ex.Message).AppendLine();
                Drop(entry.Name);
            }
        }
        string text = sb.ToString().Trim();
        return text.Length == 0 ? "No MCP servers enabled." : text;
    }

    /// <summary>呼叫指定 server 的工具；server 未啟用或未知時回報原因。</summary>
    public async Task<string> CallAsync(string server, string tool, string argsJson, CancellationToken cancellationToken)
    {
        SweepIdle();
        List<McpServerEntry> entries = Entries();
        if (entries.Count == 0) return Status();
        McpServerEntry entry = entries.Find(e => string.Equals(e.Name, server ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return Tools.ToolText.Error("unknown MCP server \"" + server + "\".");
        if (!entry.Enabled) return Tools.ToolText.Error("MCP server \"" + entry.Name + "\" is disabled.");
        try
        {
            IMcpServer connected = (await EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false)).Item1;
            return await connected.CallAsync(tool, argsJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Drop(entry.Name);
            return Tools.ToolText.Error("MCP server \"" + entry.Name + "\" failed: " + ex.Message);
        }
    }

    /// <summary>暖機：在背景把已啟用的 server 同時連起來，玩家真正用時不必等開行程。</summary>
    public Task WarmUpAsync()
    {
        var tasks = new List<Task>();
        foreach (McpServerEntry entry in Entries())
        {
            if (entry.Enabled) tasks.Add(WarmUpOneAsync(entry));
        }
        return Task.WhenAll(tasks);
    }

    private async Task WarmUpOneAsync(McpServerEntry entry)
    {
        try
        {
            // 連線探測本身就做過一次 list，這裡不再列一次。
            await EnsureConnectedAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Drop(entry.Name);
        }
    }

    /// <summary>關掉閒置逾時的 server（存檔時與每次使用前呼叫，不阻塞）。</summary>
    public void SweepIdle()
    {
        List<string> stale = [];
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, DateTime> pair in _lastUsed)
            {
                if (_clients.ContainsKey(pair.Key) && now - pair.Value > IdleTimeout) stale.Add(pair.Key);
            }
        }
        foreach (string name in stale) Drop(name);
    }

    /// <summary>關掉全部 server（不等待行程結束）。</summary>
    public void Shutdown()
    {
        List<string> names;
        lock (_gate)
        {
            names = [.. _clients.Keys];
        }
        foreach (string name in names) Drop(name);
    }

    private List<McpServerEntry> Entries()
    {
        if (_loaded) return _entries;
        lock (_gate)
        {
            if (_loaded) return _entries;
            List<McpServerEntry> servers;
            string error;
            if (McpServerConfig.TryLoad(_configPath, out servers, out error))
            {
                _entries = servers;
            }
            else
            {
                _entries = [];
                _loadError = error;
            }
            _loaded = true;
            return _entries;
        }
    }

    /// <summary>已連上的 server；Item2 只在這次呼叫真的建了連線時帶著探測到的工具清單。</summary>
    private async Task<Tuple<IMcpServer, IList<McpServerTool>>> EnsureConnectedAsync(McpServerEntry entry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IMcpServer running;
            if (_clients.TryGetValue(entry.Name, out running))
            {
                _lastUsed[entry.Name] = DateTime.UtcNow;
                return Tuple.Create(running, (IList<McpServerTool>)null);
            }
        }
        // 建構只組參數不連線；真正的握手在第一次 list／call 內發生。
        IMcpServer server = _factory(entry);
        bool stored = false;
        try
        {
            // 先做一次 list 確認連線可用，壞 server 在這裡就現形；清單順便交給呼叫端，免得再列一次。
            IList<McpServerTool> tools = await server.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                IMcpServer existing;
                if (_clients.TryGetValue(entry.Name, out existing))
                {
                    // 別人搶先連好了：用它的，探測結果是另一個行程的就不沿用。
                    server.Dispose();
                    server = existing;
                    tools = null;
                }
                else
                {
                    _clients[entry.Name] = server;
                    stored = true;
                }
                _lastUsed[entry.Name] = DateTime.UtcNow;
            }
            return Tuple.Create(server, tools);
        }
        catch (Exception)
        {
            if (!stored) server.Dispose();
            throw;
        }
    }

    private void Drop(string name)
    {
        IMcpServer server = null;
        lock (_gate)
        {
            if (_clients.TryGetValue(name, out server)) _clients.Remove(name);
            _lastUsed.Remove(name);
        }
        if (server != null)
        {
            try
            {
                server.Dispose();
            }
            catch (Exception)
            {
                // 關閉失敗不影響遊戲。
            }
        }
    }
}
