namespace OneBox.Plugins.Clipboard.Core;

/// <summary>剪贴板 HTML 格式（CF_HTML）的解析工具。</summary>
public static class ClipboardHtml
{
    /// <summary>
    /// 从含 "Version:... StartHTML:... EndHTML:..." 头的 CF_HTML 数据中提取实际 HTML 片段。
    /// 无头或格式异常时原样返回。
    /// </summary>
    public static string ExtractFragment(string cfHtml)
    {
        if (string.IsNullOrEmpty(cfHtml)) return string.Empty;

        int sf = cfHtml.IndexOf("StartFragment:", StringComparison.Ordinal);
        int ef = cfHtml.IndexOf("EndFragment:", StringComparison.Ordinal);
        if (sf < 0 || ef < 0) return cfHtml;

        try
        {
            int startNum = sf + "StartFragment:".Length;
            int endNum = ef + "EndFragment:".Length;
            int start = int.Parse(cfHtml.AsSpan(startNum, EndOfNumber(cfHtml, startNum) - startNum));
            int end = int.Parse(cfHtml.AsSpan(endNum, EndOfNumber(cfHtml, endNum) - endNum));
            if (start >= 0 && end > start && end <= cfHtml.Length)
                return cfHtml.Substring(start, end - start);
        }
        catch (FormatException)
        {
        }
        catch (OverflowException)
        {
        }

        return cfHtml;
    }

    private static int EndOfNumber(string s, int from)
    {
        int i = from;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return i;
    }
}
