using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>
/// 贴图窗口的置顶保活。
/// 单纯设置 WS_EX_TOPMOST 不够：其他程序切到前台、或它自己也置顶时，仍会盖住贴图。
/// 这里在窗口创建时插入为 HWND_TOPMOST，并在低频计时器里重申置顶，确保贴图始终在最上层。
///
/// 注意「重申」不能反复用 HWND_TOPMOST：那个参数会把窗口插到整个置顶组的最前面。
/// 多张贴图互相遮挡时，各自周期性插队会形成循环争抢（画面闪烁、谁也无法稳定在上）。
/// 因此已经处于置顶状态的窗口只用 SWP_NOZORDER 确认，不改动 Z 序；
/// 多张贴图之间的层叠关系交给用户手动拖拽决定，程序不再干预。
/// </summary>
public static class TopmostKeeper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>把窗口设为置顶并插入到最顶层，不抢焦点。属于显式意图，允许改变 Z 序。</summary>
    public static void AssertTopmost(Window window) => AssertTopmost(HandleOf(window));

    /// <summary>
    /// 把窗口句柄设为置顶，不抢焦点，且属于显式意图（允许插到置顶组最前面）。
    /// </summary>
    public static void AssertTopmost(IntPtr hwnd) => ApplyTopmost(hwnd, explicitRequest: true);

    /// <summary>
    /// 周期性保活：确认窗口仍在置顶组内，但<b>不</b>改变组内层叠次序。
    /// 多张贴图互相遮挡时，各自周期插队会导致循环争抢，因此保活只做无 Z 序变更的确认。
    /// </summary>
    public static void ReassertTopmost(Window window) => ReassertTopmost(HandleOf(window));

    /// <summary>周期性保活：确认置顶但不改变 Z 序。</summary>
    public static void ReassertTopmost(IntPtr hwnd) => ApplyTopmost(hwnd, explicitRequest: false);

    private static void ApplyTopmost(IntPtr hwnd, bool explicitRequest)
    {
        if (hwnd == IntPtr.Zero) return;

        var alreadyTopmost = IsTopmost(hwnd);
        if (!TopmostPolicy.ShouldReorder(alreadyTopmost, explicitRequest))
        {
            // 已在置顶组内且只是保活：用 SWP_NOZORDER 重申样式，保持现有层叠次序
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
            return;
        }

        // 需要插到最前面：hwndInsertAfter = HWND_TOPMOST
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>窗口当前是否处于置顶状态。</summary>
    public static bool IsTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
    }

    /// <summary>取消置顶。</summary>
    public static void ClearTopmost(Window window)
    {
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_TOPMOST);
        SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>设置“不显示在任务栏、点击不激活”的贴图风格。</summary>
    /// <param name="noActivate">
    /// 是否保持 WS_EX_NOACTIVATE。文字标注输入期间需要临时允许激活，
    /// 此时传 false，否则置顶状态变化会把键盘焦点重新收走。
    /// </param>
    public static void ApplyPasteStyle(Window window, bool topmost, bool noActivate = true)
    {
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW;
        style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        style = topmost ? style | WS_EX_TOPMOST : style & ~WS_EX_TOPMOST;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);

        // 这里必须无条件插队（而非走 AssertTopmost）：刚改完样式位后 IsTopmost 已为真，
        // 而 AssertTopmost 对已置顶窗口只做无 Z 序确认，新贴图会停在置顶组末尾、被已有贴图盖住。
        // 这是用户显式开启置顶（或新建贴图）的时刻，属于明确的层叠意图，允许改变次序。
        if (topmost) SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else ClearTopmost(window);
    }

    /// <summary>让窗口可以接受键盘焦点（重命名、快捷键操作时临时用）。</summary>
    public static void SetNoActivate(Window window, bool enabled)
    {
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style = enabled ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    /// <summary>
    /// 把窗口提到置顶组的最前面并显示，仍不激活。
    /// 仅用于用户主动操作（如点击贴图）——这属于明确意图，允许改变层叠次序；
    /// 周期性的置顶保活绝不能走这里，否则多张贴图会互相争抢。
    /// </summary>
    public static void BringToFrontWithoutActivating(Window window)
    {
        var hwnd = HandleOf(window);
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static IntPtr HandleOf(Window window)
    {
        try
        {
            return new WindowInteropHelper(window).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
