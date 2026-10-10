using System.Collections.Generic;
using System.Globalization;

namespace RimAgent.Tools
{
    /// <summary>
    /// 被截斷的工具結果原文，以代號（out_1、out_2…）保存到對話結束，供 read_tool_output 取回省略的部分。
    /// 只留最近幾份；不進存檔，讀檔或清除對話後舊代號失效，模型會被告知重跑原工具。
    /// 子代理與主 Agent 的工具可能先後寫入，所以上鎖。
    /// </summary>
    internal sealed class ToolOutputStore
    {
        /// <summary>保留的份數；更舊的先丟，代號不重用。</summary>
        public const int Capacity = 20;

        /// <summary>
        /// 全部保存內容的字元上限（約 4 MB）：MCP 抓網頁之類的結果一份就可能數 MB，只限份數會在 Mono 堆積大量字串。
        /// 超過就先丟最舊的；單份超過上限只存前段。
        /// </summary>
        public const int MaxTotalChars = 2000000;

        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _outputs = new Dictionary<string, string>();
        // List 而非 LinkedList／Queue：後兩者在 Mono 與 .NET Framework 分屬不同組件，測試會載入失敗。
        private readonly List<string> _order = new List<string>();
        private int _next;
        private long _totalChars;

        public string Save(string text)
        {
            if (text.Length > MaxTotalChars)
            {
                text = text.Substring(0, ToolOutputLimit.AlignToCharBoundary(text, MaxTotalChars)) +
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

        public bool TryGet(string id, out string text)
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
}
