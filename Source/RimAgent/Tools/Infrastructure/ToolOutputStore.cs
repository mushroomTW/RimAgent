using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;

namespace RimAgent.Tools;

/// <summary>
/// 只留最近幾份；不進存檔，讀檔或清除對話後舊代號失效，模型會被告知重跑原工具。
/// 子代理與主 Agent 的工具可能先後寫入，所以上鎖。
/// </summary>
internal sealed class ToolOutputStore
{
    public const int Capacity = 20;

    /// <summary>
    /// 全部保存內容的字元上限（約 4 MB）：MCP 抓網頁之類的結果一份就可能數 MB，只限份數會堆積大量字串。
    /// 超過就先丟最舊的；單份超過上限只存前段。
    /// </summary>
    public const int MaxTotalChars = 2000000;

    // 子代理與主 Agent 共用同一批工具實例；用呼叫流程上的 AsyncLocal 分流，平行任務不會互相寫入。
    private static readonly AsyncLocal<ToolOutputStore?> Scoped = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _outputs = [];
    private readonly List<string> _order = [];
    private int _next;
    private long _totalChars;

    public static ToolOutputStore? Current => Scoped.Value;

    public static IDisposable EnterScope(ToolOutputStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ToolOutputStore? previous = Scoped.Value;
        Scoped.Value = store;
        return new ScopeExit(previous);
    }

    private sealed class ScopeExit : IDisposable
    {
        private readonly ToolOutputStore? _previous;

        public ScopeExit(ToolOutputStore? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Scoped.Value = _previous;
        }
    }

    public string Save(string text)
    {
        if (text.Length > MaxTotalChars)
        {
            text = text[..ToolOutputLimit.AlignToCharBoundary(text, MaxTotalChars)] +
                   "\n[saved output stops here: only the first " + MaxTotalChars.ToString(CultureInfo.InvariantCulture) + " characters were kept]";
        }
        lock (_lock)
        {
            string id = "out_" + (++_next).ToString(CultureInfo.InvariantCulture);
            _outputs[id] = text;
            _order.Add(id);
            _totalChars += text.Length;
            // 最新這份一定留著。
            while (_order.Count > Capacity || (_totalChars > MaxTotalChars && _order.Count > 1))
            {
                _totalChars -= _outputs[_order[0]].Length;
                _outputs.Remove(_order[0]);
                _order.RemoveAt(0);
            }
            return id;
        }
    }

    public bool TryGet(string? id, [NotNullWhen(true)] out string? text)
    {
        lock (_lock)
        {
            return _outputs.TryGetValue(id?.Trim() ?? string.Empty, out text);
        }
    }

    /// <summary>對話清除或讀檔時呼叫；編號不歸零，舊歷史裡的代號不會指到新內容。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _outputs.Clear();
            _order.Clear();
            _totalChars = 0;
        }
    }
}
