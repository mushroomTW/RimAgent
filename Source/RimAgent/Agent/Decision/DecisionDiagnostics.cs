using System;
using System.Collections.Concurrent;

namespace RimAgent.Agent;

/// <summary>
/// 決策模型失敗的診斷：各 Decision* 元件原本吞掉例外退回後備，玩家完全看不到原因。
/// 這裡每種不同的失敗訊息只警告一次，避免每個工具呼叫都洗版；單元測試可換掉輸出。
/// </summary>
internal static class DecisionDiagnostics
{
    private const int MaxDistinct = 64;

    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

    /// <summary>實際輸出；預設寫入遊戲日誌。測試可替換為收集器。</summary>
    internal static Action<string> Sink { get; set; } = message => Verse.Log.Warning(message);

    /// <summary>記錄一次決策失敗；同一元件＋同一訊息只輸出第一次。</summary>
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

    /// <summary>清除已警告的紀錄（測試用）。</summary>
    internal static void Reset()
    {
        Seen.Clear();
    }
}
