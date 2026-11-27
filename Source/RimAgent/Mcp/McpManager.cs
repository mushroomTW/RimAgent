using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimAgent.Mcp;

public sealed class McpManager
{
    /// <summary>server 閒置多久視為可關（分鐘）。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly string? _configPath;
    private readonly Func<McpServerEntry, IMcpServer> _factory;
    private readonly Lock _gate = new();
    private List<McpServerEntry>? _entries;
    private volatile bool _loaded;
    private bool _shutdown;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Bug", "S2930:\"IDisposables\" should be disposed",
        Justification = "Shutdown 時可能仍有非同步呼叫在讀 Token；CTS 沒有計時器或等待把手，交給 GC 即可。")]
    private readonly CancellationTokenSource _shutdownCts = new();
    private string _loadError = string.Empty;
    private readonly Dictionary<string, IMcpServer> _clients =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastUsed =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Tuple<IMcpServer, IList<McpServerTool>?>>> _connecting =
        new(StringComparer.OrdinalIgnoreCase);

    public McpManager(string? configPath) : this(configPath, entry => new SdkMcpServer(entry))
    {
    }

    internal McpManager(string? configPath, Func<McpServerEntry, IMcpServer> factory)
    {
        _configPath = configPath;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

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

    public async Task<string> ListToolsAsync(CancellationToken cancellationToken)
    {
        SweepIdle();
        List<McpServerEntry> entries = Entries();
        if (entries.Count == 0) return Status();
        var sb = new StringBuilder();
        foreach (McpServerEntry entry in entries)
        {
            if (!entry.Enabled) continue;
            await AppendServerToolsAsync(sb, entry, cancellationToken).ConfigureAwait(false);
        }
        string text = sb.ToString().Trim();
        return text.Length == 0 ? "No MCP servers enabled." : text;
    }

    private async Task AppendServerToolsAsync(StringBuilder sb, McpServerEntry entry, CancellationToken cancellationToken)
    {
        IMcpServer? used = null;
        try
        {
            // 首次連線時探測那一趟已經拿到清單，直接用，不多跑一次 list。
            Tuple<IMcpServer, IList<McpServerTool>?> connection = await EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false);
            used = connection.Item1;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 玩家按停止：連線沒壞，不丟掉 server。
            throw;
        }
        catch (Exception ex)
        {
            sb.Append('[').Append(entry.Name).Append("] failed: ").Append(ex.Message).AppendLine();
            // 連線階段就失敗時 EnsureConnectedAsync 已自行釋放它建的實例；登記中的可能是別人剛連好的，不能動。
            if (used != null) Drop(entry.Name, used);
        }
    }

    public async Task<string> CallAsync(string? server, string tool, string argsJson, CancellationToken cancellationToken)
    {
        SweepIdle();
        List<McpServerEntry> entries = Entries();
        if (entries.Count == 0) return Status();
        McpServerEntry? entry = entries.Find(e => string.Equals(e.Name, server ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return Tools.ToolText.Error("unknown MCP server \"" + server + "\".");
        if (!entry.Enabled) return Tools.ToolText.Error("MCP server \"" + entry.Name + "\" is disabled.");
        IMcpServer? connected = null;
        try
        {
            connected = (await EnsureConnectedAsync(entry, cancellationToken).ConfigureAwait(false)).Item1;
            // 長時間呼叫的起點與終點都更新時間，避免跑到一半被當成閒置掃掉。
            Touch(entry.Name, connected);
            string result = await connected.CallAsync(tool, argsJson, cancellationToken).ConfigureAwait(false);
            Touch(entry.Name, connected);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 玩家按停止：連線沒壞，不丟掉 server。
            throw;
        }
        catch (Exception ex)
        {
            // 連線階段就失敗時 EnsureConnectedAsync 已自行釋放它建的實例；登記中的可能是別人剛連好的，不能動。
            if (connected != null) Drop(entry.Name, connected);
            return Tools.ToolText.Error("MCP server \"" + entry.Name + "\" failed: " + ex.Message);
        }
    }

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
            // 連線探測本身就做過一次 list，這裡不再列一次；ConnectNewServerAsync 內部已掛 _shutdownCts.Token。
            await EnsureConnectedAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 暖機失敗：EnsureConnectedAsync 已釋放自己建的實例；不動可能由別的呼叫者剛登記的連線。
        }
    }

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

    public void Shutdown()
    {
        List<IMcpServer> servers;
        lock (_gate)
        {
            _shutdown = true;
            servers = [.. _clients.Values];
            _clients.Clear();
            _lastUsed.Clear();
            _connecting.Clear();
        }
        _shutdownCts.Cancel();
        foreach (IMcpServer server in servers)
        {
            try { server.Dispose(); }
            catch (Exception) { /* 關閉失敗不影響遊戲。 */ }
        }
    }

    private List<McpServerEntry> Entries()
    {
        if (_loaded) return _entries ?? [];
        lock (_gate)
        {
            if (_loaded) return _entries ?? [];
            if (McpServerConfig.TryLoad(_configPath, out List<McpServerEntry> servers, out string error))
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
    private async Task<Tuple<IMcpServer, IList<McpServerTool>?>> EnsureConnectedAsync(McpServerEntry entry, CancellationToken cancellationToken)
    {
        Task<Tuple<IMcpServer, IList<McpServerTool>?>>? inflight;
        TaskCompletionSource<Tuple<IMcpServer, IList<McpServerTool>?>>? tcs = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown, nameof(McpManager));
            if (_clients.TryGetValue(entry.Name, out IMcpServer? running))
            {
                _lastUsed[entry.Name] = DateTime.UtcNow;
                return Tuple.Create<IMcpServer, IList<McpServerTool>?>(running, null);
            }
            if (!_connecting.TryGetValue(entry.Name, out inflight))
            {
                tcs = new TaskCompletionSource<Tuple<IMcpServer, IList<McpServerTool>?>>(TaskCreationOptions.RunContinuationsAsynchronously);
                inflight = tcs.Task;
                _connecting[entry.Name] = inflight;
            }
        }
        if (tcs != null)
        {
            _ = ConnectNewServerAsync(entry, tcs);
        }
        Tuple<IMcpServer, IList<McpServerTool>?> result = await inflight.WaitAsync(cancellationToken).ConfigureAwait(false);
        return tcs != null ? result : Tuple.Create<IMcpServer, IList<McpServerTool>?>(result.Item1, null);
    }

    private async Task ConnectNewServerAsync(McpServerEntry entry, TaskCompletionSource<Tuple<IMcpServer, IList<McpServerTool>?>> tcs)
    {
        IMcpServer? server = null;
        IMcpServer? toDispose = null;
        bool stored = false;
        try
        {
            // 建構只組參數不連線；真正的握手在第一次 list／call 內發生。
            server = _factory(entry);
            // 先做一次 list 確認連線可用，壞 server 在這裡就現形；清單順便交給呼叫端，免得再列一次。
            IList<McpServerTool>? tools = await server.ListToolsAsync(_shutdownCts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_connecting.TryGetValue(entry.Name, out var current) && ReferenceEquals(current, tcs.Task))
                    _connecting.Remove(entry.Name);
                ObjectDisposedException.ThrowIf(_shutdown, nameof(McpManager));
                if (_clients.TryGetValue(entry.Name, out IMcpServer? existing))
                {
                    toDispose = server;
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
            toDispose?.Dispose();
            tcs.TrySetResult(Tuple.Create(server, tools));
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_connecting.TryGetValue(entry.Name, out var current) && ReferenceEquals(current, tcs.Task))
                    _connecting.Remove(entry.Name);
            }
            if (!stored)
            {
                try { server?.Dispose(); }
                catch (Exception) { /* 釋放失敗不蓋掉原本的連線錯誤。 */ }
            }
            tcs.TrySetException(ex);
            // 若所有等待者皆已取消，預先觀察例外避免觸發 UnobservedTaskException。
            _ = tcs.Task.Exception;
        }
    }

    private void Touch(string name, IMcpServer server)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(name, out IMcpServer? current) && ReferenceEquals(current, server)) _lastUsed[name] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 丟掉 server；帶 <paramref name="failed"/> 時只丟掉那個失敗的實例，
    /// 若登記的已是別的呼叫者重連的新實例就不動它（失敗的舊實例由其建立者自行釋放）。
    /// </summary>
    private void Drop(string name, IMcpServer? failed = null)
    {
        IMcpServer? server = null;
        lock (_gate)
        {
            if (_clients.TryGetValue(name, out server))
            {
                if (failed != null && !ReferenceEquals(server, failed))
                {
                    server = null;
                }
                else
                {
                    _clients.Remove(name);
                    _lastUsed.Remove(name);
                }
            }
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
