using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using OneBox.Plugins.Screenshot.Controls;
using OneBox.Plugins.Screenshot.Core;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>
/// 贴图窗口：把截图钉在屏幕上，始终浮在其它窗口最上层，切换应用也不会被遮挡。
/// 支持拖动、鼠标滚轮缩放、右下角等比缩放，以及复制/保存/识别/关闭。
/// 图片下方第二行是编辑栏，可在贴图上绘制矩形/椭圆边框、箭头与文字，并逐步撤销。
/// </summary>
public partial class PinWindow : Window
{
    /// <summary>
    /// 重申置顶的间隔。开销很小（一次无 Z 序变更的 SetWindowPos），
    /// 但贴图可能有十几张，放宽到 1 秒足够应对“被普通窗口盖住”的情况。
    /// </summary>
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 底部操作工具条的高度（DIP）。窗口高度 = 图片显示高度 + 操作条 + 编辑条；
    /// 所有"按窗口高度反推图片显示高度"的地方都要先扣掉这些固定高度。
    /// </summary>
    internal const double ToolbarHeight = 34.0;

    /// <summary>图片下方第二行编辑栏的高度（DIP），与操作条同高。</summary>
    internal const double EditToolbarHeight = 34.0;

    /// <summary>图片下方的全部固定高度。</summary>
    internal const double ChromeHeight = ToolbarHeight + EditToolbarHeight;

    /// <summary>标注色块未选中时的边框颜色。</summary>
    private static readonly Brush SwatchBorderIdle =
        CreateFrozenBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    /// <summary>工具/形状按钮被选中时的底色。</summary>
    private static readonly Brush ActiveToolBrush =
        CreateFrozenBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6));

    /// <summary>文字框的选区颜色。</summary>
    private static readonly Brush TextEditorSelectionBrush =
        CreateFrozenBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6));

    private readonly BitmapSource _source;
    private readonly Action<PinWindow>? _onClosed;
    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _flashTimer;

    private bool _pinned = true;
    private bool _dragging;
    private bool _resizing;
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

    // ---- 贴图标注状态 ----
    private readonly PinAnnotationHistory _annotations = new();
    private PinEditTool _editTool;
    private PinShapeKind _shapeKind = PinShapeKind.Rectangle;
    private AnnotationColor _shapeColor = PinAnnotationPalette.DefaultColor;
    private double _shapeThickness = PinAnnotationLimits.DefaultThickness;
    private AnnotationColor _textColor = PinAnnotationPalette.DefaultColor;
    private double _textFontSize = PinAnnotationLimits.DefaultFontSize;

    private bool _drawingShape;
    private Point _shapeStartImage;
    private PinAnnotation? _shapeDraft;

    private bool _drawingArrow;
    private Point _arrowStartImage;
    private ArrowAnnotation? _arrowDraft;

    private Border? _textEditorHost;
    private TextBox? _textEditor;
    private Point _textOriginImage;
    private double _textMaxWidthImage;
    private IntPtr _foregroundBeforeEditing;
    private bool _endingTextEditing;
    private bool _suppressNextImageClick;

    private int _annotationVersion;
    private readonly List<Button> _shapeColorButtons = [];
    private readonly List<Button> _textColorButtons = [];

    private enum PinEditTool
    {
        None,
        Shape,
        Arrow,
        Text,
    }

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

    /// <summary>请求删除本贴图（连同已保存的文件，由插件主体决定）。</summary>
    public event Action<PinWindow>? DeleteRequested;

    /// <summary>标注新增或撤销后触发；插件据此清空 OCR 版面缓存。</summary>
    public event Action? AnnotationsChanged;

    /// <summary>本贴图已保存到的文件路径；未保存过为 null。</summary>
    public string? SavedFilePath { get; set; }

    /// <summary>
    /// 贴图窗口是否已关闭。识别是异步的，关闭贴图不会中断正在跑的识别，
    /// 但识别完成后的"写剪贴板 + 弹结果窗"对已关闭的贴图没有意义，
    /// 调用方用这个标记决定要不要落地结果（否则会出现"剪贴板被悄悄改掉"的怪现象）。
    /// </summary>
    public bool IsClosed { get; private set; }

    public PinWindow(BitmapSource source, Action<PinWindow>? onClosed = null)
    {
        InitializeComponent();
        _source = source;
        _onClosed = onClosed;
        _imageAspect = source.PixelWidth / (double)Math.Max(1, source.PixelHeight);

        ShotImage.Source = source;
        AnnotationLayer.SetImageSize(source.PixelWidth, source.PixelHeight);
        AnnotationLayer.SetAnnotations(_annotations.Items);

        // 选择文字按钮平时淡显，进入模式后点亮
        SelectTextButton.Opacity = 0.45;

        BuildColorPalettes();
        UpdateEditToolUi();

        // 初始尺寸：不超过屏幕的 60%，小图保持原样。
        // 窗口高度 = 图片高度 + 底部操作条 + 编辑条。
        var area = ScreenCapture.GetVirtualScreenBounds()
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.6 / source.PixelWidth,
                                            (area.Height - ChromeHeight) * 0.6 / source.PixelHeight));
        Width = Math.Max(80, source.PixelWidth * scale);
        Height = Math.Max(60, source.PixelHeight * scale) + ChromeHeight;

        // 默认放到屏幕右下角附近，避免盖住用户正在看的内容
        var areaDip = ScreenCapture.GetVirtualScreenDip(this);
        Left = areaDip.Left + areaDip.Width - Width - 40;
        Top = areaDip.Top + areaDip.Height - Height - 80;

        _topmostTimer = new DispatcherTimer { Interval = TopmostReassertInterval };
        // 保活重申走 ReassertTopmost：只确认仍在置顶组，不改动组内层叠次序。
        // 若这里用 AssertTopmost，多张贴图会每 1 秒互相插队，形成循环争抢。
        _topmostTimer.Tick += (_, _) => { if (_pinned) TopmostKeeper.ReassertTopmost(this); };

        // 缩放/改变窗口尺寸后，高亮矩形与标注要按新的显示比例重画
        ShotImage.SizeChanged += (_, _) =>
        {
            RedrawHighlight();
            AnnotationLayer.InvalidateVisual();
        };

        // 文字编辑期间点击编辑框外：先提交，再继续处理这次点击。
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;

        // 截图流程会临时隐藏贴图；隐藏时结束编辑状态，避免恢复显示后卡在标注模式。
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _editTool != PinEditTool.None) ExitEditTool();
        };

        UpdateInfo();
    }

    /// <summary>
    /// 把贴图移动到截图区域附近（区域截图/滚动截长图用）。
    /// <paramref name="physicalRegion"/> 是虚拟屏幕物理像素坐标，
    /// 高 DPI 与多屏负坐标都要换算成 DIP，否则位置会漂到主屏右下。
    /// </summary>
    public void MoveToRegion(System.Drawing.Rectangle physicalRegion)
    {
        var dip = ScreenCapture.ToDip(this, physicalRegion);

        // 区域很小（比如整窗截图贴着窗口边框）时贴图会完全盖住原区域，
        // 习惯做法是贴在区域正下方；下方放不下再回到正上方。
        var area = ScreenCapture.GetVirtualScreenDip(this);
        var top = dip.Bottom + 8;
        if (top + Height > area.Bottom) top = Math.Max(area.Top, dip.Top - Height - 8);

        var left = dip.Left;
        if (left + Width > area.Right) left = Math.Max(area.Left, area.Right - Width - 8);

        Left = left;
        Top = top;
    }

    /// <summary>贴图当前是否置顶。</summary>
    public bool IsPinned => _pinned;

    /// <summary>是否处于文字选择模式（诊断用）。</summary>
    public bool IsSelectMode => _selectMode;

    /// <summary>当前版面是否已识别出来（诊断用）。</summary>
    public bool HasLayout => _layout is not null;

    /// <summary>当前选区（诊断用）。</summary>
    public OcrSelection CurrentSelection => _selection;

    /// <summary>原始位图；导出时应改用 <see cref="ComposeImage"/>。</summary>
    public BitmapSource Source => _source;

    /// <summary>当前已提交的标注数量（诊断用）。</summary>
    public int AnnotationCount => _annotations.Count;

    /// <summary>当前是否有标注。</summary>
    public bool HasAnnotations => _annotations.Count > 0;

    /// <summary>标注版本号；OCR 识别期间标注发生变化时用于丢弃过期版面。</summary>
    public int AnnotationVersion => _annotationVersion;

    /// <summary>
    /// 生成包含全部已提交标注的位图，用于复制/保存/OCR。
    /// 没有标注时直接返回原图，不额外分配位图。
    /// </summary>
    public BitmapSource ComposeImage()
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(new Func<BitmapSource>(ComposeImage));

        if (_annotations.Count == 0) return _source;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(_source, new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));
            PinAnnotationRenderer.Draw(context, _annotations.Items, 1, 1, 1.0);
        }

        var target = new RenderTargetBitmap(
            _source.PixelWidth, _source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

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

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // 编辑栏交互时为了 Ctrl+Z 临时清除了 WS_EX_NOACTIVATE；
        // 失焦后恢复“点击不激活”，下一次编辑栏交互会重新临时打开。
        TopmostKeeper.SetNoActivate(this, true);
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
        // ApplyPasteStyle 在置顶时会无条件插队，因此这里不需要再调 AssertTopmost。
        // 文字编辑期间要保留激活权限，否则置顶状态一变化键盘焦点就被收走。
        TopmostKeeper.ApplyPasteStyle(this, pinned, noActivate: _textEditor is null);

        if (pinned) _topmostTimer.Start();
        else _topmostTimer.Stop();

        PinButton.Opacity = pinned ? 1.0 : 0.45;
        PinButton.ToolTip = pinned
            ? "已置顶：始终浮在最上层（Alt+T 取消置顶）"
            : "未置顶：可能被其他窗口遮挡（Alt+T 置顶）";
        UpdateInfo();
    }

    // ---- 拖动与缩放 ----
    //
    // 同一个 PointToScreen 结果不能被当成单一坐标空间的绝对值来用：
    // WPF 在 PER_MONITOR_AWARE 进程里把屏幕位置按「虚拟屏幕 × 全局缩放」渲染，
    // 而每块显示器又有自己的物理原点与缩放。所以这里只用 PointToScreen 的"差值"——
    // 差值恒等于鼠标的真实物理位移，与窗口自身移动、DPI 缩放、分辨率都无关；
    // 需要落到 Left/Top 时，再用本窗口的 DPI 把物理位移换算回布局单位。

    /// <summary>判定"用户要拖动"所需的位移（DIP），只用来滤掉按下时的鼠标抖动。</summary>
    private const double DragThreshold = 3.0;

    private Point _pressPointInWindow;
    private bool _dragStarted;

    /// <summary>上一次 MouseMove 的鼠标屏幕坐标（物理像素），用于累计位移。</summary>
    private Point _lastPhysical;

    /// <summary>
    /// 本次点击是否属于双击序列。不直接看抬起那一帧的 ClickCount：
    /// 贴图窗口带 WS_EX_NOACTIVATE，实测同一次双击里第二次按下报 ClickCount=2，
    /// 紧接着的抬起却报 1，只看某一帧会漏掉。
    /// </summary>
    private bool _clickIsDouble;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 点进正在编辑的文本框：交给 TextBox 自己处理，不启动拖动/标注
        if (_textEditor is not null && IsInside(e.OriginalSource as DependencyObject, _textEditorHost))
            return;

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

        // 刚刚为了提交文字而吞掉的那次点击，不再继续画新标注
        if (_suppressNextImageClick)
        {
            _suppressNextImageClick = false;
            e.Handled = true;
            return;
        }

        // 编辑工具启用时，左键拖动/点击用于标注，不再移动窗口
        if (_editTool == PinEditTool.Shape)
        {
            BeginShape(e.GetPosition(ShotImage));
            e.Handled = true;
            return;
        }

        if (_editTool == PinEditTool.Arrow)
        {
            BeginArrow(e.GetPosition(ShotImage));
            e.Handled = true;
            return;
        }

        if (_editTool == PinEditTool.Text)
        {
            BeginTextEditing(e.GetPosition(ShotImage));
            e.Handled = true;
            return;
        }

        // 文字选择模式下，左键拖动改为划选文字，不再移动窗口
        if (_selectMode)
        {
            HandleSelectMouseDown(e);
            return;
        }

        // 双击的第二次按下到此为止：不进入拖动，等抬起时关闭（见 OnMouseLeftButtonUp）
        if (e.ClickCount >= 2)
        {
            _clickIsDouble = true;
            _dragging = false;
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;

        _dragging = true;
        _dragStarted = false;
        _clickIsDouble = false;
        _pressPointInWindow = e.GetPosition(this);
        _lastPhysical = PointToScreen(_pressPointInWindow);
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>文字编辑期间点击编辑框外：先提交，再让本次点击继续落到工具条/图片/手柄上。</summary>
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // 点击编辑栏表示用户要编辑贴图：临时允许激活，让 Ctrl+Z 等键盘快捷键可用。
        // 点击图片本身仍保持“不抢焦点”，不影响下方应用。
        if (IsInside(source, EditToolbar)) EnsureKeyboardFocusForEditing();

        if (_textEditor is null) return;
        if (IsInside(source, _textEditorHost)) return;

        var onResizeHandle = IsInside(source, ResizeHandle);
        var onImage = !onResizeHandle && IsPointInsideImage(e.GetPosition(ShotImage));

        CommitTextEditing(restoreFocus: false);

        // 点击图片本身时，提交后不要再立刻新建一个文字框
        if (onImage) _suppressNextImageClick = true;
    }

    /// <summary>编辑栏交互时临时解除 WS_EX_NOACTIVATE，让贴图窗能接收 Ctrl+Z 等快捷键。</summary>
    private void EnsureKeyboardFocusForEditing()
    {
        TopmostKeeper.SetNoActivate(this, false);
        if (!IsActive) Activate();
        if (!IsKeyboardFocusWithin) Keyboard.Focus(this);
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

        if (_drawingArrow)
        {
            UpdateArrowDraft(e.GetPosition(ShotImage));
            return;
        }

        if (_drawingShape)
        {
            UpdateShapeDraft(e.GetPosition(ShotImage));
            return;
        }

        if (_selecting && _layout is not null)
        {
            ExtendSelectTo(e.GetPosition(ShotImage));
            return;
        }

        if (!_dragging && !_resizing) return;

        var current = e.GetPosition(this);

        if (_dragging)
        {
            // 先等位移超过阈值再真正挪窗：双击时两次按下之间有轻微抖动，
            // 若按下就拖动，第一次单击的抬起前贴图已经偏离鼠标，"鼠标右下"的错位就此产生。
            if (!_dragStarted)
            {
                if (Math.Abs(current.X - _pressPointInWindow.X) < DragThreshold &&
                    Math.Abs(current.Y - _pressPointInWindow.Y) < DragThreshold)
                {
                    return;
                }

                _dragStarted = true;

                // 真正开始拖动后再换成四向箭头，单击/双击时保持默认箭头
                Cursor = Cursors.SizeAll;
            }

            var physical = PointToScreen(current);

            // 用两次事件之间的屏幕位移移动窗口：该差值就是鼠标的真实物理位移，
            // 与窗口自身是否移动、当前 DPI 缩放、屏幕分辨率都无关；
            // 既不会因窗口被挪动而重复叠加，也不会因窗口移动触发额外的 MouseMove 而抖动。
            Left += PhysicalToLayout(physical.X - _lastPhysical.X);
            Top += PhysicalToLayout(physical.Y - _lastPhysical.Y);
            _lastPhysical = physical;
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
            Height = width / _imageAspect + ChromeHeight;
            UpdateInfo();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // 箭头/形状标注先结束，避免被双击关闭/文字选择逻辑接管
        if (_drawingArrow)
        {
            FinishArrow(e.GetPosition(ShotImage));
            e.Handled = true;
            return;
        }

        if (_drawingShape)
        {
            FinishShape(e.GetPosition(ShotImage));
            e.Handled = true;
            return;
        }

        // 划选结束：松开即复制，这是截图取字最常见的意图
        if (_selecting)
        {
            if (Mouse.Captured == this) ReleaseMouseCapture();
            FinishSelect();
            e.Handled = true;
            return;
        }

        // 双击图片：直接关闭预览（恢复原始尺寸改用 Alt+0 或工具条按钮）。
        // 认按下时记下的"本次点击属于双击序列"，而不是抬起这一帧的 ClickCount（见字段说明）。
        // 期间只要发生过拖动（_dragStarted），或者正在标注/选字，就不是双击。
        if (_clickIsDouble && !_dragStarted && _editTool == PinEditTool.None &&
            !_selectMode && _textEditor is null)
        {
            EndInteraction();
            Close();
            e.Handled = true;
            return;
        }

        EndInteraction();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // 鼠标捕获被系统收走时，正在拖的箭头/形状按取消处理，不留下半成品
        if (!_drawingShape && !_drawingArrow) return;

        _drawingShape = false;
        _drawingArrow = false;
        _shapeDraft = null;
        _arrowDraft = null;
        AnnotationLayer.SetDraft(null);

        // 捕获被系统收走属于本次标注中止，同样退回普通状态
        ExitEditTool();
    }

    private void OnResizeStart(object sender, MouseButtonEventArgs e)
    {
        if (_textEditor is not null) CommitTextEditing(restoreFocus: false);
        CancelDraft();

        _resizing = true;
        _resizeStart = e.GetPosition(this);
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// 屏幕物理像素位移 → WPF 布局单位（Left/Top 接受的单位）。
    ///
    /// 比例取自"平台给出的映射"与"本窗口的 DPI"两者之积：任何分辨率、任何放大比例下，
    /// 鼠标移动 N 像素，窗口就在屏幕上移动 N 像素，不会在高 DPI 下被放大、也不会在跨屏时错位。
    /// </summary>
    private double PhysicalToLayout(double physicalDelta)
    {
        var scale = PlatformLayoutScale();
        return Math.Abs(scale) < 1e-6 ? physicalDelta : physicalDelta / scale;
    }

    /// <summary>
    /// 当前窗口下"1 个布局单位 = 多少物理像素"。
    ///
    /// 不写死系统缩放：PerMonitorV2 下每块显示器比例不同，窗口跨屏后会变化；
    /// 取不到可信值时退回 1:1（与未声明 DPI 感知时的行为一致）。
    /// </summary>
    private double PlatformLayoutScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is { } target)
            {
                var m = target.TransformToDevice;
                if (m.M11 > 0 && Math.Abs(m.M11 - m.M22) < 1e-6) return m.M11;
            }
        }
        catch
        {
            // 回退到窗口 DPI
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0 && Math.Abs(dpi.DpiScaleX - dpi.DpiScaleY) < 1e-6) return dpi.DpiScaleX;
        }
        catch
        {
            // 保持 1:1
        }

        return 1.0;
    }

    /// <summary>
    /// 窗口被拖到另一台缩放比例不同的显示器上，或用户在系统设置里改了缩放。
    /// WPF 会按新 DPI 折算 Left/Top；贴图是无边框自绘窗口，这里保持左上角的物理位置不变，
    /// 只让尺寸按新 DPI 重排，避免跨屏时贴图在屏幕上"弹"一下。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        if (_textEditor is not null) CommitTextEditing(restoreFocus: false);

        // 换算前的物理位置（旧 DPI 下），此刻窗口尚未按新 DPI 重排
        var before = PhysicalTopLeft();

        base.OnDpiChanged(oldDpi, newDpi);

        if (oldDpi.DpiScaleX == newDpi.DpiScaleX && oldDpi.DpiScaleY == newDpi.DpiScaleY) return;

        if (newDpi.DpiScaleX > 0 && oldDpi.DpiScaleX > 0)
        {
            // 物理尺寸保持不变：换到高 DPI 屏幕上时，同样的物理面积对应更少的布局单位
            var physicalWidth = Width * oldDpi.DpiScaleX;
            var width = Math.Max(80, physicalWidth / newDpi.DpiScaleX);
            Width = width;
            Height = width / _imageAspect + ChromeHeight;
        }

        var after = PhysicalTopLeft();
        Left += (before.X - after.X) / Math.Max(newDpi.DpiScaleX, 1e-6);
        Top += (before.Y - after.Y) / Math.Max(newDpi.DpiScaleY, 1e-6);

        UpdateInfo();
    }

    /// <summary>
    /// 窗口左上角在屏幕上的位置（与 PointToScreen 同一空间，只用于求差值）。
    ///
    /// 必须做"是否已接入 PresentationSource"的检查：窗口首次 Show 时 WPF 会先挂根视觉、
    /// 再设置 DPI，这一刻 OnDpiChanged 就会被调用，而 PresentationSource 还没接上，
    /// PointToScreen 会直接抛 InvalidOperationException("此 Visual 未连接到 PresentationSource")。
    /// 这个异常从 Window.Show() 冒出来，会把整个截图流程炸掉（滚完一轮却什么都没得到）。
    /// </summary>
    private Point PhysicalTopLeft()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null) return new Point(Left, Top);

            return PointToScreen(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            // 尚未接入 PresentationSource：用布局坐标顶上，这里只求"前后差值"，
            // 调用方会再除以新 DPI，误差在 1 像素级别，不影响观感。
            return new Point(Left, Top);
        }
    }

    private void EndInteraction()
    {
        if (Mouse.Captured == this) ReleaseMouseCapture();
        _dragging = false;
        _dragStarted = false;
        _resizing = false;
        UpdateCursor();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // 正在画箭头/形状时不允许缩放，避免草稿坐标与显示比例错位
        if (_drawingShape || _drawingArrow)
        {
            e.Handled = true;
            return;
        }

        if (_textEditor is not null) CommitTextEditing(restoreFocus: false);

        // 滚轮缩放：按 1.1 倍步进
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var width = Math.Clamp(Width * factor, 60, _source.PixelWidth * 6.0);
        Width = width;
        Height = width / _imageAspect + ChromeHeight;
        UpdateInfo();
        e.Handled = true;
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => ResetToOriginalSize();

    private void ResetToOriginalSize()
    {
        if (_textEditor is not null) CommitTextEditing(restoreFocus: false);
        CancelDraft();

        // 目标宽度 = 原图像素宽；超过屏幕 90% 时回落到该上限（屏幕尺寸按 DIP 计）
        var area = ScreenCapture.GetVirtualScreenDip(this);
        var target = Math.Min(_source.PixelWidth, area.Width * 0.9);
        Width = Math.Max(80, target);
        Height = Width / _imageAspect + ChromeHeight;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        var zoom = Width / _source.PixelWidth * 100;
        var mode = _textEditor is not null ? " · 输入文字"
            : _editTool == PinEditTool.Shape ? " · 框选标注"
            : _editTool == PinEditTool.Arrow ? " · 箭头标注"
            : _editTool == PinEditTool.Text ? " · 文字标注"
            : _selectMode ? " · 选择文字"
            : "";
        var count = _annotations.Count > 0 ? $" · 标注 {_annotations.Count}" : "";
        InfoText.Text = $"{_source.PixelWidth} × {_source.PixelHeight} · {zoom:0}% · {(_pinned ? "置顶" : "未置顶")}{mode}{count}";
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

    /// <summary>source 是否位于 ancestor 的视觉子树内（含自身）。</summary>
    private static bool IsInside(DependencyObject? source, DependencyObject? ancestor)
    {
        if (ancestor is null) return false;

        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private bool IsPointInsideImage(Point inImage)
    {
        var width = ShotImage.ActualWidth;
        var height = ShotImage.ActualHeight;
        return width > 0 && height > 0 &&
               inImage.X >= 0 && inImage.Y >= 0 &&
               inImage.X <= width && inImage.Y <= height;
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
        // 编辑工具与 OCR 选字互斥
        if (_editTool != PinEditTool.None) ExitEditTool();

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
        UpdateCursor();
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
        var versionAtStart = _annotationVersion;
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

        // 识别期间用户可能已经退出模式、关掉了窗口，或又加/撤销了标注
        if (!_selectMode) return;

        if (versionAtStart != _annotationVersion)
        {
            ExitTextSelect();
            Flash("标注已变化，请重新进入选择文字");
            return;
        }

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
        SelectTextButton.Opacity = 0.45;
        _selection = default;
        HighlightLayer.Children.Clear();
        if (Mouse.Captured == this && !_dragging && !_resizing && !_drawingShape && !_drawingArrow)
            ReleaseMouseCapture();
        UpdateCursor();
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

    // ---- 贴图标注 ----

    private void BuildColorPalettes()
    {
        foreach (var choice in PinAnnotationPalette.Choices)
        {
            var shapeButton = CreateSwatchButton(choice, OnShapeColorClick);
            _shapeColorButtons.Add(shapeButton);
            ShapeColorPanel.Children.Add(shapeButton);

            var textButton = CreateSwatchButton(choice, OnTextColorClick);
            _textColorButtons.Add(textButton);
            TextColorPanel.Children.Add(textButton);
        }

        UpdateSwatchSelection(_shapeColorButtons, _shapeColor);
        UpdateSwatchSelection(_textColorButtons, _textColor);

        ShapeThicknessText.Text = _shapeThickness.ToString("0");
        TextSizeText.Text = _textFontSize.ToString("0");
    }

    private Button CreateSwatchButton(PinAnnotationColorChoice choice, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            Background = CreateBrush(choice.Color),
            Tag = choice,
            ToolTip = choice.Name,
        };
        button.Click += onClick;
        return button;
    }

    private void OnShapeColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PinAnnotationColorChoice choice }) return;

        _shapeColor = choice.Color;
        UpdateSwatchSelection(_shapeColorButtons, _shapeColor);
        Flash($"边框颜色：{choice.Name}");
    }

    private void OnTextColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PinAnnotationColorChoice choice }) return;

        _textColor = choice.Color;
        UpdateSwatchSelection(_textColorButtons, _textColor);
        Flash($"文字颜色：{choice.Name}");
    }

    private static void UpdateSwatchSelection(IReadOnlyList<Button> buttons, AnnotationColor selected)
    {
        foreach (var button in buttons)
        {
            if (button.Tag is not PinAnnotationColorChoice choice) continue;

            var isSelected = choice.Color == selected;
            button.BorderBrush = isSelected ? Brushes.White : SwatchBorderIdle;
            button.BorderThickness = new Thickness(isSelected ? 2 : 1);
        }
    }

    private void SetToolButtonState(Button button, bool active)
        => button.Background = active ? ActiveToolBrush : Brushes.Transparent;

    private void UpdateEditToolUi()
    {
        ShapeEditGroup.Visibility = _editTool == PinEditTool.Shape ? Visibility.Visible : Visibility.Collapsed;
        StrokeEditGroup.Visibility = _editTool is PinEditTool.Shape or PinEditTool.Arrow
            ? Visibility.Visible
            : Visibility.Collapsed;
        TextEditGroup.Visibility = _editTool == PinEditTool.Text ? Visibility.Visible : Visibility.Collapsed;

        SetToolButtonState(ShapeToolButton, _editTool == PinEditTool.Shape);
        SetToolButtonState(ArrowToolButton, _editTool == PinEditTool.Arrow);
        SetToolButtonState(TextToolButton, _editTool == PinEditTool.Text);
        SetToolButtonState(RectangleShapeButton, _shapeKind == PinShapeKind.Rectangle);
        SetToolButtonState(EllipseShapeButton, _shapeKind == PinShapeKind.Ellipse);

        UndoButton.IsEnabled = _annotations.CanUndo;
        UndoButton.Opacity = _annotations.CanUndo ? 1.0 : 0.45;

        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_dragging)
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        Cursor = _textEditor is not null || _editTool == PinEditTool.Text || _selectMode
            ? Cursors.IBeam
            : _editTool is PinEditTool.Shape or PinEditTool.Arrow
                ? Cursors.Cross
                : Cursors.Arrow;
    }

    private void OnToggleShapeTool(object sender, RoutedEventArgs e)
    {
        if (_editTool == PinEditTool.Shape)
        {
            ExitEditTool();
            return;
        }

        EnterEditTool(PinEditTool.Shape);
    }

    private void OnToggleArrowTool(object sender, RoutedEventArgs e)
    {
        if (_editTool == PinEditTool.Arrow)
        {
            ExitEditTool();
            return;
        }

        EnterEditTool(PinEditTool.Arrow);
    }

    private void OnToggleTextTool(object sender, RoutedEventArgs e)
    {
        if (_editTool == PinEditTool.Text)
        {
            ExitEditTool();
            return;
        }

        EnterEditTool(PinEditTool.Text);
    }

    private void EnterEditTool(PinEditTool tool)
    {
        if (_textEditor is not null) CommitTextEditing(restoreFocus: false, exitEditTool: false);
        if (_selectMode) ExitTextSelect();

        _editTool = tool;
        UpdateEditToolUi();
        UpdateInfo();
        Flash(tool switch
        {
            PinEditTool.Shape => "选择工具：拖动绘制一个边框（完成后自动退出）",
            PinEditTool.Arrow => "箭头：从起点拖到指向位置（完成后自动退出）",
            _ => "文字标注：点击输入一段文字（提交后自动退出）",
        });
    }

    private void ExitEditTool()
    {
        CancelDraft();
        if (_textEditor is not null) CommitTextEditing(restoreFocus: false, exitEditTool: false);

        // 退出编辑后恢复“点击不激活”；当前窗口若已激活仍可继续按 Ctrl+Z，
        // 直到用户切到其他应用。
        TopmostKeeper.SetNoActivate(this, true);

        _editTool = PinEditTool.None;
        UpdateEditToolUi();
        UpdateInfo();

        // 编辑栏激活过的窗口在单次操作后仍保持焦点，Ctrl+Z 可以立即撤销刚做的标注。
        if (IsActive && !IsKeyboardFocusWithin) Keyboard.Focus(this);
    }

    private void OnRectangleShapeClick(object sender, RoutedEventArgs e)
    {
        _shapeKind = PinShapeKind.Rectangle;
        UpdateEditToolUi();
        Flash("边框形状：矩形");
    }

    private void OnEllipseShapeClick(object sender, RoutedEventArgs e)
    {
        _shapeKind = PinShapeKind.Ellipse;
        UpdateEditToolUi();
        Flash("边框形状：椭圆");
    }

    private void OnShapeThicknessDown(object sender, RoutedEventArgs e)
        => AdjustShapeThickness(-1);

    private void OnShapeThicknessUp(object sender, RoutedEventArgs e)
        => AdjustShapeThickness(1);

    private void AdjustShapeThickness(double delta)
    {
        _shapeThickness = PinAnnotationLimits.ClampThickness(_shapeThickness + delta);
        ShapeThicknessText.Text = _shapeThickness.ToString("0");
        Flash($"线条粗细：{_shapeThickness:0}px");
    }

    private void OnTextSizeDown(object sender, RoutedEventArgs e)
        => AdjustTextSize(-2);

    private void OnTextSizeUp(object sender, RoutedEventArgs e)
        => AdjustTextSize(2);

    private void AdjustTextSize(double delta)
    {
        _textFontSize = PinAnnotationLimits.ClampFontSize(_textFontSize + delta);
        TextSizeText.Text = _textFontSize.ToString("0");
        Flash($"文字大小：{_textFontSize:0}px");
    }

    private void BeginShape(Point displayPoint)
    {
        _drawingShape = true;
        _shapeStartImage = ClampToImage(ToImagePixel(displayPoint));
        _shapeDraft = null;
        AnnotationLayer.SetDraft(null);
        CaptureMouse();
        UpdateCursor();
    }

    private void UpdateShapeDraft(Point displayPoint)
    {
        var end = ClampToImage(ToImagePixel(displayPoint));
        _shapeDraft = PinAnnotationFactory.CreateShape(
            _shapeKind,
            _shapeStartImage.X,
            _shapeStartImage.Y,
            end.X,
            end.Y,
            _shapeColor,
            _shapeThickness,
            _source.PixelWidth,
            _source.PixelHeight,
            minSize: 0.5);
        AnnotationLayer.SetDraft(_shapeDraft);
    }

    private void BeginArrow(Point displayPoint)
    {
        _drawingArrow = true;
        _arrowStartImage = ClampToImage(ToImagePixel(displayPoint));
        _arrowDraft = null;
        AnnotationLayer.SetDraft(null);
        CaptureMouse();
        UpdateCursor();
    }

    private void UpdateArrowDraft(Point displayPoint)
    {
        var end = ClampToImage(ToImagePixel(displayPoint));
        _arrowDraft = PinAnnotationFactory.CreateArrow(
            _arrowStartImage.X,
            _arrowStartImage.Y,
            end.X,
            end.Y,
            _shapeColor,
            _shapeThickness,
            _source.PixelWidth,
            _source.PixelHeight,
            minLength: 0.5);
        AnnotationLayer.SetDraft(_arrowDraft);
    }

    private void FinishArrow(Point displayPoint)
    {
        if (!_drawingArrow) return;

        var end = ClampToImage(ToImagePixel(displayPoint));
        _drawingArrow = false;
        if (Mouse.Captured == this) ReleaseMouseCapture();

        var arrow = PinAnnotationFactory.CreateArrow(
            _arrowStartImage.X,
            _arrowStartImage.Y,
            end.X,
            end.Y,
            _shapeColor,
            _shapeThickness,
            _source.PixelWidth,
            _source.PixelHeight);

        _arrowDraft = null;
        AnnotationLayer.SetDraft(null);

        // 单次操作：画完一个箭头立即退出箭头工具，恢复拖动/双击关闭
        ExitEditTool();

        if (arrow is null)
        {
            Flash("箭头太短，未添加标注");
            return;
        }

        AddAnnotation(arrow);
    }

    private void FinishShape(Point displayPoint)
    {
        if (!_drawingShape) return;

        var end = ClampToImage(ToImagePixel(displayPoint));
        _drawingShape = false;
        if (Mouse.Captured == this) ReleaseMouseCapture();

        var shape = PinAnnotationFactory.CreateShape(
            _shapeKind,
            _shapeStartImage.X,
            _shapeStartImage.Y,
            end.X,
            end.Y,
            _shapeColor,
            _shapeThickness,
            _source.PixelWidth,
            _source.PixelHeight);

        _shapeDraft = null;
        AnnotationLayer.SetDraft(null);

        // 单次操作：画完一个框立即退出选择工具，恢复拖动/双击关闭
        ExitEditTool();

        if (shape is null)
        {
            Flash("拖动范围太小，未添加标注");
            return;
        }

        AddAnnotation(shape);
    }

    private void CancelDraft()
    {
        _drawingShape = false;
        _shapeDraft = null;
        _drawingArrow = false;
        _arrowDraft = null;
        AnnotationLayer.SetDraft(null);
        if (Mouse.Captured == this) ReleaseMouseCapture();
        UpdateCursor();
    }

    private Point ClampToImage(Point point)
        => new(
            Math.Clamp(point.X, 0, _source.PixelWidth),
            Math.Clamp(point.Y, 0, _source.PixelHeight));

    private void AddAnnotation(PinAnnotation annotation)
    {
        _annotations.Add(annotation);
        OnAnnotationsChanged();

        AppLog.Write($"贴图标注新增：{DescribeAnnotation(annotation)}，共 {_annotations.Count}");
        Flash(annotation switch
        {
            ShapeAnnotation { Kind: PinShapeKind.Ellipse } => "已添加椭圆标注",
            ShapeAnnotation => "已添加矩形标注",
            TextAnnotation => "已添加文字标注",
            ArrowAnnotation => "已添加箭头标注",
            _ => "已添加标注",
        });
    }

    private void UndoAnnotation()
    {
        if (_drawingShape || _drawingArrow)
        {
            Flash("正在绘制，松开后再撤销");
            return;
        }

        if (_textEditor is not null)
        {
            Flash("请先完成文字输入");
            return;
        }

        var removed = _annotations.Undo();
        if (removed is null)
        {
            Flash("没有可撤销的标注");
            return;
        }

        OnAnnotationsChanged();
        AppLog.Write($"贴图标注撤销：{DescribeAnnotation(removed)}，剩余 {_annotations.Count}");
        Flash(removed switch
        {
            TextAnnotation => "已撤销文字标注",
            ArrowAnnotation => "已撤销箭头标注",
            _ => "已撤销形状标注",
        });
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoAnnotation();

    /// <summary>Ctrl+Z 命令：只在没有文字输入、没有正在拖框且有可撤销标注时可用。</summary>
    private void OnUndoCommandCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = _textEditor is null && !_drawingShape && !_drawingArrow && _annotations.CanUndo;
        e.Handled = true;
    }

    private void OnUndoCommandExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        UndoAnnotation();
        e.Handled = true;
    }

    private void OnAnnotationsChanged()
    {
        _annotationVersion++;
        AnnotationLayer.SetAnnotations(_annotations.Items);

        // 标注改变了合成图，OCR 版面（以及当前高亮）随即失效
        if (_selectMode) ExitTextSelect();
        _layout = null;
        _selection = default;
        HighlightLayer.Children.Clear();

        AnnotationsChanged?.Invoke();
        UpdateEditToolUi();
        UpdateInfo();
    }

    private static string DescribeAnnotation(PinAnnotation annotation) => annotation switch
    {
        ShapeAnnotation shape => shape.Kind == PinShapeKind.Ellipse ? "椭圆标注" : "矩形标注",
        ArrowAnnotation => "箭头标注",
        TextAnnotation text => $"文字标注「{Truncate(text.Text, 12)}」",
        _ => "标注",
    };

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    // ---- 文字标注编辑 ----

    private void BeginTextEditing(Point displayPoint)
    {
        if (_textEditor is not null) return;

        var displayWidth = ShotImage.ActualWidth;
        var displayHeight = ShotImage.ActualHeight;
        if (displayWidth <= 0 || displayHeight <= 0) return;

        var scaleX = displayWidth / _source.PixelWidth;
        var scaleY = displayHeight / _source.PixelHeight;
        if (scaleX <= 0 || scaleY <= 0) return;

        var left = Math.Clamp(displayPoint.X, 0, Math.Max(0, displayWidth - 1));
        var top = Math.Clamp(displayPoint.Y, 0, Math.Max(0, displayHeight - 1));

        var available = Math.Max(20, displayWidth - left - 4);
        var width = Math.Min(240, available);
        if (width < 80) left = Math.Max(0, displayWidth - width - 4);

        var maxHeight = Math.Max(40, displayHeight - top - 4);
        var editorWidth = Math.Max(10, width - 6);
        var lightText = IsLightColor(_textColor);

        var editor = new TextBox
        {
            Width = editorWidth,
            MinHeight = 24,
            MaxHeight = Math.Max(24, maxHeight - 8),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = PinAnnotationRenderer.AnnotationFontFamily,
            FontSize = Math.Clamp(_textFontSize * scaleY, 6, 200),
            Foreground = CreateBrush(_textColor),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(3, 2, 3, 2),
            CaretBrush = lightText ? Brushes.White : Brushes.Black,
            SelectionBrush = TextEditorSelectionBrush,
            SpellCheck = { IsEnabled = false },
        };

        var host = new Border
        {
            Width = width,
            MaxHeight = maxHeight,
            Background = CreateFrozenBrush(lightText
                ? Color.FromArgb(0xE6, 0x1B, 0x1B, 0x2F)
                : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = CreateFrozenBrush(Color.FromArgb(0xCC, 0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(2),
            Child = editor,
        };

        _textEditorHost = host;
        _textEditor = editor;
        _textOriginImage = new Point((left + 6) / scaleX, (top + 5) / scaleY);
        _textMaxWidthImage = Math.Max(1, Math.Max(1, editorWidth - 6) / scaleX);

        TextEditorLayer.Children.Add(host);
        Canvas.SetLeft(host, left);
        Canvas.SetTop(host, top);

        editor.PreviewKeyDown += OnTextEditorPreviewKeyDown;
        editor.LostKeyboardFocus += OnTextEditorLostKeyboardFocus;

        UpdateEditToolUi();
        UpdateInfo();
        Flash("输入文字：Ctrl+Enter 完成，Esc 取消");

        // 贴图窗默认 WS_EX_NOACTIVATE，必须先允许激活才能接收键盘
        _foregroundBeforeEditing = Native.GetForegroundWindow();
        TopmostKeeper.SetNoActivate(this, false);
        Activate();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(_textEditor, editor)) return;
            editor.Focus();
            Keyboard.Focus(editor);
        }));
    }

    private void OnTextEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTextEditing();
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            CommitTextEditing(restoreFocus: true);
        }
    }

    private void OnTextEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_textEditor is null || _endingTextEditing) return;

        // 等焦点真正落定后再决定；若焦点只是转到工具条按钮，同样按提交处理
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_textEditor is not null && !_endingTextEditing)
                CommitTextEditing(restoreFocus: false);
        }));
    }

    private void CommitTextEditing(bool restoreFocus, bool exitEditTool = true)
    {
        var editor = _textEditor;
        if (editor is null) return;

        var text = editor.Text;
        var origin = _textOriginImage;
        var maxWidth = _textMaxWidthImage;

        EndTextEditing(restoreFocus);

        // 单次操作：提交一段文字后立即退出文字工具，恢复拖动/双击关闭。
        // 由 ExitEditTool/EnterEditTool 内部调用时传 false，避免递归。
        if (exitEditTool) ExitEditTool();

        var annotation = PinAnnotationFactory.CreateText(
            origin.X,
            origin.Y,
            text,
            _textColor,
            _textFontSize,
            maxWidth,
            _source.PixelWidth,
            _source.PixelHeight);

        if (annotation is null)
        {
            if (!string.IsNullOrWhiteSpace(text)) Flash("文字标注无效");
            return;
        }

        AddAnnotation(annotation);
    }

    private void CancelTextEditing()
    {
        if (_textEditor is null) return;

        EndTextEditing(restoreFocus: true);
        ExitEditTool();
        Flash("已取消文字输入");
    }

    private void EndTextEditing(bool restoreFocus)
    {
        if (_textEditor is null || _endingTextEditing) return;

        var editor = _textEditor;
        var host = _textEditorHost;
        var previousForeground = _foregroundBeforeEditing;

        _endingTextEditing = true;
        try
        {
            _textEditor = null;
            _textEditorHost = null;
            _foregroundBeforeEditing = IntPtr.Zero;

            editor.PreviewKeyDown -= OnTextEditorPreviewKeyDown;
            editor.LostKeyboardFocus -= OnTextEditorLostKeyboardFocus;
            if (host is not null) TextEditorLayer.Children.Remove(host);

            // 恢复贴图的“不抢焦点”风格
            TopmostKeeper.SetNoActivate(this, true);

            UpdateEditToolUi();
            UpdateInfo();

            if (restoreFocus && previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground))
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero && previousForeground != handle)
                    Native.SetForegroundWindow(previousForeground);
            }
        }
        finally
        {
            _endingTextEditing = false;
        }
    }

    private static bool IsLightColor(AnnotationColor color)
    {
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance > 0.55;
    }

    private static SolidColorBrush CreateBrush(AnnotationColor color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---- 工具条 ----

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClipboardSupport.CopyImage(ComposeImage())) Flash("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            AppLog.Write($"合成带标注图片失败（复制）: {ex}");
            Flash("无法生成带标注的图片");
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this);

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

        // 文字编辑时键盘归 TextBox：Ctrl+Z 撤销输入、Esc 取消、Ctrl+Enter 提交都在 PreviewKeyDown 处理
        if (_textEditor is not null) return;

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
                case Key.Z:
                    UndoAnnotation();
                    e.Handled = true;
                    return;
                case Key.Delete:
                    DeleteRequested?.Invoke(this);
                    e.Handled = true;
                    return;
                case Key.D0 or Key.NumPad0:
                    ResetToOriginalSize();
                    e.Handled = true;
                    return;
            }
        }

        // 编辑工具启用时：Esc 先退出工具，而不是关掉贴图
        if (_editTool != PinEditTool.None && e.Key == Key.Escape)
        {
            ExitEditTool();
            e.Handled = true;
            return;
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
        if (_textEditor is not null) EndTextEditing(restoreFocus: false);

        IsClosed = true;
        _topmostTimer.Stop();
        _flashTimer?.Stop();
        if (Mouse.Captured == this) Mouse.Capture(null);
        TextEditorLayer.Children.Clear();
        base.OnClosed(e);
        _onClosed?.Invoke(this);
    }
}
