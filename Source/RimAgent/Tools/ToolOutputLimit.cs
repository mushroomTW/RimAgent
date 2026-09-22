using System.Globalization;

namespace RimAgent.Tools
{
    /// <summary>
    /// 工具結果送進模型前的硬上限：留頭、留尾、丟中間，並在接縫處說明省略了多少字。
    /// 觀察類工具（例如 50x50 的格子摘要）可以回很長的文字，沒有上限時一次呼叫就能吃掉整個
    /// 上下文視窗，之後的歷史壓縮也救不回來（被折疊的是舊訊息，不是這一則）。
    /// 頭尾都留：多數工具結果開頭是摘要、結尾是總計或失敗清單，兩端都有訊息。
    /// </summary>
    internal static class ToolOutputLimit
    {
        /// <summary>單次工具結果保留的字元數（約 2000 token）：一輪可能呼叫好幾個工具，還要留空間給歷史與回覆。</summary>
        public const int MaxResultChars = 8000;

        public static string Apply(string text)
        {
            return Apply(text, MaxResultChars);
        }

        /// <summary>保留的內容以 <paramref name="maxChars"/> 為上限；另加一行固定長度的省略說明。</summary>
        internal static string Apply(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;

            int head = maxChars / 2;
            int tailStart = text.Length - (maxChars - head);
            // 切點不能落在代理對中間：留半個字元會讓請求內文的 UTF-16 轉碼失敗或變亂碼。
            if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
            if (tailStart < text.Length && char.IsLowSurrogate(text[tailStart])) tailStart++;
            int omitted = tailStart - head;
            return text.Substring(0, head)
                   + "\n… [" + omitted.ToString(CultureInfo.InvariantCulture) + " characters omitted; tool output is capped] …\n"
                   + text.Substring(tailStart);
        }
    }
}
