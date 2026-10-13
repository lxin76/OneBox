using System.Net;
using System.Text.RegularExpressions;

namespace OneBox.Plugins.Clipboard.Core;

/// <summary>剪贴板文本处理工具（纯逻辑，可单测）。</summary>
public static class ClipboardText
{
    private static readonly Regex ScriptTag = new(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleTag = new(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BrTag = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockTag = new(@"</(p|div|li|tr|h[1-6]|blockquote)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CollapseSpaces = new(@"[ \t\u00A0]+", RegexOptions.Compiled);
    private static readonly Regex CollapseNewlines = new(@"\s*\n\s*", RegexOptions.Compiled);

    /// <summary>统一换行符为 \n，便于预览与去重。</summary>
    public static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>判断是否为 http(s) 链接。</summary>
    public static bool IsProbablyUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) return false;
        var trimmed = text.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>粗略将 HTML 转为纯文本（脚本/样式剔除、块级标签换行、实体解码）。</summary>
    public static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var s = ScriptTag.Replace(html, " ");
        s = StyleTag.Replace(s, " ");
        s = BrTag.Replace(s, "\n");
        s = BlockTag.Replace(s, "\n");
        s = AnyTag.Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = CollapseSpaces.Replace(s, " ");
        s = CollapseNewlines.Replace(s, "\n");
        return s.Trim();
    }
}
