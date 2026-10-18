using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OneBox.Plugin.Sdk.Wpf;
using OneBox.Plugins.TextPin.Core;
using OneBox.Plugins.TextPin.Services;

namespace OneBox.Plugins.TextPin.Windows;

/// <summary>
/// 常驻置顶的纯文本框。支持拖动、右下角缩放、可选置顶与"点击不抢焦点"，
/// 内容改动后自动保存到插件数据目录。
///
/// 焦点策略：置顶模式下窗口带 WS_EX_NOACTIVATE，点击工具栏不会打断前台软件；
/// 但纯文本框必须能输入，所以鼠标按进正文区时临时允许激活，失焦后自动恢复，
/// 并把前台还给之前的目标窗口。
/// </summary>
public partial class PinnedWindow : Window
{
    private readonly TextPinPlugin _plugin;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _saveTimer;

    private SettingsWindow? _settingsWindow;

    private bool _pinned;
    private bool _noActivate;
    private bool _dragging;
    private bool _resizing;
    private Point _dragStart;
    private Point _resizeStart;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private IntPtr _preEditForeground;
    private IntPtr _lastForeignForeground;

    /// <summary>正在程序化设置文本（加载/粘贴），不触发自动保存与状态刷新。</summary>
    private bool _suppressTextEvents;

    public PinnedWindow(TextPinPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ShowActivated = false;
        ShowInTaskbar = false;

        _pinned = plugin.Settings.Pinned;
        _noActivate = plugin.Settings.NoActivate;

        // 先挂 SourceInitialized，再触碰窗口句柄相关逻辑
        SourceInitialized += OnSourceInitialized;

        ApplyPinned(_pinned);
        ApplyNoActivate(_noActivate);
        ApplyFontSize(plugin.Settings.FontSize);
        ApplyWordWrap(plugin.Settings.WordWrap);
        UpdateHintText();
        UpdateStatus();

        // 定时重申置顶：只确认仍在置顶组，不改动组内层叠次序，
        // 避免和其他置顶窗口互相插队。同时持续跟踪用户正在使用的前台窗口。
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += OnTopmostTimerTick;
        _topmostTimer.Start();

        // 自动保存：输入过程中不做磁盘写，停顿一下再落盘
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Start();
    }

    /// <summary>当前文本。</summary>
    public string Text => EditorBox.Text;

    // ---- 文本 ----

    /// <summary>程序化设置文本（启动加载、粘贴读入），不触发自动保存。</summary>
    public void SetTextSilently(string? text)
    {
        _suppressTextEvents = true;
        EditorBox.Text = PlainText.NormalizeNewlines(text);
        EditorBox.CaretIndex = EditorBox.Text.Length;
        _suppressTextEvents = false;
        UpdateStatus();
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextEvents) return;

        // 超出上限时截断（例如粘贴进来一个超大文本）
        var max = _plugin.Settings.MaxTextLength;
        if (max > 0 && EditorBox.Text.Length > max)
        {
            var caret = EditorBox.CaretIndex;
            _suppressTextEvents = true;
            EditorBox.Text = PlainText.Truncate(EditorBox.Text, max);
            _suppressTextEvents = false;
            EditorBox.CaretIndex = Math.Min(caret, EditorBox.Text.Length);
            SetStatus($"内容超过 {max} 字上限，已截断");
        }
        else
        {
            UpdateStatus();
        }

        // 每敲一个字就发一条 IPC 状态上报没必要，统一收敛到下面的保存节流里
        _saveTimer.Stop();
        _saveTimer.Start(); // 停顿 1 秒后落盘并刷新宿主状态
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveNow();
    }

    /// <summary>立即保存（失焦、隐藏、插件退出时调用），并刷新宿主里显示的状态。</summary>
    public void SaveNow()
    {
        _plugin.SaveText(EditorBox.Text);
        _plugin.NotifyStatus(EditorBox.Text);
    }

    /// <summary>抓取进行中的提示（抓取要等 Ctrl+C 生效，通常一两百毫秒内完成）。</summary>
    public void ShowBusy(string message) => SetStatus(message);

    /// <summary>
    /// 接受抓取到的选中文字：替换正文、立即落盘，并把窗口显示出来。
    /// 与"读入剪贴板"共用同一套截断与保存逻辑。
    /// </summary>
    public void AdoptCapturedText(string text)
    {
        SetTextSilently(PlainText.Sanitize(text, _plugin.Settings.MaxTextLength));
        SaveNow();

        if (!IsVisible) Show();
        TopmostService.BringToFront(new WindowInteropHelper(this).Handle);
        if (_pinned) TopmostService.AssertTopmost(this);

        SetStatus($"已置顶选中文字 · {TextStats.Measure(EditorBox.Text).Describe()}");
    }

    /// <summary>抓取失败提示。窗口已有内容时只提示，不清空，避免误伤已有文本。</summary>
    public void ShowCaptureFailed(string error) => SetStatus(error);

    /// <summary>清空文本（托盘菜单）。</summary>
    public void ClearText()
    {
        SetTextSilently("");
        SaveNow();
        SetStatus("已清空");
    }

    private void UpdateStatus()
    {
        var stats = TextStats.Measure(EditorBox.Text);
        StatusText.Text = $"{stats.Describe()} · {(_pinned ? "置顶" : "未置顶")}";
    }

    private void SetStatus(string message) => StatusText.Text = message;

    /// <summary>热键文本变化后刷新提示（设置页修改热键后调用）。</summary>
    public void UpdateHintText()
        => HintText.Text = $"{_plugin.EffectiveHotkey(TextPinPlugin.HotkeyToggle, _plugin.Settings.ToggleHotkey)} 显示/隐藏";

    // ---- 置顶与不抢焦点 ----

    private void OnSourceInitialized(object? sender, EventArgs e)
        => TopmostService.MakeFloating(this, topmost: _pinned, noActivate: _noActivate);

    private void OnTopmostTimerTick(object? sender, EventArgs e)
    {
        if (_pinned) TopmostService.AssertTopmost(this);

        if (!IsActive)
        {
            var fg = TopmostService.GetForeground();
            if (fg != IntPtr.Zero && fg != new WindowInteropHelper(this).Handle)
                _lastForeignForeground = fg;
        }
    }

    /// <summary>应用置顶状态（设置页与图钉按钮共用）。</summary>
    public void ApplyPinned(bool pinned)
    {
        _pinned = pinned;
        TopmostService.SetTopmost(this, pinned);
        PinButton.Opacity = pinned ? 1.0 : 0.5;
        PinButton.ToolTip = pinned ? "取消置顶" : "恢复置顶";
        UpdateStatus();
    }

    /// <summary>应用"不抢焦点"（悬浮模式）。关闭后窗口按普通窗口参与焦点切换。</summary>
    public void ApplyNoActivate(bool noActivate)
    {
        _noActivate = noActivate;
        // 正在输入时不要立刻收回激活权限，否则光标会被打断
        if (!EditorBox.IsKeyboardFocusWithin)
            TopmostService.SetNoActivate(this, on: noActivate);
    }

    public void ApplyFontSize(double size)
    {
        var clamped = Math.Clamp(size, 9, 32);
        EditorBox.FontSize = clamped;
    }

    public void ApplyWordWrap(bool wrap)
    {
        EditorBox.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        EditorBox.HorizontalScrollBarVisibility = wrap
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    /// <summary>若本窗口正被激活（用户正在输入），返回应归还的前台窗口句柄。</summary>
    public IntPtr TakeForegroundTargetForPaste()
    {
        if (!IsActive) return IntPtr.Zero;
        var target = _preEditForeground != IntPtr.Zero ? _preEditForeground : _lastForeignForeground;
        _preEditForeground = IntPtr.Zero;
        return target;
    }

    // ---- 焦点：正文区需要键盘输入，临时允许激活 ----

    private void OnEditorPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 悬浮模式下点击正文时临时允许激活，否则点了也输入不了
        if (_noActivate && !IsActive)
        {
            _preEditForeground = TopmostService.GetForeground();
            TopmostService.AllowActivation(this, allow: true);
        }
    }

    private void OnEditorGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_noActivate && _preEditForeground == IntPtr.Zero)
            _preEditForeground = TopmostService.GetForeground();
        if (_noActivate) TopmostService.AllowActivation(this, allow: true);
    }

    private void OnEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_noActivate) TopmostService.AllowActivation(this, allow: false);
        SaveNow();
    }

    /// <summary>正文快捷键：Ctrl+Enter 复制并粘贴，Esc 失焦收起。</summary>
    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Keyboard.ClearFocus();
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            CopyToClipboard(autoPaste: true);
        }
    }

    // ---- 工具栏动作 ----

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.Pinned = !_pinned;
        _plugin.SaveSettings();
        ApplyPinned(_plugin.Settings.Pinned);
    }

    /// <summary>把剪贴板里的纯文本读入（一次性读取，不是监听）。</summary>
    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteFromClipboard(alsoPaste: false);

    /// <summary>复制全部文本；<paramref name="alsoPaste"/> 为真时接着粘贴到前台窗口。</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
        => CopyToClipboard(autoPaste: _plugin.Settings.AutoPaste);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        SaveNow();
        Hide();
    }

    /// <summary>把剪贴板里的纯文本读入文本框。</summary>
    /// <param name="alsoPaste">读入后是否再粘贴回前台窗口（热键流程用）。</param>
    public void PasteFromClipboard(bool alsoPaste)
    {
        var text = ClipboardText.TryRead();
        if (text is null)
        {
            SetStatus("剪贴板里没有文本");
            return;
        }

        SetTextSilently(PlainText.Sanitize(text, _plugin.Settings.MaxTextLength));
        SaveNow();
        SetStatus($"已读入剪贴板文本 · {TextStats.Measure(EditorBox.Text).Describe()}");

        if (alsoPaste) CopyToClipboard(autoPaste: true);
    }

    /// <summary>把当前文本复制到剪贴板，可选接着粘贴到前台窗口。</summary>
    public void CopyToClipboard(bool autoPaste)
    {
        var text = EditorBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("没有可复制的内容");
            return;
        }

        _plugin.CopyText(text, autoPaste && _plugin.Settings.AutoPaste);
        SetStatus(autoPaste && _plugin.Settings.AutoPaste ? "已复制并粘贴" : "已复制到剪贴板");

        // 短暂反馈，避免用户不确定有没有生效
        CopyButton.Content = "✓";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = "📋";
        };
        timer.Start();
    }

    // ---- 显示/隐藏与设置窗 ----

    /// <summary>显示/隐藏切换（热键与托盘菜单共用）。</summary>
    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            SaveNow();
            Hide();
        }
        else
        {
            Show();
            TopmostService.BringToFront(new WindowInteropHelper(this).Handle);
            if (_pinned) TopmostService.AssertTopmost(this);
        }
    }

    /// <summary>打开设置窗口。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_plugin, this);
        PluginTheme.ApplyToWindow(_settingsWindow);
        _settingsWindow.Show();
    }

    /// <summary>宿主改键后，让已打开的设置窗口也刷新键位显示与"未生效"提示。</summary>
    public void RefreshSettingsHotkeys() => _settingsWindow?.RefreshHotkeys();

    /// <summary>设置窗口改了热键后刷新提示与宿主注册结果。</summary>
    public void OnSettingsChanged()
    {
        ApplyPinned(_plugin.Settings.Pinned);
        ApplyNoActivate(_plugin.Settings.NoActivate);
        ApplyFontSize(_plugin.Settings.FontSize);
        ApplyWordWrap(_plugin.Settings.WordWrap);
        UpdateHintText();
    }

    // ---- 拖动与缩放 ----

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var src = e.OriginalSource as DependencyObject;
        // 按钮与文本框各自处理自己的交互，不触发窗口拖动
        if (FindParent<ButtonBase>(src) is not null) return;
        if (FindParent<TextBox>(src) is not null) return;

        _dragStart = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_resizing)
        {
            var pos = e.GetPosition(this);
            Width = Math.Max(MinWidth, _resizeStartWidth + (pos.X - _resizeStart.X));
            Height = Math.Max(MinHeight, _resizeStartHeight + (pos.Y - _resizeStart.Y));
            return;
        }

        if (_dragging)
        {
            var pos = e.GetPosition(this);
            Left += pos.X - _dragStart.X;
            Top += pos.Y - _dragStart.Y;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_resizing)
        {
            _resizing = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}