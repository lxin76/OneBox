using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>
/// 区域截图遮罩：覆盖整个虚拟屏幕，拖动选择区域，支持窗口吸附与实时尺寸提示。
/// 背景直接使用启动瞬间抓到的整屏快照（而不是透明窗口），这样遮罩本身就锁定了画面。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private readonly System.Drawing.Rectangle _virtualBounds;
    private readonly IReadOnlyList<WindowTarget> _windows;

    /// <summary>窗口矩形，屏幕 DIP 坐标。句柄就绪后按真实缩放系数填充。</summary>
    private List<Rect> _windowRects = [];

    private double _scaleX = 1;
    private double _scaleY = 1;
    private Point _offsetInWindow;

    private Point _start;
    private Rect _selection;
    private bool _dragging;
    private bool _snapEnabled = true;
    private bool _hasSelection;

    /// <summary>用户确认的区域（虚拟屏幕物理像素坐标）；取消时为 null。</summary>
    public System.Drawing.Rectangle? ResultRegion { get; private set; }

    /// <summary>用户选中的窗口标题（用于命名提示）；自由选区时为 null。</summary>
    public string? ResultWindowTitle { get; private set; }

    /// <summary>用户选中的窗口句柄；自由选区为 <see cref="IntPtr.Zero"/>。滚动截长图据此只抓窗口内容。</summary>
    public IntPtr ResultWindowHandle { get; private set; }

    /// <summary>本次使用的 DPI 缩放信息（诊断用）。</summary>
    public string ScaleInfo => $"{_scaleX:F3}×{_scaleY:F3}";

    public RegionSelectorWindow()
    {
        InitializeComponent();

        _virtualBounds = ScreenCapture.GetVirtualScreenBounds()
                         ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        // 用启动瞬间抓取的整屏快照当背景，这既锁定了画面，也避免遮罩窗口自己入镜
        var snapshot = ScreenCapture.CaptureVirtualScreen();
        if (snapshot is not null)
        {
            var brush = new ImageBrush(snapshot) { Stretch = Stretch.Fill };
            brush.Freeze();
            RootGrid.Background = brush;
        }
        else
        {
            RootGrid.Background = new SolidColorBrush(Color.FromRgb(20, 20, 32));
        }

        // 窗口自身永远排在最前，且排除在本插件的窗口枚举之外
        _windows = WindowEnumerator.ListSmallestFirst()
            .Where(w => w.Handle != IntPtr.Zero)
            .ToList();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // DPI 缩放必须在这里读，不能在构造函数里：那时 PresentationSource 还是 null，
        // 系数会退化成 1，导致 125% 缩放下选区比实际拖拽范围小 20%。
        // OnSourceInitialized 时合成目标已就绪，TransformToDevice 给出真实缩放。
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                       ?? Matrix.Identity;
        _scaleX = toDevice.M11 > 0 ? toDevice.M11 : ScreenCapture.GetScaleX(this);
        _scaleY = toDevice.M22 > 0 ? toDevice.M22 : ScreenCapture.GetScaleY(this);

        // 遮罩覆盖整个虚拟屏幕（含多屏的负坐标）。Width/Height 用 DIP，
        // WPF 会按本窗口的 DPI 自动换算成物理像素，因此无需手动 SetWindowPos。
        Left = _virtualBounds.Left / _scaleX;
        Top = _virtualBounds.Top / _scaleY;
        Width = Math.Max(1, _virtualBounds.Width / _scaleX);
        Height = Math.Max(1, _virtualBounds.Height / _scaleY);

        FullGeometry.Rect = new Rect(0, 0, Width, Height);

        // 窗口矩形换算成相对遮罩左上角的 DIP，供吸附命中测试使用
        _offsetInWindow = new Point(_virtualBounds.Left, _virtualBounds.Top);
        _windowRects = _windows.Select(w => ToDip(w.Bounds)).ToList();

        Activate();
        Focus();
        Mouse.Capture(this);
    }

    /// <summary>虚拟屏幕物理像素 → 遮罩窗口内的 DIP 坐标（含虚拟屏幕原点的负坐标偏移）。</summary>
    private Rect ToDip(System.Drawing.Rectangle physical) => new(
        (physical.Left - _offsetInWindow.X) / _scaleX,
        (physical.Top - _offsetInWindow.Y) / _scaleY,
        physical.Width / _scaleX,
        physical.Height / _scaleY);

    /// <summary>遮罩窗口内的 DIP 坐标 → 虚拟屏幕物理像素。</summary>
    private System.Drawing.Rectangle ToPhysical(Rect dip) => System.Drawing.Rectangle.FromLTRB(
        (int)Math.Round(dip.Left * _scaleX + _offsetInWindow.X),
        (int)Math.Round(dip.Top * _scaleY + _offsetInWindow.Y),
        (int)Math.Round(dip.Right * _scaleX + _offsetInWindow.X),
        (int)Math.Round(dip.Bottom * _scaleY + _offsetInWindow.Y));

    // ---- 鼠标交互 ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _start = e.GetPosition(this);
        _dragging = true;
        _snapEnabled = false; // 一旦开始拖动就是自由选区，除非按 Space 切回来
        UpdateSelection(new Rect(_start, _start));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var current = e.GetPosition(this);

        GuideH.X1 = 0; GuideH.X2 = Width; GuideH.Y1 = current.Y; GuideH.Y2 = current.Y;
        GuideV.Y1 = 0; GuideV.Y2 = Height; GuideV.X1 = current.X; GuideV.X2 = current.X;

        if (!_dragging)
        {
            // 未拖动时：悬停高亮鼠标下的窗口，提示可以单击直接取整窗
            var hovered = HitTestWindow(current);
            if (_snapEnabled && hovered is not null)
            {
                ShowSelection(hovered.Value.Rect, hovered.Value.Title, isHover: true);
            }
            return;
        }

        UpdateSelection(new Rect(_start, current));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;

        var end = e.GetPosition(this);
        var dragged = new Rect(_start, end);

        // 几乎没有拖动 → 视为单击：选取鼠标下的窗口
        if (dragged.Width < 4 || dragged.Height < 4)
        {
            var hovered = HitTestWindow(end);
            if (hovered is not null)
            {
                Confirm(hovered.Value.Rect, hovered.Value.Title);
                return;
            }
        }

        if (dragged.Width < 2 || dragged.Height < 2)
        {
            // 点在空白处，忽略这次点击，继续保持选择状态
            UpdateSelection(dragged);
            return;
        }

        Confirm(dragged, null);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Cancel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;

            case Key.Enter when _hasSelection:
                Confirm(_selection, ResultWindowTitle);
                e.Handled = true;
                break;

            case Key.Space:
                // 切换窗口吸附：吸附时选区自动贴合鼠标下的窗口
                _snapEnabled = !_snapEnabled;
                var cursor = Mouse.GetPosition(this);
                if (_snapEnabled && HitTestWindow(cursor) is { } target)
                    ShowSelection(target.Rect, target.Title, isHover: false);
                HintSubText.Text = _snapEnabled
                    ? "窗口吸附：开 · 移动鼠标自动贴合窗口 · 按 Space 关闭"
                    : "窗口吸附：关 · 拖动自由选区 · 按 Space 开启";
                e.Handled = true;
                break;

            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                // Ctrl+A：选取整个虚拟屏幕
                Confirm(new Rect(0, 0, Width, Height), null);
                e.Handled = true;
                break;
        }
    }

    // ---- 选区渲染 ----

    private void UpdateSelection(Rect rect)
    {
        if (!_snapEnabled) ShowSelection(rect, null, isHover: false);
        else
        {
            var hovered = HitTestWindow(rect.BottomRight);
            if (hovered is not null) ShowSelection(hovered.Value.Rect, hovered.Value.Title, isHover: false);
            else ShowSelection(rect, null, isHover: false);
        }
    }

    private void ShowSelection(Rect rect, string? title, bool isHover)
    {
        rect = ClampToScreen(rect);
        _selection = rect;
        _hasSelection = rect.Width >= 2 && rect.Height >= 2;

        HoleGeometry.Rect = rect;
        SelectionBorder.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SelectionBorder.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Background = isHover ? new SolidColorBrush(Color.FromArgb(24, 59, 130, 246))
                                             : Brushes.Transparent;

        if (!_hasSelection)
        {
            SizeTip.Visibility = Visibility.Collapsed;
            return;
        }

        var physical = ToPhysical(rect);
        SizeTipText.Text = $"{physical.Width} × {physical.Height}"
                           + (string.IsNullOrEmpty(title) ? "" : $"  ·  {Truncate(title!, 28)}");
        SizeTip.Visibility = Visibility.Visible;
        SizeTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // 尺寸提示跟随选区：默认放选区下方，空间不足时移到选区内侧
        var tipWidth = SizeTip.DesiredSize.Width;
        var tipHeight = SizeTip.DesiredSize.Height;
        var tipLeft = rect.Left;
        var tipTop = rect.Bottom + 6;
        if (tipTop + tipHeight > Height) tipTop = Math.Max(0, rect.Bottom - tipHeight - 6);
        if (tipLeft + tipWidth > Width) tipLeft = Math.Max(0, Width - tipWidth);
        SizeTip.Margin = new Thickness(tipLeft, tipTop, 0, 0);
        SizeTip.HorizontalAlignment = HorizontalAlignment.Left;
        SizeTip.VerticalAlignment = VerticalAlignment.Top;
    }

    private Rect ClampToScreen(Rect rect)
    {
        var left = Math.Max(0, Math.Min(rect.Left, rect.Right));
        var top = Math.Max(0, Math.Min(rect.Top, rect.Bottom));
        var right = Math.Min(Width, Math.Max(rect.Left, rect.Right));
        var bottom = Math.Min(Height, Math.Max(rect.Top, rect.Bottom));
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>找出包含指定点的最小窗口（排除本遮罩窗口自身）。</summary>
    private (Rect Rect, string Title, IntPtr Handle)? HitTestWindow(Point point)
    {
        for (int i = 0; i < _windowRects.Count; i++)
        {
            if (!_windowRects[i].Contains(point)) continue;

            // 排除本进程的窗口（遮罩自己、贴图窗、工具条等）
            var handle = _windows[i].Handle;
            Native.GetWindowThreadProcessId(handle, out var pid);
            if (pid == (uint)Environment.ProcessId) continue;

            return (_windowRects[i], _windows[i].Title, handle);
        }
        return null;
    }

    private void Confirm(Rect rect, string? title)
    {
        rect = ClampToScreen(rect);
        if (rect.Width < 2 || rect.Height < 2)
        {
            Cancel();
            return;
        }

        ResultRegion = ToPhysical(rect);
        ResultWindowTitle = title;

        // 选区的中心点落在哪个顶层窗口，滚动截图就把滚轮投递给它。
        // 注意：不能只在 _windows 里查——那张表按“最小面积优先”命中，容易选到弹窗，
        // 而这里要的是真正承托页面的根窗口。所以优先用 WindowFromPoint 取根窗口，
        // 并排除本进程自己的窗口（遮罩、贴图、控制条）。取不到时退回原来的命中测试。
        var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        var physical = ResultRegion.Value;
        var screenCenter = new Point(physical.Left + physical.Width / 2.0, physical.Top + physical.Height / 2.0);
        ResultWindowHandle = ResolveTargetWindow(screenCenter) ?? HitTestWindow(center)?.Handle ?? IntPtr.Zero;
        CloseSafely();
    }

    /// <summary>取某点所在的顶层根窗口（排除本进程窗口）；取不到返回 null。</summary>
    private static IntPtr? ResolveTargetWindow(Point point)
    {
        try
        {
            var screen = new Native.POINT { X = (int)point.X, Y = (int)point.Y };
            var hwnd = Native.WindowFromPoint(screen);
            if (hwnd == IntPtr.Zero) return null;

            var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            if (root != IntPtr.Zero) hwnd = root;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return null;

            return Native.IsWindowVisible(hwnd) ? hwnd : null;
        }
        catch
        {
            return null;
        }
    }

    private void Cancel()
    {
        ResultRegion = null;
        CloseSafely();
    }

    private void CloseSafely()
    {
        if (Mouse.Captured == this) Mouse.Capture(null);
        try
        {
            Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
