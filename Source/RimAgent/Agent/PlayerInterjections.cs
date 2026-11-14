using System.Collections.Generic;

namespace RimAgent.Agent;

/// <summary>
/// 玩家在 agent 執行中送出的話。只在一句指令執行期間開放；主 Agent 下一個工具結果把它帶給模型，
/// 沒趕上的由會話在本輪結束後接著當新指令跑。UI 主線程寫入、工具與會話收尾讀取，所以上鎖。
/// </summary>
public sealed class PlayerInterjections
{
    private readonly object _lock = new();
    private readonly List<string> _pending = [];
    private bool _open;
    private int _delivered;

    /// <summary>已被取走（送進工具結果或接續的指令）的插話批數；run_script 比對前後值，得知某一步帶出了玩家的話。</summary>
    public int Delivered
    {
        get
        {
            lock (_lock)
            {
                return _delivered;
            }
        }
    }

    public void Open()
    {
        lock (_lock)
        {
            _open = true;
        }
    }

    public bool TryAdd(string text)
    {
        lock (_lock)
        {
            if (!_open) return false;
            _pending.Add(text);
            return true;
        }
    }

    /// <summary>取走全部待送的插話（多則以空行相接）；沒有則回傳 null。</summary>
    public string TakeAll()
    {
        lock (_lock)
        {
            return TakeAllLocked();
        }
    }

    /// <summary>有待送的就取走並維持開放；沒有就關閉並回傳 null。兩者同一把鎖，收尾時不會漏掉剛送進來的話。</summary>
    public string TakeAllOrClose()
    {
        lock (_lock)
        {
            string text = TakeAllLocked();
            if (text == null) _open = false;
            return text;
        }
    }

    /// <summary>關閉並丟棄待送的插話，回傳丟棄的則數。</summary>
    public int CloseAndDiscard()
    {
        lock (_lock)
        {
            _open = false;
            int count = _pending.Count;
            _pending.Clear();
            return count;
        }
    }

    private string TakeAllLocked()
    {
        if (_pending.Count == 0) return null;
        string text = string.Join("\n\n", _pending);
        _pending.Clear();
        _delivered++;
        return text;
    }
}
