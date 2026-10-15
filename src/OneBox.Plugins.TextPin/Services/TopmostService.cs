using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>置顶文本框的窗口层级与扩展样式控制（置顶 / 不夺焦点 / 不显示任务栏）。</summary>
public static class TopmostService
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);    // HWND_TOPMOST
    private static readonly IntPtr HwndNoTopmost = new(-2);  // HWND_NOTOPMOST
    private static readonly IntPtr HwndTop = new(0);         // HWND_TOP（普通层级最上层）

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>当前前台窗口句柄。</summary>
    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>把指定窗口设为前台（本进程是前台时允许交接）。</summary>
    public static void ActivateWindow(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>
    /// 把窗口提升到普通窗口层级的最高层（不激活、不抢焦点）。
    /// SetWindowPos(HWND_TOP) 不受前台窗口限制，比 SetForegroundWindow 更可靠。
    /// </summary>
    public static void BringToFront(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>切换 WS_EX_NOACTIVATE（是否允许窗口被点击激活）。</summary>
    public static void SetNoActivate(Window window, bool on)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = on ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    /// <summary>临时允许/恢复激活（输入文本需要键盘焦点时临时允许）。不改变置顶状态。</summary>
    public static void AllowActivation(Window window, bool allow)
        => SetNoActivate(window, on: !allow);

    /// <summary>设置窗口扩展样式：不激活 + 不显示任务栏，并按需置顶。</summary>
    public static void MakeFloating(Window window, bool topmost, bool noActivate)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW;
        style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        if (topmost) style |= WS_EX_TOPMOST;
        else style &= ~WS_EX_TOPMOST;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);

        if (topmost) AssertTopmost(hwnd);
        else SetNotTopmost(hwnd);
    }

    /// <summary>重申置顶，防止被其他置顶窗口盖住。建议定时调用。</summary>
    public static void AssertTopmost(Window window) => AssertTopmost(new WindowInteropHelper(window).Handle);

    public static void AssertTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>切换置顶状态。</summary>
    public static void SetTopmost(Window window, bool on)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = on ? style | WS_EX_TOPMOST : style & ~WS_EX_TOPMOST;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);

        if (on) AssertTopmost(hwnd);
        else SetNotTopmost(hwnd);
    }

    private static void SetNotTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
