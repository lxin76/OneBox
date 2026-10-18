using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OneBox.Abstractions.Theming;
using OneBox.Plugin.Sdk;

namespace OneBox.Plugin.Sdk.Wpf;

/// <summary>
/// 插件侧的主题应用器：把宿主下发的调色板刷进本进程的 WPF 应用资源，
/// 于是插件窗口里写作 <c>{DynamicResource Theme.Card}</c> 的地方会跟着宿主一起换色。
///
/// 用法（插件主体开头一次即可）：
/// <code>
/// PluginTheme.Attach(context);
/// </code>
/// 之后每创建一个带系统标题栏的窗口，加一行 <c>PluginTheme.ApplyToWindow(window);</c>，
/// 标题栏的明暗也会跟随主题。
///
/// 之所以刷到应用级资源而不是逐窗设置：WPF 的 DynamicResource 是延迟解析的，
/// 资源放在 <see cref="Application.Resources"/> 后，之后新开的窗口无需任何注册
/// 就自动带上当前主题；换主题时替换资源值，已打开的窗口也会立即重绘。
/// 宿主没在下发里给出主题（例如插件被单独调试）时，退回内置的默认深色主题。
/// </summary>
public static class PluginTheme
{
    private static readonly object Sync = new();
    private static readonly List<Window> TrackedWindows = [];
    private static ThemePalette _current = ThemeCatalog.Default;
    private static bool _attached;

    /// <summary>当前生效的主题；未 <see cref="Attach"/> 时为内置默认主题。</summary>
    public static ThemePalette Current
    {
        get { lock (Sync) return _current; }
    }

    /// <summary>
    /// 开始跟随宿主主题：立刻应用当前主题，并在宿主切换主题时自动更新。
    /// 应在插件主体开始处调用一次（此时插件的 Application 已创建）。
    /// </summary>
    public static void Attach(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (Sync)
        {
            if (_attached) return;
            _attached = true;
            _current = context.CurrentTheme;
        }

        context.ThemeChanged += Apply;
        Apply(Current);
    }

    /// <summary>应用一套调色板（宿主下发之外的手动指定也走这里）。</summary>
    public static void Apply(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        lock (Sync) _current = palette;
        WriteResources(palette);

        // 已经打开的窗口：系统标题栏的明暗要跟着换
        Window[] windows;
        lock (Sync) windows = [.. TrackedWindows];
        foreach (var window in windows) TitleBarTheme.Apply(window, palette.IsDark);
    }

    /// <summary>
    /// 让一个窗口跟随主题：设置系统标题栏的明暗，之后主题变化时自动更新。
    /// 窗口关闭后自动停止跟踪，不需要手动注销。
    /// </summary>
    public static void ApplyToWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (Sync)
        {
            if (TrackedWindows.Contains(window)) return;
            TrackedWindows.Add(window);
        }

        window.Closed += (_, _) =>
        {
            lock (Sync) TrackedWindows.Remove(window);
        };

        // 已经拿到句柄的窗口直接设置；否则等 SourceInitialized（Show 时才有句柄）
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            TitleBarTheme.Apply(window, Current.IsDark);
        else
            window.SourceInitialized += (_, _) => TitleBarTheme.Apply(window, Current.IsDark);
    }

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

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>；无法解析时返回 false，交给调用方保留原值。</summary>
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
            // 色值写错不该让插件启动失败：跳过该键，界面沿用上一次的颜色
        }

        return false;
    }
}
