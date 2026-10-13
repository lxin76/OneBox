using System.Text;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>一个可被取景的顶层窗口。</summary>
public sealed record WindowTarget(System.Drawing.Rectangle Bounds, string Title, IntPtr Handle);

/// <summary>枚举桌面上的可见顶层窗口，用于区域截图的窗口吸附。</summary>
public static class WindowEnumerator
{
    /// <summary>列出所有值得取景的窗口（可见、未最小化、有实际尺寸、未被遮罩）。</summary>
    public static IReadOnlyList<WindowTarget> List()
    {
        var screen = ScreenCapture.GetVirtualScreenBounds()
                     ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var result = new List<WindowTarget>();
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
                if (IsCloaked(hwnd)) return true;

                var bounds = GetBounds(hwnd);
                if (bounds is not { } rect) return true;

                // 滤掉没有实际面积的窗口（工具提示、0 尺寸的辅助窗口）
                if (rect.Width < 24 || rect.Height < 24) return true;
                // 滤掉与虚拟屏幕完全不相交的窗口
                if (!rect.IntersectsWith(screen)) return true;

                var title = GetTitle(hwnd);
                // 无标题的多半是系统辅助窗口，跳过；有标题的一律保留（含浏览器、编辑器等）
                if (title.Length == 0) return true;

                result.Add(new WindowTarget(rect, title, hwnd));
            }
            catch
            {
                // 单个窗口读取失败不影响整体枚举
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>按取景范围由小到大的顺序排（命中测试时优先取最小的那个窗口）。</summary>
    public static IReadOnlyList<WindowTarget> ListSmallestFirst()
        => List()
            .OrderBy(t => (long)t.Bounds.Width * t.Bounds.Height)
            .ToList();

    /// <summary>取窗口的实际边界：优先用 DWM 的扩展框（不含投影），失败时退回 GetWindowRect。</summary>
    public static System.Drawing.Rectangle? GetBounds(IntPtr hwnd)
    {
        const int size = 16; // sizeof(RECT)
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var dwm, size) == 0)
        {
            var rect = System.Drawing.Rectangle.FromLTRB(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
            if (rect.Width > 0 && rect.Height > 0) return rect;
        }

        return Native.GetWindowRect(hwnd, out var r)
            ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : null;
    }

    private static bool IsCloaked(IntPtr hwnd)
        => Native.DwmGetWindowAttributeInt(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
           && cloaked != 0;

    private static string GetTitle(IntPtr hwnd)
    {
        var length = Native.GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(length + 1);
        Native.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
