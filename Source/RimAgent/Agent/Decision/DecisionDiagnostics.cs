using System;
using System.Collections.Concurrent;

namespace RimAgent.Agent;

/// <summary>每種不同的失敗訊息只警告一次，避免每個工具呼叫都洗版；單元測試可換掉輸出。</summary>
internal static class DecisionDiagnostics
{
    private const int MaxDistinct = 64;

    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

    internal static Action<string> Sink { get; set; } = message => Verse.Log.Warning(message);

    internal static void Warn(string component, Exception ex)
    {
        string message = "RimAgent " + component + ": decision model call failed, using fallback (" + (ex?.Message ?? "unknown") + ")";
        if (Seen.Count >= MaxDistinct && !Seen.ContainsKey(message)) return;
        if (!Seen.TryAdd(message, 0)) return;
        try
        {
            Sink?.Invoke(message);
        }
        catch (Exception)
        {
            // 日誌本身不可用（例如在遊戲外執行）不該讓決策流程失敗。
        }
    }

    internal static void Reset()
    {
        Seen.Clear();
    }
}
