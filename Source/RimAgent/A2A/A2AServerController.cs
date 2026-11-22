using System;
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

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IAsyncDisposable? _server;
    private static int _runningPort;
    private static string? _folder;

    public static int RunningPort => Volatile.Read(ref _runningPort);

    public static string? LastError { get; private set; }

    public static void Apply(string modRootDir, bool enabled, int port)
    {
        _folder ??= Path.Combine(modRootDir, FolderName);
        int desired = enabled ? port : 0;
        if (desired == RunningPort && LastError == null) return;
        _ = Task.Run(() => ApplyAsync(desired));
    }

    private static async Task ApplyAsync(int desiredPort)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (desiredPort == RunningPort && LastError == null) return;
            if (_server != null)
            {
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
                Volatile.Write(ref _runningPort, 0);
            }
            LastError = null;
            if (desiredPort == 0) return;
            try
            {
                RegisterResolver(_folder!);
                _server = await StartHostAsync(_folder!, desiredPort).ConfigureAwait(false);
                Volatile.Write(ref _runningPort, desiredPort);
                Log.Message("[RimAgent] A2A server listening on http://localhost:" + desiredPort + " (JSON-RPC) and :" + (desiredPort + 1) + " (gRPC).");
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
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>
    /// 只有這個方法碰 RimAgent.A2A 的型別：JIT 編譯它時才會載入該組件，必須在 <see cref="RegisterResolver"/> 之後才第一次呼叫，
    /// 所以不能內嵌進呼叫端。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<IAsyncDisposable> StartHostAsync(string folder, int port)
    {
        string version = typeof(A2AServerController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        // 明確 new：方法群組轉換會被編譯器快取成靜態欄位，RimWorld 啟動掃描欄位型別時就會載入 RimAgent.A2A（見 A2AShippingTests）。
        return RimAgent.A2A.A2AServerHost.StartAsync(folder, port, port + 1, version, new ColonyInstructionRunner(RunRemoteInstructionAsync));
    }

    /// <summary>遠端 agent 送來的一句指令，照玩家在視窗裡送出的方式跑：同樣受權限、模式與確認閘門管控。</summary>
    internal static async Task<string> RunRemoteInstructionAsync(string instruction, CancellationToken cancellationToken)
    {
        string? reply;
        try
        {
            reply = await RimLLMDispatcher.EnqueueOnMainThreadAsync(() =>
            {
                RimAgentGameComponent? component = RimAgentGameComponent.Instance
                    ?? throw new InvalidOperationException("No colony is loaded in RimWorld right now.");
                return component.Session.SendAsync(instruction, displayText: "[A2A] " + instruction, cancellationToken: cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentBusyException ex)
        {
            throw new InvalidOperationException("RimAgent is busy with another instruction. Try again after it finishes.", ex);
        }
        return reply ?? throw new InvalidOperationException("RimAgent could not finish the instruction; the error is shown in its window in game.");
    }
}
