using System.Text;

namespace RimAgent.UI;

internal static class ReplyMarkdown
{
    /// <summary>行首的 &gt; 是引用標記，要留給 Markdown 解析；其餘角括號改成全形，避免被當成 rich text 標籤。</summary>
    public static string EscapeAngleBrackets(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return string.Empty;
        var sb = new StringBuilder(reply.Length);
        bool inLeadingQuote = true;
        foreach (char ch in reply)
        {
            if (ch == '\n' || ch == '\r')
            {
                inLeadingQuote = true;
                sb.Append(ch);
            }
            else if (ch == '<')
            {
                inLeadingQuote = false;
                sb.Append('＜');
            }
            else if (ch == '>')
            {
                sb.Append(inLeadingQuote ? '>' : '＞');
            }
            else
            {
                if (ch != ' ' && ch != '\t') inLeadingQuote = false;
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
