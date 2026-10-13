using System.Globalization;
using System.Text;

namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 截图文件名生成与去重。纯逻辑，便于单元测试。
/// </summary>
public static class FileNameBuilder
{
    /// <summary>Windows 文件名非法字符。</summary>
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// 按模板生成文件名（不含扩展名）。模板里的时间占位符会被替换成当前时间。
    /// 非法字符与空白会被清理；结果为空时回退到 "Shot"。
    /// </summary>
    public static string Build(string? template, DateTime now)
    {
        var text = string.IsNullOrWhiteSpace(template) ? "Shot_{yyyyMMdd_HHmmss}" : template!;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && text.IndexOf('}', i + 1) is var end && end > i + 1)
            {
                var pattern = text[(i + 1)..end];
                // 只接受纯时间格式串，避免把任意 {xxx} 当格式串抛出异常
                if (IsTimePattern(pattern))
                {
                    sb.Append(now.ToString(pattern, CultureInfo.InvariantCulture));
                    i = end;
                    continue;
                }
            }
            sb.Append(text[i]);
        }

        var name = Sanitize(sb.ToString());
        return name.Length == 0 ? "Shot" : name;
    }

    /// <summary>
    /// 生成一个当前不存在、且不与 <paramref name="reserved"/> 冲突的完整路径。
    /// 重名时追加 " (2)"、" (3)"…。
    /// </summary>
    public static string BuildUniquePath(
        string directory, string baseName, string extension, ISet<string>? reserved = null)
    {
        if (!extension.StartsWith('.')) extension = "." + extension;

        for (int index = 1; ; index++)
        {
            var candidate = index == 1 ? baseName : $"{baseName} ({index})";
            var path = Path.Combine(directory, candidate + extension);

            var taken = File.Exists(path)
                        || (reserved is not null && reserved.Contains(path));
            if (!taken) return path;
        }
    }

    /// <summary>把格式串里的非法文件名字符（如时间格式里的 ":"）替换为 "-"。</summary>
    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(InvalidChars.Contains(ch) ? '-' : ch);
        }
        return sb.ToString().Trim().Trim('.');
    }

    /// <summary>判断花括号里的内容是否像一个 .NET 时间格式串。</summary>
    private static bool IsTimePattern(string pattern)
    {
        if (pattern.Length == 0 || pattern.Length > 32) return false;

        var hasToken = false;
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case 'y' or 'M' or 'd' or 'H' or 'h' or 'm' or 's' or 'f' or 'F' or 't':
                    hasToken = true;
                    break;
                case ':' or '/' or '-' or '_' or '.' or ' ':
                    break;
                default:
                    return false;
            }
        }
        return hasToken;
    }

    /// <summary>设置里声明的扩展名（含点）。</summary>
    public static string ExtensionFor(ShotImageFormat format) => format switch
    {
        ShotImageFormat.Jpeg => ".jpg",
        _ => ".png",
    };
}
