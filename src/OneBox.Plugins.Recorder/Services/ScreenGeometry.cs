using System.Windows;
using System.Windows.Interop;
using OneBox.Plugins.Recorder.Core;

namespace OneBox.Plugins.Recorder.Services;

/// <summary>一块显示器（物理像素边界）。</summary>
public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    System.Drawing.Rectangle Bounds,
    System.Drawing.Rectangle WorkArea,
    bool IsPrimary);

/// <summary>屏幕几何：显示器枚举、光标所在屏幕、控制条定位。</summary>
public static class ScreenGeometry
{
    /// <summary>枚举所有显示器。顺序与 Win32 枚举顺序一致，用于和录屏库的显示器列表按位置对齐。</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        Native.MonitorEnumProc callback = (IntPtr handle, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>(),
                szDevice = "",
            };

            if (!Native.GetMonitorInfo(handle, ref info)) return true;

            var bounds = new System.Drawing.Rectangle(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                Math.Max(1, info.rcMonitor.Width),
                Math.Max(1, info.rcMonitor.Height));
            var work = new System.Drawing.Rectangle(
                info.rcWork.Left,
                info.rcWork.Top,
                Math.Max(1, info.rcWork.Width),
                Math.Max(1, info.rcWork.Height));

            monitors.Add(new MonitorInfo(
                monitors.Count,
                info.szDevice ?? "",
                bounds,
                work,
                (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));

            return true;
        };

        try
        {
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败时退回虚拟屏幕，至少还能录到一块区域
        }

        if (monitors.Count == 0)
        {
            var bounds = GetVirtualScreenBounds();
            monitors.Add(new MonitorInfo(0, "", bounds, bounds, true));
        }

        return monitors;
    }

    /// <summary>虚拟屏幕边界（所有显示器合并，物理像素，可能为负）。</summary>
    public static System.Drawing.Rectangle GetVirtualScreenBounds()
    {
        var x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0) return new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new System.Drawing.Rectangle(x, y, width, height);
    }

    public static System.Drawing.Point GetCursorPosition()
        => Native.GetCursorPos(out var p) ? new System.Drawing.Point(p.X, p.Y) : System.Drawing.Point.Empty;

    /// <summary>鼠标当前所在的显示器；取不到时用主屏。</summary>
    public static MonitorInfo? GetMonitorAtCursor(IReadOnlyList<MonitorInfo>? monitors = null)
    {
        monitors ??= GetMonitors();
        var cursor = GetCursorPosition();

        foreach (var monitor in monitors)
            if (monitor.Bounds.Contains(cursor)) return monitor;

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    public static MonitorInfo? GetPrimaryMonitor(IReadOnlyList<MonitorInfo>? monitors = null)
    {
        monitors ??= GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    /// <summary>把悬浮控制条放到指定屏幕工作区的底部居中（物理像素定位，避免高 DPI 折算误差）。</summary>
    public static void PositionBottomCenter(Window window, MonitorInfo monitor)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var dpi = Native.GetDpiForWindow(handle);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;

            var width = (int)Math.Round(Math.Max(1, window.ActualWidth) * scale);
            var height = (int)Math.Round(Math.Max(1, window.ActualHeight) * scale);

            var x = monitor.WorkArea.Left + (monitor.WorkArea.Width - width) / 2;
            var y = monitor.WorkArea.Bottom - height - (int)Math.Round(24 * scale);

            Native.SetWindowPos(
                handle, Native.HWND_TOPMOST, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        catch
        {
            // 定位失败不影响录制，窗口会留在 WPF 默认位置
        }
    }

    /// <summary>声明进程 DPI 感知；清单已声明时这一步失败也无妨。</summary>
    public static void EnableDpiAwareness()
    {
        try
        {
            if (!Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                Native.SetProcessDPIAware();
        }
        catch
        {
            try { Native.SetProcessDPIAware(); } catch { /* 已声明过则忽略 */ }
        }
    }
}
