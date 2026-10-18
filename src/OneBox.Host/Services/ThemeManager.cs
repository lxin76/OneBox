using System.Windows;
using System.Windows.Media;
using OneBox.Abstractions.Theming;
using OneBox.Host.Windows;

namespace OneBox.Host.Services;

/// <summary>
/// 宿主侧主题管理：把主题调色板刷进 WPF 应用资源，并通知各方刷新。
///
/// 界面里所有颜色都写成 <c>{DynamicResource Theme.Xxx}</c>（键见 <see cref="ThemeKeys"/>），
/// 因此这里只要替换资源值，已经打开的窗口就会自动重绘——不需要逐个窗口去改属性。
/// 系统标题栏不认 WPF 资源，由 <see cref="Changed"/> 的订阅方（App）统一重刷。
///
/// 主题对插件同样生效：宿主把整份调色板通过 IPC 下发给每个插件进程
/// （握手时给一次，之后切换时再广播），插件侧的 PluginTheme 用同一组键名落地。
/// </summary>
public static class ThemeManager
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.Ordinal);
    private static ThemePalette _current = ThemeCatalog.Default;

    /// <summary>当前主题。</summary>
    public static ThemePalette Current
    {
        get { lock (Sync) return _current; }
    }

    /// <summary>主题已变更（参数为新主题）。订阅方负责刷新系统标题栏、通知插件。</summary>
    public static event Action<ThemePalette>? Changed;

    /// <summary>应用主题；重复应用同一主题不会重复触发 <see cref="Changed"/>。</summary>
    public static void Apply(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        lock (Sync)
        {
            if (ReferenceEquals(_current, palette) || _current.Id == palette.Id) return;
            _current = palette;
            BrushCache.Clear();
        }

        WriteResources(palette);
        Changed?.Invoke(palette);
    }

    /// <summary>按 id 应用主题；未知 id 回退默认主题（不抛异常）。</summary>
    public static void Apply(string? themeId) => Apply(ThemeCatalog.Get(themeId));

    /// <summary>
    /// 启动时应用主题：与 <see cref="Apply(string?)"/> 相同，但即使与当前值相同也
    /// 强制写一遍资源。窗口在启动阶段就要用到这些颜色，不能因为"没变化"而留下空资源。
    /// </summary>
    public static void Initialize(string? themeId)
    {
        var palette = ThemeCatalog.Get(themeId);
        lock (Sync)
        {
            _current = palette;
            BrushCache.Clear();
        }

        WriteResources(palette);
        Changed?.Invoke(palette);
    }

    /// <summary>按颜色键取一枚画刷（供 C# 代码构建界面时使用，例如设置中心与卸载对话框）。</summary>
    public static Brush Brush(string key)
    {
        lock (Sync)
        {
            if (BrushCache.TryGetValue(key, out var cached)) return cached;
            var brush = CreateBrush(_current.Get(key));
            BrushCache[key] = brush;
            return brush;
        }
    }

    /// <summary>
    /// 按**指定**主题（而非当前主题）取画刷：设置中心的主题预览要在当前主题下
    /// 展示其他主题的配色，因此不能走 <see cref="Brush"/>。
    /// </summary>
    public static Brush BrushFor(ThemePalette palette, string key)
        => TryParseColor(palette.Get(key), out var color) ? Frozen(color) : Brushes.Transparent;

    private static void WriteResources(ThemePalette palette)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        // 整体覆盖而不是逐键合并：换主题时不做增量，避免残留上一个主题的颜色
        var stale = resources.Keys
            .Cast<object>()
            .OfType<string>()
            .Where(key => key.StartsWith(ThemeKeys.ResourcePrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var key in stale) resources.Remove(key);

        foreach (var (key, value) in palette.Enumerate())
        {
            if (!TryParseColor(value, out var color)) continue;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[ThemeKeys.ResourceKey(key)] = brush;
        }
    }

    private static Brush CreateBrush(string value)
        => TryParseColor(value, out var color) ? Frozen(color) : Brushes.Transparent;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>；无法解析时返回 false，调用方保留原值。</summary>
    internal static bool TryParseColor(string value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            if (ColorConverter.ConvertFromString(value.Trim()) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
            // 色值写错不该让宿主启动失败：跳过该键
        }

        return false;
    }

    /// <summary>把当前主题的明暗应用到所有已打开的宿主窗口的系统标题栏。</summary>
    public static void RefreshTitleBars()
    {
        if (Application.Current is not { } app) return;
        var dark = Current.IsDark;
        foreach (Window window in app.Windows)
        {
            try
            {
                DarkTitleBar.Apply(window, dark);
            }
            catch
            {
                // 窗口正在关闭时取句柄可能失败：标题栏不是关键功能，忽略
            }
        }
    }
}
