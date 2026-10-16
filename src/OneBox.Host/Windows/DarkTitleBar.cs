using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OneBox.Host.Windows;

/// <summary>
/// 让系统标题栏跟随主题明暗。
///
/// 界面本身是深色卡片时，系统标题栏默认的浅色接在一起会明显割裂；换成浅色主题同理。
/// 这里不接管窗口框架，只请求 DWM 把标题栏按当前主题绘制——
/// 拖动、最大化、贴边分屏、阴影全部仍由系统负责，没有自己维护的窗口尺寸逻辑。
/// </summary>
internal static class DarkTitleBar
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE：Win10 20H1（build 18985）起为 20，更早的预览版为 19。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>按主题把窗口标题栏画成深色或浅色。系统不支持时静默跳过（保持系统默认样式）。</summary>
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
