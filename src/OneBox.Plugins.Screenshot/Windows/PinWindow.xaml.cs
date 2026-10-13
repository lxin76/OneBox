using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OneBox.Plugins.Screenshot.Core;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>
/// 贴图窗口：把截图钉在屏幕上，始终浮在其它窗口最上层，切换应用也不会被遮挡。
/// 支持拖动、鼠标滚轮缩放、右下角等比缩放，以及复制/保存/识别/关闭。
/// </summary>
public partial class PinWindow : Window
{
    /// <summary>
    /// 重申置顶的间隔。开销很小（一次无 Z 序变更的 SetWindowPos），
    /// 但贴图可能有十几张，放宽到 1 秒足够应对“被普通窗口盖住”的情况。
    /// </summary>
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromSeconds(1);

    private readonly BitmapSource _source;
    private readonly Action<PinWindow>? _onClosed;
    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _flashTimer;

    private bool _pinned = true;
    private bool _dragging;
    private bool _resizing;
    private Point _dragOffsetInWindow;
    private Point _resizeStart;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private readonly double _imageAspect;

    // ---- 文字选择状态 ----
    private OcrLayout? _layout;
    private bool _selectMode;
    private bool _selecting;
    private OcrSelection _selection;
    private bool _loadingLayout;

    /// <summary>高亮颜色（半透明蓝），与系统文本选择的观感接近。</summary>
    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromArgb(96, 60, 150, 255));

    /// <summary>
    /// 请求本贴图的 OCR 版面（词框 + 行聚类）。由插件主体实现并缓存，
    /// 因为识别一次要几十到几百毫秒，绝不能在拖动过程中重跑。
    /// </summary>
    public Func<PinWindow, Task<OcrLayout?>>? LayoutProvider { get; set; }

    /// <summary>请求把选中的文字写入剪贴板。</summary>
    public Func<string, bool>? CopyTextHandler { get; set; }

    /// <summary>请求把本贴图保存到文件（由插件主体执行真正的保存逻辑）。</summary>
    public event Action<PinWindow>? SaveRequested;

    /// <summary>请求识别本贴图的文字。</summary>
    public event Action<PinWindow>? OcrRequested;

    public PinWindow(BitmapSource source, Action<PinWindow>? onClosed = null)
    {
        InitializeComponent();
        _source = source;
        _onClosed = onClosed;
        _imageAspect = source.PixelWidth / (double)Math.Max(1, source.PixelHeight);

        ShotImage.Source = source;

        // 选择文字按钮平时淡显，进入模式后点亮
        SelectTextButton.Opacity = 0.45;

        // 初始尺寸：不超过屏幕的 60%，小图保持原样
        var area = ScreenCapture.GetVirtualScreenBounds()
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.6 / source.PixelWidth,
                                            area.Height * 0.6 / source.PixelHeight));
        Width = Math.Max(80, source.PixelWidth * scale);
        Height = Math.Max(60, source.PixelHeight * scale);

        // 默认放到屏幕右下角附近，避免盖住用户正在看的内容
        var areaDip = ScreenCapture.GetVirtualScreenDip(this);
        Left = areaDip.Left + areaDip.Width - Width - 40;
        Top = areaDip.Top + areaDip.Height - Height - 80;

        _topmostTimer = new DispatcherTimer { Interval = TopmostReassertInterval };
        // 保活重申走 ReassertTopmost：只确认仍在置顶组，不改动组内层叠次序。
        // 若这里用 AssertTopmost，多张贴图会每 1 秒互相插队，形成循环争抢。
        _topmostTimer.Tick += (_, _) => { if (_pinned) TopmostKeeper.ReassertTopmost(this); };

        // 缩放/改变窗口尺寸后，高亮矩形要按新的显示比例重画
        ShotImage.SizeChanged += (_, _) => RedrawHighlight();
    }

    /// <summary>贴图当前是否置顶。</summary>
    public bool IsPinned => _pinned;

    /// <summary>是否处于文字选择模式（诊断用）。</summary>
    public bool IsSelectMode => _selectMode;

    /// <summary>当前版面是否已识别出来（诊断用）。</summary>
    public bool HasLayout => _layout is not null;

    /// <summary>当前选区（诊断用）。</summary>
    public OcrSelection CurrentSelection => _selection;

    /// <summary>原始位图。</summary>
    public BitmapSource Source => _source;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyPinnedState(_pinned);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 本窗口被激活说明是用户主动操作（点击了贴图），此时插到最顶层是符合预期的意图，
        // 因此走无条件抬升的 BringToFrontWithoutActivating，而不是不改 Z 序的 AssertTopmost。
        // 注意：周期性的置顶保活绝不能这么做，否则多张贴图会互相争抢层叠次序。
        if (_pinned) TopmostKeeper.BringToFrontWithoutActivating(this);
    }

    // ---- 置顶保活 ----

    private void OnToggleTopmost(object sender, RoutedEventArgs e) => ToggleTopmost();

    private void ToggleTopmost() => ApplyPinnedState(!_pinned);

    /// <summary>外部（插件主体/托盘菜单）要求置顶或取消置顶。</summary>
    public void SetPinned(bool pinned)
    {
        if (_pinned != pinned) ApplyPinnedState(pinned);
    }

    private void ApplyPinnedState(bool pinned)
    {
        _pinned = pinned;
        // ApplyPasteStyle 在置顶时会无条件插队，因此这里不需要再调 AssertTopmost
        TopmostKeeper.ApplyPasteStyle(this, pinned);

        if (pinned) _topmostTimer.Start();
        else _topmostTimer.Stop();

        PinButton.Opacity = pinned ? 1.0 : 0.45;
        PinButton.ToolTip = pinned
            ? "已置顶：始终浮在最上层（Alt+T 取消置顶）"
            : "未置顶：可能被其他窗口遮挡（Alt+T 置顶）";
        UpdateInfo();
    }

    // ---- 拖动与缩放 ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 工具条与手柄上的点击自行处理，不触发拖动
        if (e.OriginalSource is DependencyObject d && IsInsideButton(d))
        {
            // 工具条点击同样属于用户对这张贴图的主动操作，应先把它抬到最前
            BringToFront();
            return;
        }

        // 用户点击了这张贴图：把它抬到置顶组最前，便于在互相遮挡时取用。
        // 不能依赖 OnActivated——窗口带 WS_EX_NOACTIVATE，点击通常不会触发激活。
        BringToFront();

        // 文字选择模式下，左键拖动改为划选文字，不再移动窗口
        if (_selectMode)
        {
            HandleSelectMouseDown(e);
            return;
        }

        if (e.ClickCount == 2)
        {
            ResetToOriginalSize();
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        _dragging = true;
        _dragOffsetInWindow = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// 把本贴图抬到置顶组最前面。仅用于用户的主动操作（点击、拖动、缩放）。
    /// 周期性的置顶保活走的是不改 Z 序的 ReassertTopmost，两者不可混用。
    /// </summary>
    private void BringToFront()
    {
        if (_pinned) TopmostKeeper.BringToFrontWithoutActivating(this);
    }

    /// <summary>文字选择模式下的按下：双击选词，否则开始划选。</summary>
    private void HandleSelectMouseDown(MouseButtonEventArgs e)
    {
        BeginSelectAt(e.GetPosition(ShotImage), e.ClickCount);

        if (_selecting) CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// 在图片显示区内的某一点开始选择。<paramref name="windowPoint"/> 是相对图片控件的坐标，
    /// 与 <c>e.GetPosition(ShotImage)</c> 一致。
    /// </summary>
    internal void BeginSelectAt(Point windowPoint, int clickCount = 1)
    {
        if (_layout is null) return;

        var image = ToImagePixel(windowPoint);

        if (clickCount == 2)
        {
            _selection = _layout.SelectWordAt(image.X, image.Y);
            RedrawHighlight();
            CopySelection();
            return;
        }

        _selection = OcrSelection.At(_layout.CaretAt(image.X, image.Y));
        _selecting = true;
        RedrawHighlight();
    }

    /// <summary>拖动过程中更新选区末端。</summary>
    internal void ExtendSelectTo(Point windowPoint)
    {
        if (!_selecting || _layout is null) return;

        var image = ToImagePixel(windowPoint);
        _selection = new OcrSelection(_selection.Anchor, _layout.CaretAt(image.X, image.Y));
        RedrawHighlight();
    }

    /// <summary>结束划选并复制。</summary>
    internal void FinishSelect()
    {
        if (!_selecting) return;

        _selecting = false;
        CopySelection();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_selecting && _layout is not null)
        {
            ExtendSelectTo(e.GetPosition(ShotImage));
            return;
        }

        if (!_dragging && !_resizing) return;

        var current = e.GetPosition(this);

        if (_dragging)
        {
            // 用屏幕坐标定位，避免窗口移动后 e.GetPosition(this) 自身漂移
            var screen = PointToScreen(current);
            Left = screen.X - _dragOffsetInWindow.X;
            Top = screen.Y - _dragOffsetInWindow.Y;
        }
        else
        {
            var deltaX = current.X - _resizeStart.X;
            var deltaY = current.Y - _resizeStart.Y;

            // 取主要位移方向，按原图宽高比等比缩放
            var width = Math.Abs(deltaX) * _imageAspect > Math.Abs(deltaY)
                ? _resizeStartWidth + deltaX
                : (_resizeStartHeight + deltaY) * _imageAspect;

            width = Math.Max(80, Math.Min(width, _source.PixelWidth * 6.0));
            Width = width;
            Height = width / _imageAspect;
            UpdateInfo();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // 划选结束：松开即复制，这是截图取字最常见的意图
        if (_selecting)
        {
            if (Mouse.Captured == this) ReleaseMouseCapture();
            FinishSelect();
            e.Handled = true;
            return;
        }

        EndInteraction();
    }

    private void OnResizeStart(object sender, MouseButtonEventArgs e)
    {
        _resizing = true;
        _resizeStart = e.GetPosition(this);
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        CaptureMouse();
        e.Handled = true;
    }

    private void EndInteraction()
    {
        if (Mouse.Captured == this) ReleaseMouseCapture();
        _dragging = false;
        _resizing = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // 滚轮缩放：按 1.1 倍步进
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var width = Math.Clamp(Width * factor, 60, _source.PixelWidth * 6.0);
        Width = width;
        Height = width / _imageAspect;
        UpdateInfo();
        e.Handled = true;
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => ResetToOriginalSize();

    private void ResetToOriginalSize()
    {
        // 目标宽度 = 原图像素宽；超过屏幕 90% 时回落到该上限（屏幕尺寸按 DIP 计）
        var area = ScreenCapture.GetVirtualScreenDip(this);
        var target = Math.Min(_source.PixelWidth, area.Width * 0.9);
        Width = Math.Max(80, target);
        Height = Width / _imageAspect;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        var zoom = Width / _source.PixelWidth * 100;
        var mode = _selectMode ? " · 选择文字" : "";
        InfoText.Text = $"{_source.PixelWidth} × {_source.PixelHeight} · {zoom:0}% · {(_pinned ? "置顶" : "未置顶")}{mode}";
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---- 文字选择 ----
    //
    // 交互设计：用工具条上的「选择文字」按钮进入模式，而不是复用左键拖动。
    // 左键拖动在本窗口已用于移动贴图，若用隐式区分（拖一点算选字、拖很多算移动）
    // 会非常难以预期。进入模式后光标变 I 形，拖动划选，松开即复制，再按一次退出。

    private void OnToggleTextSelect(object sender, RoutedEventArgs e) => ToggleTextSelect();

    /// <summary>进入或退出文字选择模式。</summary>
    public async void ToggleTextSelect()
    {
        if (_selectMode)
        {
            ExitTextSelect();
            return;
        }

        // 识别尚未回来时不要重复触发
        if (_loadingLayout)
        {
            Flash("正在识别文字，请稍候…");
            return;
        }

        _selectMode = true;
        Cursor = Cursors.IBeam;
        SelectTextButton.Opacity = 1.0;
        RedrawHighlight();

        if (_layout is not null)
        {
            Flash($"已进入选择文字：拖动划选（共 {_layout.Lines.Count} 行）");
            return;
        }

        // 首次进入才识别。OCR 要几十到几百毫秒，只做一次并缓存，
        // 之后的划选全是纯几何计算（亚毫秒级）。
        if (LayoutProvider is null)
        {
            ExitTextSelect();
            Flash("文字选择不可用");
            return;
        }

        _loadingLayout = true;
        Flash("正在识别文字…");

        // OCR 会占用 UI 线程几百毫秒。这里让出一次调度，且优先级低于 Render，
        // 好让上面的提示先真正画出来，否则用户看到的是界面直接卡住。
        await Dispatcher.Yield(DispatcherPriority.Background);

        OcrLayout? layout;
        try
        {
            layout = await LayoutProvider(this);
        }
        catch (Exception ex)
        {
            AppLog.Write($"贴图文字选择识别失败: {ex}");
            layout = null;
        }
        finally
        {
            _loadingLayout = false;
        }

        // 识别期间用户可能已经退出模式或关掉了窗口
        if (!_selectMode) return;

        _layout = layout;
        if (_layout is null || _layout.IsEmpty)
        {
            // 先退出模式再提示：ExitTextSelect 会刷新信息条，顺序反了提示会被立刻抹掉
            ExitTextSelect();
            Flash("这张图里没有识别到文字");
            return;
        }

        Flash($"已识别 {_layout.Lines.Count} 行：拖动划选，松开即复制");
    }

    private void ExitTextSelect()
    {
        _selectMode = false;
        _selecting = false;
        Cursor = Cursors.Arrow;
        SelectTextButton.Opacity = 0.45;
        _selection = default;
        HighlightLayer.Children.Clear();
        if (Mouse.Captured == this && !_dragging && !_resizing) ReleaseMouseCapture();
        UpdateInfo();
    }

    /// <summary>把窗口坐标换算成原图像素坐标。图片按 Stretch="Fill" 铺满，故按显示尺寸等比换算。</summary>
    private Point ToImagePixel(Point inImage)
    {
        var w = ShotImage.ActualWidth;
        var h = ShotImage.ActualHeight;
        if (w <= 0 || h <= 0) return inImage;

        return new Point(
            inImage.X * _source.PixelWidth / w,
            inImage.Y * _source.PixelHeight / h);
    }

    /// <summary>按当前选区重画高亮。矩形与命中测试同源，因此永远与鼠标位置一致。</summary>
    private void RedrawHighlight()
    {
        HighlightLayer.Children.Clear();
        if (!_selectMode || _layout is null || _selection.IsEmpty) return;

        var w = ShotImage.ActualWidth;
        var h = ShotImage.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var scaleX = w / _source.PixelWidth;
        var scaleY = h / _source.PixelHeight;

        foreach (var rect in _layout.GetHighlightRects(_selection))
        {
            var shape = new Rectangle
            {
                Width = Math.Max(0, rect.Width * scaleX),
                Height = Math.Max(0, rect.Height * scaleY),
                Fill = HighlightBrush,
            };
            Canvas.SetLeft(shape, rect.X * scaleX);
            Canvas.SetTop(shape, rect.Y * scaleY);
            HighlightLayer.Children.Add(shape);
        }
    }

    /// <summary>把当前选区复制到剪贴板，并给出反馈。</summary>
    private void CopySelection()
    {
        if (_layout is null || _selection.IsEmpty) return;

        var text = _layout.GetText(_selection);
        if (string.IsNullOrEmpty(text)) return;

        var ok = CopyTextHandler?.Invoke(text) ?? false;
        AppLog.Write($"贴图划选复制：{text.Length} 字符，{(ok ? "成功" : "失败")}");
        Flash(ok ? $"已复制 {text.Length} 个字符" : "复制失败，剪贴板可能被占用");
    }

    // ---- 工具条 ----

    private void OnToolbarMouseEnter(object sender, MouseEventArgs e) => Toolbar.Opacity = 1.0;

    private void OnToolbarMouseLeave(object sender, MouseEventArgs e) => Toolbar.Opacity = 0.28;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ClipboardSupport.CopyImage(_source)) Flash("已复制到剪贴板");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this);

    private void OnOcrClick(object sender, RoutedEventArgs e) => OcrRequested?.Invoke(this);

    private void OnOpenInViewerClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this);

    /// <summary>在信息条上短暂显示一句反馈，随后恢复常规信息。</summary>
    private void Flash(string message)
    {
        InfoText.Text = message;

        // 复用同一个计时器并重置：否则连续两条提示的计时器会互相覆盖，
        // 前一条到期时把后一条刚显示的内容抹掉。
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _flashTimer.Stop();
        _flashTimer.Tick -= OnFlashTick;
        _flashTimer.Tick += OnFlashTick;
        _flashTimer.Start();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        UpdateInfo();
    }

    // ---- 快捷键 ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            switch (e.Key)
            {
                case Key.T:
                    ToggleTopmost();
                    e.Handled = true;
                    return;
                case Key.C:
                    OnCopyClick(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                case Key.S:
                    SaveRequested?.Invoke(this);
                    e.Handled = true;
                    return;
                case Key.O:
                    OcrRequested?.Invoke(this);
                    e.Handled = true;
                    return;
                case Key.X:
                    ToggleTextSelect();
                    e.Handled = true;
                    return;
                case Key.D0 or Key.NumPad0:
                    ResetToOriginalSize();
                    e.Handled = true;
                    return;
            }
        }

        // 选择模式下：Esc 退出模式（而不是关掉贴图），Ctrl+A 全选，Ctrl+C 复制选区
        if (_selectMode)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (ctrl && e.Key == Key.A && _layout is not null)
            {
                _selection = _layout.SelectAll();
                RedrawHighlight();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.C)
            {
                CopySelection();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                ExitTextSelect();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        _flashTimer?.Stop();
        if (Mouse.Captured == this) Mouse.Capture(null);
        base.OnClosed(e);
        _onClosed?.Invoke(this);
    }
}
