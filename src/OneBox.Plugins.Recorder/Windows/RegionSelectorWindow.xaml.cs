using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OneBox.Plugins.Recorder.Windows;

/// <summary>
/// 区域录制遮罩：覆盖整个虚拟屏幕，拖动选择一块矩形。
/// 与截图插件的选区窗不同，这里不需要窗口吸附和整屏快照——
/// 录制会在遮罩关闭后才开始，取景直接看真实桌面即可。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private System.Drawing.Rectangle _virtualBounds;
    private double _scaleX = 1;
    private double _scaleY = 1;

    private Point _start;
    private bool _dragging;
    private bool _hasSelection;
    private bool _closed;

    /// <summary>用户选中的区域（虚拟屏幕物理像素）；取消时为 null。</summary>
    public System.Drawing.Rectangle? ResultRegion { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();
        _virtualBounds = Services.ScreenGeometry.GetVirtualScreenBounds();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // DPI 必须在窗口句柄就绪后读，构造函数里 TransformToDevice 还是单位矩阵
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                       ?? Matrix.Identity;
        _scaleX = toDevice.M11 > 0 ? toDevice.M11 : 1;
        _scaleY = toDevice.M22 > 0 ? toDevice.M22 : 1;

        Left = _virtualBounds.Left / _scaleX;
        Top = _virtualBounds.Top / _scaleY;
        Width = Math.Max(1, _virtualBounds.Width / _scaleX);
        Height = Math.Max(1, _virtualBounds.Height / _scaleY);
        FullGeometry.Rect = new Rect(0, 0, Width, Height);

        Activate();
        Focus();
        Mouse.Capture(this);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _start = e.GetPosition(this);
        _dragging = true;
        UpdateSelection(new Rect(_start, _start));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        UpdateSelection(new Rect(_start, e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;

        var rect = ClampToWindow(new Rect(_start, e.GetPosition(this)));
        if (rect.Width < 16 || rect.Height < 16)
        {
            // 误点一下不当作确认，保持遮罩等用户重新拖
            UpdateSelection(rect);
            return;
        }

        ResultRegion = ToPhysical(rect);
        CloseSilently();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ResultRegion = null;
        CloseSilently();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            ResultRegion = null;
            CloseSilently();
            e.Handled = true;
        }
    }

    private void UpdateSelection(Rect rect)
    {
        rect = ClampToWindow(rect);
        _hasSelection = rect.Width >= 2 && rect.Height >= 2;

        HoleGeometry.Rect = _hasSelection ? rect : new Rect(0, 0, 0, 0);
        SelectionBorder.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SelectionBorder.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;

        if (!_hasSelection)
        {
            SizeTip.Visibility = Visibility.Collapsed;
            return;
        }

        var physical = ToPhysical(rect);
        SizeTipText.Text = $"{physical.Width} × {physical.Height}";
        SizeTip.Visibility = Visibility.Visible;
        SizeTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var tipWidth = SizeTip.DesiredSize.Width;
        var tipHeight = SizeTip.DesiredSize.Height;
        var tipLeft = rect.Left;
        var tipTop = rect.Bottom + 6;
        if (tipTop + tipHeight > Height) tipTop = Math.Max(0, rect.Top - tipHeight - 6);
        if (tipLeft + tipWidth > Width) tipLeft = Math.Max(0, Width - tipWidth);

        SizeTip.Margin = new Thickness(tipLeft, tipTop, 0, 0);
        SizeTip.HorizontalAlignment = HorizontalAlignment.Left;
        SizeTip.VerticalAlignment = VerticalAlignment.Top;
    }

    private Rect ClampToWindow(Rect rect)
    {
        var left = Math.Max(0, Math.Min(rect.Left, rect.Right));
        var top = Math.Max(0, Math.Min(rect.Top, rect.Bottom));
        var right = Math.Min(Width, Math.Max(rect.Left, rect.Right));
        var bottom = Math.Min(Height, Math.Max(rect.Top, rect.Bottom));
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private System.Drawing.Rectangle ToPhysical(Rect dip) => System.Drawing.Rectangle.FromLTRB(
        (int)Math.Round(dip.Left * _scaleX + _virtualBounds.Left),
        (int)Math.Round(dip.Top * _scaleY + _virtualBounds.Top),
        (int)Math.Round(dip.Right * _scaleX + _virtualBounds.Left),
        (int)Math.Round(dip.Bottom * _scaleY + _virtualBounds.Top));

    /// <summary>宿主退出时强制关闭遮罩；不产生录制。</summary>
    public void CloseSilently()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            if (Mouse.Captured == this) Mouse.Capture(null);
            Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
    }
}
