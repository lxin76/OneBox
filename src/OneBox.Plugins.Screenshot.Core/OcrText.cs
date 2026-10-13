using System.Text;

namespace OneBox.Plugins.Screenshot.Core;

/// <summary>OCR 结果的文本整理。纯逻辑，便于单元测试。</summary>
public static class OcrText
{
    /// <summary>判断字符是否为 CJK（中日韩）字符。</summary>
    public static bool IsCjk(char ch) => ch switch
    {
        >= '\u3040' and <= '\u30FF' => true, // 平假名/片假名
        >= '\u3400' and <= '\u4DBF' => true, // 扩展 A
        >= '\u4E00' and <= '\u9FFF' => true, // 基本区
        >= '\uF900' and <= '\uFAFF' => true, // 兼容表意
        >= '\uAC00' and <= '\uD7AF' => true, // 谚文
        _ => false,
    };

    /// <summary>
    /// OCR 引擎按“词”输出，中文词之间也会插空格（"截 图 识 别"）。
    /// 这里删掉两个 CJK 字符之间的空格，保留中英文之间的空格。
    /// </summary>
    public static string StripCjkSpaces(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == ' ' && sb.Length > 0)
            {
                // 找下一个非空格字符
                int j = i;
                while (j < text.Length && text[j] == ' ') j++;
                if (j < text.Length && IsCjk(text[j]) && IsCjk(sb[^1]))
                {
                    i = j - 1; // 跳过这些空格
                    continue;
                }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>合并多行 OCR 结果，去掉首尾空白与多余空行。</summary>
    public static string Normalize(string? text, bool stripCjkSpaces)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                // 折叠连续空行
                if (builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
                continue;
            }
            if (builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
            builder.Append(stripCjkSpaces ? StripCjkSpaces(trimmed) : trimmed);
        }
        return builder.ToString().Trim('\n');
    }
}
