using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>仅放在选区外的非激活控制条；没有安全位置时隐藏，仍可用 F8 完成、Esc 取消。</summary>
public partial class ScrollControlWindow : Window
{
    public event Action? FinishRequested;
    public event Action? CancelRequested;
    private readonly System.Drawing.Rectangle _region;
    private bool _closing;
    public bool HasSafePlacement { get; private set; }

    public ScrollControlWindow(System.Drawing.Rectangle region)
    {
        InitializeComponent();
        _region = region;
        ShowActivated = false;
        SourceInitialized += (_, _) => TopmostKeeper.ApplyPasteStyle(this, topmost: true);
        Loaded += (_, _) =>
        {
            UpdateLayout();
            PlaceOutsideCapture();
        };
        Closing += (_, _) => { if (!_closing) CancelRequested?.Invoke(); };
    }

    private void PlaceOutsideCapture()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var window)) return;
        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var bounds = ScreenCapture.GetVirtualScreenBounds();
        if (bounds is not { } screen) return;
        var candidates = new[]
        {
            new System.Drawing.Point(_region.Right - width, _region.Bottom + 10),
            new System.Drawing.Point(_region.Right - width, _region.Top - height - 10),
            new System.Drawing.Point(_region.Right + 10, _region.Top),
            new System.Drawing.Point(_region.Left - width - 10, _region.Top),
            new System.Drawing.Point(screen.Left + 10, screen.Top + 10),
            new System.Drawing.Point(screen.Right - width - 10, screen.Bottom - height - 10),
        };
        foreach (var point in candidates)
        {
            var candidate = new System.Drawing.Rectangle(point.X, point.Y, width, height);
            if (candidate.IntersectsWith(_region)) continue;
            var rect = new PlacementRect { Left = candidate.Left, Top = candidate.Top, Right = candidate.Right, Bottom = candidate.Bottom };
            var monitor = MonitorFromRect(ref rect, 0);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) continue;
            if (candidate.Left < info.Work.Left || candidate.Top < info.Work.Top || candidate.Right > info.Work.Right || candidate.Bottom > info.Work.Bottom) continue;
            if (!SetWindowPos(hwnd, IntPtr.Zero, candidate.Left, candidate.Top, 0, 0, 0x0001 | 0x0004 | 0x0010)) continue;
            HasSafePlacement = true;
            return;
        }
        HasSafePlacement = false;
        Native.ShowWindow(hwnd, Native.SW_HIDE);
        AppLog.Write("滚动截图：没有选区外控制条位置，控制条隐藏；F8 完成并预览，Esc 取消。");
    }

    public void Report(ScrollProgress progress, int maxHeight)
    {
        if (_closing) return;
        StatusText.Text = progress.Status + "\nF8 完成并预览 · Esc 取消";
        Progress.Value = maxHeight <= 0 ? 0 : Math.Min(100, progress.CapturedHeight * 100.0 / maxHeight);
    }

    /// <summary>兼容旧调用：控制条始终处于选区外，禁止逐帧显示/隐藏造成采样污染。</summary>
    public void ParkForCapture(bool park) { }

    public void CloseControl()
    {
        _closing = true;
        Close();
    }
    private void OnStopClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public PlacementRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out PlacementRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref PlacementRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
