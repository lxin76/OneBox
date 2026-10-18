using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using OneBox.Abstractions;
using OneBox.Abstractions.Theming;

namespace OneBox.Tests;

/// <summary>
/// 主题目录的契约测试。
///
/// "多几个主题"看起来只是多几组颜色，但真正会伤人的是两件事：
/// 某个主题漏了一个颜色键（界面上出现一块没上色的默认灰），
/// 以及文字与底色的对比度不够（换个主题就看不清字）。
/// 这两件事都能在纯计算里钉死，不必真的把窗口渲染出来。
///
/// 新增主题时这些断言会自动覆盖它：漏键、色值写错、对比度不达标都会直接失败。
/// 颜色键本身是宿主与插件共用的契约，所以也一并测了它经 IPC 传输的往返。
/// </summary>
public class ThemeCatalogTests
{
    [Fact]
    public void 内置主题非空且标识唯一()
    {
        Assert.NotEmpty(ThemeCatalog.All);
        Assert.Equal(
            ThemeCatalog.All.Count,
            ThemeCatalog.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ThemeCatalog.All.Count,
            ThemeCatalog.All.Select(t => t.DisplayName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 默认主题是目录里的第一个()
    {
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Default.Id);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.All[0].Id);
    }

    [Fact]
    public void 每个主题都给出了全部颜色键()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var missing = ThemeKeys.All.Where(key => !theme.Colors.ContainsKey(key)).ToList();
            Assert.True(missing.Count == 0, $"{theme.Id} 缺少颜色键：{string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void 每个主题的色值都能解析成颜色()
    {
        foreach (var theme in ThemeCatalog.All)
        foreach (var (key, value) in theme.Enumerate())
        {
            var error = Record.Exception(() => Parse(value));
            Assert.True(error is null, $"{theme.Id}.{key} 的色值 {value} 无法解析：{error?.Message}");
        }
    }

    [Fact]
    public void 每个主题都有名称与说明()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(theme.DisplayName), $"{theme.Id} 缺少主题名");
            Assert.False(string.IsNullOrWhiteSpace(theme.Description), $"{theme.Id} 缺少说明");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dark-")]
    [InlineData("不存在的主题")]
    public void 未知主题回退默认主题(string? id)
        => Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Get(id).Id);

    [Fact]
    public void 主题标识不区分大小写且忽略首尾空白()
    {
        Assert.Equal("light", ThemeCatalog.Get("LIGHT").Id);
        Assert.Equal("light", ThemeCatalog.Get("  light  ").Id);
        Assert.True(ThemeCatalog.IsKnown("Graphite"));
        Assert.False(ThemeCatalog.IsKnown("graphite2"));
    }

    /// <summary>
    /// 文字与底色的对比度：正文按 WCAG AA 的 4.5:1，最淡的水印文字放宽到 3:1。
    /// 这几组配对就是界面上真实存在的组合（正文/标题/输入框/说明/强调文字/状态色）。
    /// </summary>
    [Fact]
    public void 文字与底色的对比度达到无障碍标准()
    {
        (string Text, string Backdrop, double Min)[] pairs =
        [
            (ThemeKeys.Text, ThemeKeys.Window, 4.5),
            (ThemeKeys.Text, ThemeKeys.Surface, 4.5),
            (ThemeKeys.Text, ThemeKeys.Card, 4.5),
            (ThemeKeys.TextStrong, ThemeKeys.Window, 4.5),
            (ThemeKeys.TextStrong, ThemeKeys.Surface, 4.5),
            (ThemeKeys.TextStrong, ThemeKeys.Card, 4.5),
            (ThemeKeys.TextInput, ThemeKeys.InputBg, 4.5),
            (ThemeKeys.TextInput, ThemeKeys.Card, 4.5),
            (ThemeKeys.TextMuted, ThemeKeys.Window, 4.5),
            (ThemeKeys.TextMuted, ThemeKeys.Surface, 4.5),
            (ThemeKeys.TextMuted, ThemeKeys.Card, 4.5),
            (ThemeKeys.TextFaint, ThemeKeys.Card, 3.0),
            (ThemeKeys.AccentText, ThemeKeys.AccentSoft, 4.5),
            (ThemeKeys.TextOnAccent, ThemeKeys.Accent, 4.5),
            (ThemeKeys.Ok, ThemeKeys.Card, 4.5),
            (ThemeKeys.Warn, ThemeKeys.Card, 4.5),
            (ThemeKeys.Error, ThemeKeys.Card, 4.5),
        ];

        foreach (var theme in ThemeCatalog.All)
        foreach (var (textKey, backdropKey, min) in pairs)
        {
            var ratio = ContrastRatio(Parse(theme.Get(textKey)), Parse(theme.Get(backdropKey)));
            Assert.True(
                ratio >= min,
                $"{theme.Id}：{textKey} 配 {backdropKey} 的对比度只有 {ratio:0.00}:1，低于要求的 {min}:1");
        }
    }

    /// <summary>颜色键是宿主与插件共用的契约，调色板必须能原样经 IPC 传到插件进程。</summary>
    [Fact]
    public void 调色板经IPC序列化后颜色一个不少()
    {
        var original = ThemeCatalog.Get("sepia");
        var payload = new ThemeChangedPayload { Theme = original };

        var restored = JsonSerializer
            .Deserialize<ThemeChangedPayload>(JsonSerializer.Serialize(payload, Protocol.Json), Protocol.Json)!;

        Assert.Equal(original.Id, restored.Theme.Id);
        Assert.Equal(original.IsDark, restored.Theme.IsDark);
        foreach (var key in ThemeKeys.All)
            Assert.Equal(original.Get(key), restored.Theme.Get(key));
    }

    /// <summary>颜色键的 WPF 资源名（XAML 里写作 {DynamicResource Theme.Card}）。</summary>
    [Fact]
    public void 颜色键的资源名带统一前缀且不重复()
    {
        Assert.Equal(ThemeKeys.All.Count, ThemeKeys.All.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in ThemeKeys.All)
        {
            Assert.Equal("Theme." + key, ThemeKeys.ResourceKey(key));
            Assert.False(string.IsNullOrWhiteSpace(key));
        }
    }

    /// <summary>
    /// XAML 里引用主题键是写字符串（<c>{DynamicResource Theme.Card}</c>），拼错了不会报错——
    /// WPF 只会静默地退回默认颜色，界面上就是一块没上色的灰。
    /// 这里把宿主与各插件 XAML 里引用到的键逐个对照 <see cref="ThemeKeys"/>，把这种错挡在测试里。
    /// </summary>
    [Fact]
    public void XAML引用的主题键都在颜色键表里()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var xamlFiles = Directory
            .GetFiles(sourceRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(string.Concat(Path.DirectorySeparatorChar, "obj", Path.DirectorySeparatorChar))
                        && !path.Contains(string.Concat(Path.DirectorySeparatorChar, "bin", Path.DirectorySeparatorChar)))
            .ToList();

        Assert.NotEmpty(xamlFiles);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in xamlFiles)
        foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{DynamicResource\s+Theme\.([A-Za-z0-9_]+)\s*\}"))
            used.Add(match.Groups[1].Value);

        Assert.NotEmpty(used);

        var unknown = used
            .Where(key => !ThemeKeys.All.Contains(key, StringComparer.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0, "XAML 引用了未定义的主题键：" + string.Join(", ", unknown));
    }

    /// <summary>测试始终在源码树里跑；从输出目录向上找到解决方案文件即项目根。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OneBox.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, $"从 {AppContext.BaseDirectory} 向上没找到 OneBox.slnx");
        return directory!.FullName;
    }

    private static Color Parse(string value)
        => (Color)ColorConverter.ConvertFromString(value)!;

    /// <summary>WCAG 相对亮度。</summary>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG 对比度（1:1 ~ 21:1）。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        var first = Luminance(a);
        var second = Luminance(b);
        var (lighter, darker) = first >= second ? (first, second) : (second, first);
        return (lighter + 0.05) / (darker + 0.05);
    }
}
