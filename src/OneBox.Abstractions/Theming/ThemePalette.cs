namespace OneBox.Abstractions.Theming;

/// <summary>
/// 一套主题的调色板：颜色键（见 <see cref="ThemeKeys"/>）到色值的映射。
///
/// 色值统一为 <c>#RRGGBB</c> 或 <c>#AARRGGBB</c> 字符串——这样它能直接经 JSON
/// 在宿主与插件之间传输，两端都不需要预先认识主题目录。
/// </summary>
public sealed class ThemePalette
{
    /// <summary>主题标识（持久化在宿主设置里，改了就等同于换了主题）。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面上显示的主题名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>一句话说明（设置中心显示）。</summary>
    public string Description { get; set; } = "";

    /// <summary>是否为深色主题；系统标题栏的明暗按它切换。</summary>
    public bool IsDark { get; set; } = true;

    /// <summary>颜色键 → 色值。</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>取色值；键缺失时返回 <paramref name="fallback"/>。</summary>
    public string Get(string key, string fallback = "#000000")
        => Colors.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>逐个取出全部颜色键对应的色值（缺失的键跳过）。</summary>
    public IEnumerable<KeyValuePair<string, string>> Enumerate()
    {
        foreach (var key in ThemeKeys.All)
        {
            if (Colors.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                yield return new KeyValuePair<string, string>(key, value);
        }
    }
}
