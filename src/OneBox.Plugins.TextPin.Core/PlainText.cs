using System.Text;

namespace OneBox.Plugins.TextPin.Core;

/// <summary>
/// 纯文本处理的纯逻辑（不依赖 WPF，可单测）。
/// 插件的全部内容都是"纯文本"，这里统一换行符、截断与统计口径，
/// 避免界面上、磁盘上、剪贴板上三处的换行与长度算法各写一遍。
/// </summary>
public static class PlainText
{
    /// <summary>统一换行符为 \n。Windows 剪贴板常带 \r\n，混用会让行数统计与去重不一致。</summary>
    public static string NormalizeNewlines(string? text)
        => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// 截断到 <paramref name="maxLength"/> 个字符。超出时在末尾加一行提示，
    /// 让用户知道内容被截断了，而不是以为插件吃掉了数据。
    /// </summary>
    public static string Truncate(string? text, int maxLength)
    {
        var value = text ?? "";
        if (maxLength <= 0 || value.Length <= maxLength) return value;

        const string notice = "\n…（内容过长，已截断）";
        // 提示本身也要占长度，否则截断后的结果反而比上限还长
        var keep = Math.Max(0, maxLength - notice.Length);
        return keep >= value.Length ? value : value[..keep] + notice;
    }

    /// <summary>是否为空或只有空白字符。</summary>
    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>把文本整理成可安全显示/保存的形式：统一换行 + 按上限截断。</summary>
    public static string Sanitize(string? text, int maxLength)
        => Truncate(NormalizeNewlines(text), maxLength);

    /// <summary>
    /// 取一段单行预览：换行折成空格、去掉首尾空白、超长加省略号。
    /// 用于窗口标题、托盘提示与状态栏。
    /// </summary>
    public static string Preview(string? text, int maxLength = 60)
    {
        var oneLine = NormalizeNewlines(text).Replace('\n', ' ').Trim();
        // 连续空白折成一个，避免预览里出现一长串空隙
        var sb = new StringBuilder(oneLine.Length);
        var lastWasSpace = false;
        foreach (var ch in oneLine)
        {
            var isSpace = ch is ' ' or '\t';
            if (isSpace && lastWasSpace) continue;
            sb.Append(isSpace ? ' ' : ch);
            lastWasSpace = isSpace;
        }

        var collapsed = sb.ToString();
        if (maxLength <= 0 || collapsed.Length <= maxLength) return collapsed;
        return collapsed[..maxLength] + "…";
    }
}
