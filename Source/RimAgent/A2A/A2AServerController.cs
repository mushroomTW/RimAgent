using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using RimAgent.Agent;
using RimLLM_Framework.Core;
using Verse;

namespace RimAgent.A2A;

/// <summary>
/// 依設定啟停 A2A 伺服端。伺服端與它的 ASP.NET Core／gRPC 組件放在 Mod 根目錄的 A2A/，
/// 開啟時才註冊解析器並載入，關閉時不載入任何一個。
/// </summary>
public static class A2AServerController
{
    public const string FolderName = "A2A";

    private sealed record DesiredConfig(int Port, string? BearerToken);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lock ActiveInstructionGate = new();
    private static IAsyncDisposable? _server;
    private static int _runningPort;
    private static string? _runningToken;
    private static DesiredConfig _desired = new(0, BearerToken: null);
    private static int _workerRunning;
    private static string? _folder;
    private static volatile string? _lastError;
    private static CancellationTokenSource? _activeInstructionCts;

    public static int RunningPort => Volatile.Read(ref _runningPort);

    public static string? LastError
    {
        get => _lastError;
        private set => _lastError = value;
    }

    public static void Apply(string modRootDir, bool enabled, int port, string? bearerToken = null)
    {
        _folder ??= Path.Combine(modRootDir, FolderName);
        int desiredPort = enabled ? Math.Clamp(port, RimAgentSettings.MinA2APort, RimAgentSettings.MaxA2APort) : 0;
        var desired = new DesiredConfig(desiredPort, enabled ? bearerToken : null);
        Interlocked.Exchange(ref _desired, desired);
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
        {
            _ = Task.Run(ConvergeLoopAsync, CancellationToken.None);
        }
    }

    internal static void CancelActiveInstruction()
    {
        lock (ActiveInstructionGate)
        {
            _activeInstructionCts?.Cancel();
        }
    }

    private static async Task ConvergeLoopAsync()
    {
        try
        {
            while (true)
            {
                DesiredConfig desired = Volatile.Read(ref _desired);
                await ApplyOnceAsync(desired).ConfigureAwait(false);
                // 啟動失敗時不再對同一個組態無限重試；等設定再次變動才重新觸發。
                if (ReferenceEquals(Volatile.Read(ref _desired), desired))
                {
                    Volatile.Write(ref _workerRunning, 0);
                    if (!ReferenceEquals(Volatile.Read(ref _desired), desired)
                        && Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                    {
                        continue;
                    }
                    return;
                }
            }
        }
        catch
        {
            Volatile.Write(ref _workerRunning, 0);
            throw;
        }
    }

    private static async Task ApplyOnceAsync(DesiredConfig desired)
    {
        await Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (desired.Port == RunningPort
                && string.Equals(desired.BearerToken, _runningToken, StringComparison.Ordinal)
                && LastError == null)
            {
                return;
            }
            if (_server != null)
            {
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
                Volatile.Write(ref _runningPort, 0);
                _runningToken = null;
            }
            LastError = null;
            if (desired.Port == 0) return;
            try
            {
                RegisterResolver(_folder!);
                _server = await StartHostAsync(_folder!, desired.Port, desired.BearerToken).ConfigureAwait(false);
                Volatile.Write(ref _runningPort, desired.Port);
                _runningToken = desired.BearerToken;
                Log.Message(string.Create(CultureInfo.InvariantCulture, $"[RimAgent] A2A server listening on http://localhost:{desired.Port} (JSON-RPC) and :{desired.Port + 1} (gRPC)."));
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
                Log.Warning("[RimAgent] A2A server failed to start: " + ex);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static int _resolverRegistered;

    private static void RegisterResolver(string folder)
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0) return;
        string root = Path.GetFullPath(folder);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string? simpleName = name.Name;
            if (string.IsNullOrWhiteSpace(simpleName)
                || simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || simpleName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || simpleName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }
            string path = Path.GetFullPath(Path.Combine(root, simpleName + ".dll"));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>
    /// 只有這個方法碰 RimAgent.A2A 的型別：JIT 編譯它時才會載入該組件，必須在 <see cref="RegisterResolver"/> 之後才第一次呼叫，
    /// 所以不能內嵌進呼叫端。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<IAsyncDisposable> StartHostAsync(string folder, int port, string? bearerToken)
    {
        string version = typeof(A2AServerController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        // 明確 new：方法群組轉換會被編譯器快取成靜態欄位，RimWorld 啟動掃描欄位型別時就會載入 RimAgent.A2A（見 A2AShippingTests）。
        return RimAgent.A2A.A2AServerHost.StartAsync(folder, port, port + 1, version, new ColonyInstructionRunner(RunRemoteInstructionAsync), bearerToken ?? string.Empty);
    }

    /// <summary>遠端 agent 送來的一句指令，照玩家在視窗裡送出的方式跑：同樣受權限、模式與確認閘門管控。</summary>
    internal static async Task<string> RunRemoteInstructionAsync(string instruction, CancellationToken cancellationToken)
    {
        string? reply;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (ActiveInstructionGate)
        {
            _activeInstructionCts = linked;
        }
        try
        {
            reply = await RimLLMDispatcher.EnqueueOnMainThreadAsync(() =>
            {
                RimAgentGameComponent? component = RimAgentGameComponent.Instance
                    ?? throw new InvalidOperationException("No colony is loaded in RimWorld right now.");
                return component.Session.SendAsync(instruction, displayText: "[A2A] " + instruction, cancellationToken: linked.Token);
            }, linked.Token).ConfigureAwait(false);
        }
        catch (AgentBusyException ex)
        {
            throw new InvalidOperationException("RimAgent is busy with another instruction. Try again after it finishes.", ex);
        }
        finally
        {
            lock (ActiveInstructionGate)
            {
                if (ReferenceEquals(_activeInstructionCts, linked))
                    _activeInstructionCts = null;
            }
        }
        linked.Token.ThrowIfCancellationRequested();
        return reply ?? throw new InvalidOperationException("RimAgent could not finish the instruction; the error is shown in its window in game.");
    }
}
