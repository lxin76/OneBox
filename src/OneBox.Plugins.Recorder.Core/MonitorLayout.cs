using System.Drawing;

namespace OneBox.Plugins.Recorder.Core;

/// <summary>
/// 一块显示器的物理像素边界。Index 是枚举顺序，用于在
/// ScreenRecorderLib 的显示器列表里按位置兜底匹配。
/// </summary>
public readonly record struct MonitorBounds(
    int Index,
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkArea,
    bool IsPrimary)
{
    public int Left => Bounds.Left;
    public int Top => Bounds.Top;
    public int Right => Bounds.Right;
    public int Bottom => Bounds.Bottom;

    public bool Contains(int x, int y)
        => x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;
}

/// <summary>
/// 多显示器坐标换算：虚拟屏幕坐标（物理像素、可能为负）↔ 单块显示器的局部坐标。
///
/// ScreenRecorderLib 的 DisplayRecordingSource.SourceRect 是相对该显示器左上角的，
/// 因此选区落在哪块屏幕、怎么裁到屏幕内、怎么换成局部坐标，都必须先算清楚。
/// 这些都是纯几何，放在 Core 里可单测。
/// </summary>
public static class MonitorLayout
{
    /// <summary>包含指定点的显示器；没有命中时返回最近的一块（与 Win32 MonitorFromPoint 语义一致）。</summary>
    public static MonitorBounds? MonitorAt(IReadOnlyList<MonitorBounds> monitors, int x, int y)
    {
        foreach (var monitor in monitors)
            if (monitor.Contains(x, y)) return monitor;

        MonitorBounds? nearest = null;
        var nearestDistance = long.MaxValue;
        foreach (var monitor in monitors)
        {
            var dx = Math.Max(Math.Max(monitor.Bounds.Left - x, 0), x - (monitor.Bounds.Right - 1));
            var dy = Math.Max(Math.Max(monitor.Bounds.Top - y, 0), y - (monitor.Bounds.Bottom - 1));
            var distance = (long)dx * dx + (long)dy * dy;
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = monitor;
        }
        return nearest;
    }

    public static MonitorBounds? Primary(IReadOnlyList<MonitorBounds> monitors)
    {
        foreach (var monitor in monitors)
            if (monitor.IsPrimary) return monitor;
        return monitors.Count > 0 ? monitors[0] : null;
    }

    /// <summary>选区与哪块显示器重叠面积最大，就录哪块；跨屏时只保留该屏内的部分。</summary>
    public static MonitorBounds? DominantForRegion(IReadOnlyList<MonitorBounds> monitors, Rectangle region)
    {
        MonitorBounds? best = null;
        long bestArea = -1;

        foreach (var monitor in monitors)
        {
            var overlap = Rectangle.Intersect(region, monitor.Bounds);
            var area = (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        return bestArea > 0 ? best : null;
    }

    /// <summary>把选区裁进显示器边界；无重叠时返回空矩形。</summary>
    public static Rectangle ClampToMonitor(Rectangle region, MonitorBounds monitor)
        => Rectangle.Intersect(region, monitor.Bounds);

    /// <summary>虚拟屏幕坐标 → 显示器局部坐标。</summary>
    public static Rectangle ToLocal(Rectangle region, MonitorBounds monitor)
        => new(region.Left - monitor.Bounds.Left, region.Top - monitor.Bounds.Top, region.Width, region.Height);

    /// <summary>
    /// 把虚拟屏幕选区换算为「录制源 + 局部裁剪矩形」。
    /// 选区过小或完全落在显示器之外时返回 false（调用方应给出提示而不是开一个空录制）。
    /// </summary>
    public static bool TryLocalizeRegion(
        IReadOnlyList<MonitorBounds> monitors,
        Rectangle region,
        out MonitorBounds monitor,
        out Rectangle localRegion,
        int minSize = 16)
    {
        monitor = default;
        localRegion = Rectangle.Empty;

        if (region.Width < minSize || region.Height < minSize) return false;

        var dominant = DominantForRegion(monitors, region);
        if (dominant is not { } target) return false;

        var clamped = ClampToMonitor(region, target);
        if (clamped.Width < minSize || clamped.Height < minSize) return false;

        monitor = target;
        localRegion = ToLocal(clamped, target);
        return true;
    }

    /// <summary>
    /// 等比缩小到最大宽度，并且宽高都取偶数（H.264 要求），不放大源画面。
    /// </summary>
    public static (int Width, int Height) FitOutputSize(int sourceWidth, int sourceHeight, int maxWidth)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (2, 2);

        var width = maxWidth > 0 ? Math.Min(sourceWidth, maxWidth) : sourceWidth;
        var scale = width / (double)sourceWidth;
        var height = (int)Math.Round(sourceHeight * scale);

        return (MakeEven(Math.Max(2, width)), MakeEven(Math.Max(2, height)));
    }

    private static int MakeEven(int value) => Math.Max(2, value / 2 * 2);
}
