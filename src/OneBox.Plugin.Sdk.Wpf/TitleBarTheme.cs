using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OneBox.Plugin.Sdk.Wpf;

/// <summary>
/// 让插件窗口的系统标题栏跟随主题明暗（与宿主里的 DarkTitleBar 是同一套 DWM 调用）。
/// 不接管窗口框架：拖动、最大化、贴边分屏、阴影仍全部由系统负责。
/// </summary>
internal static class TitleBarTheme
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE：Win10 20H1（build 18985）起为 20，更早的预览版为 19。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>按主题把标题栏画成深色或浅色；系统不支持时静默跳过（保持系统默认样式）。</summary>
    internal static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            // 旧版本 Windows 用的是另一个属性号，再试一次；仍失败则保持系统默认
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
    }
}
