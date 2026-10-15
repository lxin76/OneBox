namespace OneBox.Plugins.TextPin.Core;

/// <summary>文本统计结果。空文本与只有空白字符的文本统计一致（都是 0 行 0 字）。</summary>
/// <param name="Characters">字符数（含换行与空格）。</param>
/// <param name="Lines">行数；空文本记 0 行。</param>
/// <param name="Bytes">UTF-8 编码后的字节数，用来提示"内容是否过大"。</param>
public readonly record struct TextStatistics(int Characters, int Lines, long Bytes)
{
    /// <summary>是否没有内容。</summary>
    public bool IsEmpty => Characters == 0;

    /// <summary>人类可读的一行摘要，如 "12 行 · 340 字 · 512 B"。</summary>
    public string Describe()
    {
        if (IsEmpty) return "空";

        var parts = new List<string>(3);
        if (Lines > 1) parts.Add($"{Lines} 行");
        parts.Add($"{Characters} 字");
        parts.Add(FormatBytes(Bytes));
        return string.Join(" · ", parts);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}

/// <summary>文本统计（纯逻辑，可单测）。</summary>
public static class TextStats
{
    /// <summary>
    /// 统计字符数、行数与 UTF-8 字节数。
    /// 空文本记 0 行——否则空窗口会显示"1 行"，看起来像有一行空内容。
    /// </summary>
    public static TextStatistics Measure(string? text)
    {
        var value = text ?? "";
        if (value.Length == 0) return new TextStatistics(0, 0, 0);

        var lines = 1;
        foreach (var ch in value)
            if (ch == '\n') lines++;

        var bytes = System.Text.Encoding.UTF8.GetByteCount(value);
        return new TextStatistics(value.Length, lines, bytes);
    }
}
