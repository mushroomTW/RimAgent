using System.Globalization;

namespace RimAgent.Tools;

/// <summary>
/// 觀察類工具（例如 50x50 的格子摘要）可以回很長的文字，沒有上限時一次呼叫就能吃掉整個
/// 上下文視窗，之後的歷史壓縮也救不回來（被折疊的是舊訊息，不是這一則）。
/// 頭尾都留：多數工具結果開頭是摘要、結尾是總計或失敗清單，兩端都有訊息。
/// </summary>
internal static class ToolOutputLimit
{
    /// <summary>單次工具結果保留的字元數（約 2000 token）：一輪可能呼叫好幾個工具，還要留空間給歷史與回覆。</summary>
    public const int MaxResultChars = 8000;

    public static string Apply(string? text, int maxChars = MaxResultChars, ToolOutputStore? store = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxChars) return text;

        int head = AlignToCharBoundary(text, maxChars / 2);
        int tailStart = AlignToCharBoundary(text, text.Length - (maxChars - maxChars / 2));
        string omitted = (tailStart - head).ToString(CultureInfo.InvariantCulture);
        string note = store == null
            ? " characters omitted; tool output is capped"
            : " characters omitted; full output saved as " + store.Save(text) +
              ", read the rest with read_tool_output from offset " + head.ToString(CultureInfo.InvariantCulture) + " or search it with find";
        return text.Substring(0, head)
               + "\n… [" + omitted + note + "] …\n"
               + text.Substring(tailStart);
    }

    /// <summary>
    /// 切點不能落在代理對中間：留半個字元會讓請求內文的 UTF-16 轉碼失敗或變亂碼。
    /// 落在中間就往前退一格，兩側都是完整字元。
    /// </summary>
    internal static int AlignToCharBoundary(string text, int index)
    {
        return index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index])
            ? index - 1
            : index;
    }
}
