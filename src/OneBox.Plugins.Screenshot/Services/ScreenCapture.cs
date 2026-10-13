using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>
/// 屏幕捕获。使用 GDI BitBlt 直取虚拟屏幕像素，避免 WPF 渲染路径带来的缩放问题。
/// </summary>
public static class ScreenCapture
{
    /// <summary>虚拟屏幕（所有显示器合并）的边界，单位为物理像素。</summary>
    public static System.Drawing.Rectangle? GetVirtualScreenBounds()
    {
        var x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        var y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        var w = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        var h = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) return null;
        return new System.Drawing.Rectangle(x, y, w, h);
    }

    /// <summary>显示器数量。</summary>
    public static int MonitorCount => Math.Max(1, Native.GetSystemMetrics(Native.SM_CMONITORS));

    /// <summary>鼠标当前所在位置（物理像素）。</summary>
    public static System.Drawing.Point GetCursorPosition()
        => Native.GetCursorPos(out var p) ? new System.Drawing.Point(p.X, p.Y) : System.Drawing.Point.Empty;

    /// <summary>鼠标当前位置所在窗口的矩形（物理像素）。</summary>
    public static System.Drawing.Rectangle? GetWindowUnderCursor()
    {
        if (!Native.GetCursorPos(out var p)) return null;
        var hwnd = Native.WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return null;

        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        return Native.GetWindowRect(hwnd, out var r)
            ? System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : null;
    }

    /// <summary>抓取虚拟屏幕的一部分（物理像素坐标，相对虚拟屏幕左上角）。</summary>
    public static BitmapSource? CaptureRegion(System.Drawing.Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;

        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var oldObj = IntPtr.Zero;

        try
        {
            memDc = Native.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height, // 负高度 = 自上而下，避免手动翻转
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Native.BI_RGB,
                },
            };

            dib = Native.CreateDIBSection(memDc, ref info, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;

            oldObj = Native.SelectObject(memDc, dib);

            // CAPTUREBLT：把分层窗口（部分带透明通道的窗口）也一并抓取
            var ok = Native.BitBlt(
                memDc, 0, 0, region.Width, region.Height,
                screenDc, region.X, region.Y,
                Native.SRCCOPY | Native.CAPTUREBLT);
            if (!ok) return null;

            // 直接复用 DIB 内存构造位图，避免额外拷贝
            var stride = region.Width * 4;
            var source = BitmapSource.Create(
                region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null,
                bits, stride * region.Height, stride);
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) Native.SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>抓取整个虚拟屏幕。</summary>
    public static BitmapSource? CaptureVirtualScreen()
    {
        var bounds = GetVirtualScreenBounds();
        return bounds is { } b ? CaptureRegion(b) : null;
    }

    /// <summary>
    /// 把 WPF 的 DIP 坐标换算成虚拟屏幕的物理像素坐标。
    /// </summary>
    public static System.Drawing.Rectangle ToPhysical(Window window, Rect dipRegion)
    {
        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var topLeft = toDevice.Transform(new Point(dipRegion.Left, dipRegion.Top));
        var bottomRight = toDevice.Transform(new Point(dipRegion.Right, dipRegion.Bottom));
        return System.Drawing.Rectangle.FromLTRB(
            (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));
    }

    /// <summary>
    /// 虚拟屏幕边界，换算为该视觉元素所在的 DIP 坐标。
    /// WPF 的 Left/Top/Width/Height 都用 DIP，而 GDI 给的是物理像素，
    /// 高 DPI 下直接把物理像素赋给它们会错位，必须先换算。
    /// </summary>
    public static Rect GetVirtualScreenDip(Visual visual)
    {
        var bounds = GetVirtualScreenBounds() ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var (scaleX, scaleY) = GetScale(visual);

        return new Rect(
            bounds.Left / scaleX,
            bounds.Top / scaleY,
            bounds.Width / scaleX,
            bounds.Height / scaleY);
    }

    /// <summary>把虚拟屏幕内的一段物理像素区域换算为该视觉元素的 DIP 坐标。</summary>
    public static Rect ToDip(Visual visual, System.Drawing.Rectangle physical)
    {
        var (scaleX, scaleY) = GetScale(visual);

        return new Rect(
            physical.Left / scaleX,
            physical.Top / scaleY,
            physical.Width / scaleX,
            physical.Height / scaleY);
    }

    /// <summary>X 方向 DPI 缩放系数（1 = 100%）。</summary>
    public static double GetScaleX(Visual visual) => GetScale(visual).ScaleX;

    /// <summary>Y 方向 DPI 缩放系数（1 = 100%）。</summary>
    public static double GetScaleY(Visual visual) => GetScale(visual).ScaleY;

    /// <summary>取视觉元素的 DPI 缩放；元素尚未接入视觉树时回退为 1。</summary>
    private static (double ScaleX, double ScaleY) GetScale(Visual visual)
    {
        // 窗口一旦创建，GetDpiForWindow 是最可靠的来源：
        // VisualTreeHelper.GetDpi 在窗口尚未显示（构造函数里）时会返回 1，
        // 在 125% 缩放下会把物理像素当成 DIP，导致尺寸整体缩小 20%。
        if (visual is Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    var dpi = Native.GetDpiForWindow(handle);
                    if (dpi > 0) return (dpi / 96.0, dpi / 96.0);
                }
            }
            catch
            {
                // 回退到 WPF 的 DPI 信息
            }
        }

        try
        {
            var info = VisualTreeHelper.GetDpi(visual);
            return (info.DpiScaleX > 0 ? info.DpiScaleX : 1,
                    info.DpiScaleY > 0 ? info.DpiScaleY : 1);
        }
        catch
        {
            return (1, 1);
        }
    }

    /// <summary>声明本进程 DPI 感知，保证坐标与物理像素一一对应。</summary>
    public static void EnableDpiAwareness()
    {
        try
        {
            // Win10 1703+：PER_MONITOR_AWARE_V2 让多屏不同缩放时坐标最准
            if (!Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                Native.SetProcessDPIAware();
        }
        catch
        {
            try { Native.SetProcessDPIAware(); } catch { /* 已声明过则忽略 */ }
        }
    }
}
