using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardFloat.App.Services;
using ClipboardFloat.App.ViewModels;
using ClipboardFloat.Core;

namespace ClipboardFloat.App.Windows;

/// <summary>
/// 悬浮窗：常驻置顶 + 点击不夺焦点（WS_EX_NOACTIVATE）+ 毛玻璃 + 可拖动吸附。
/// 窄条与历史面板两种形态；搜索输入时临时允许激活；条目支持点击复制与拖拽。
/// </summary>
public partial class FloatingWindow : Window
{
    private const string AutoPasteOnTip = "自动粘贴：开（点击条目后自动粘贴到前台窗口）";
    private const string AutoPasteOffTip = "自动粘贴：关（点击仅复制）";
    private const string MonitoringOnTip = "剪贴板监听：开（点击暂停，暂停后不再记录新的复制内容）";
    private const string MonitoringOffTip = "剪贴板监听：关（点击开启）";

    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private HotkeyService? _hotkey;
    private SettingsWindow? _settingsWindow;
    private bool _dragging;
    private Point _dragStart;
    private bool _pinned;
    private bool _wakeTopmost; // 唤醒时的临时置顶（隐藏/取消置顶后清除）
    private bool _expanded;
    private DateTime _lastPointerOver = DateTime.UtcNow;
    private IntPtr _preSearchForeground;
    private IntPtr _foregroundAtExpand;
    private IntPtr _lastForeignForeground;
    private ClipItemViewModel? _contextItem;

    // 拖拽状态
    private bool _dragCandidate;
    private Point _dragStartPoint;
    private ClipItemViewModel? _dragItem;

    public FloatingWindow()
    {
        InitializeComponent();
        ShowActivated = false;
        ShowInTaskbar = false;

        _pinned = App.CurrentSettings.Pinned;

        // 先挂 SourceInitialized，再触碰窗口句柄相关逻辑
        SourceInitialized += OnSourceInitialized;

        ApplyPinned(_pinned);
        AutoPasteButton.Opacity = App.CurrentSettings.AutoPaste ? 1.0 : 0.5;
        AutoPasteButton.ToolTip = App.CurrentSettings.AutoPaste ? AutoPasteOnTip : AutoPasteOffTip;
        ApplyMonitoringState(App.CurrentSettings.MonitoringEnabled);
        HintText.Text = $"{App.CurrentSettings.PanelHotkey} 呼出历史 · 不抢焦点";

        // 定时器：仅置顶时才重申置顶（防止被其他置顶窗口盖住）；
        // 同时持续跟踪用户正在使用的前台窗口（供粘贴前归还焦点）
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += OnTopmostTimerTick;
        _topmostTimer.Start();

        // 展开面板在鼠标离开一段时间后自动折叠，避免遮挡
        _autoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoCollapseTimer.Tick += OnAutoCollapseTick;
        _autoCollapseTimer.Start();
    }

    private void OnTopmostTimerTick(object? sender, EventArgs e)
    {
        if (_pinned || _wakeTopmost) TopmostService.AssertTopmost(this);
        if (!IsActive)
        {
            var fg = TopmostService.GetForeground();
            if (fg != IntPtr.Zero && fg != new WindowInteropHelper(this).Handle)
                _lastForeignForeground = fg;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        TopmostService.MakeFloating(this, topmost: _pinned);
        // 取消置顶时同时恢复"正常窗口"行为（可激活、可被覆盖）
        TopmostService.SetNoActivate(this, on: _pinned);
        ReconfigureHotkeys();
    }

    /// <summary>按当前设置重新注册全局热键（设置页修改热键后调用）。</summary>
    public void ReconfigureHotkeys()
    {
        _hotkey?.Dispose();
        _hotkey = new HotkeyService(this);

        var panel = HotkeyParser.Parse(App.CurrentSettings.PanelHotkey);
        var paste = HotkeyParser.Parse(App.CurrentSettings.PasteHotkey);
        if (panel is { } p)
        {
            if (!_hotkey.Register(p.Modifiers | HotkeyService.MOD_NOREPEAT, p.Vk, ToggleVisibility))
                AppLog.Write($"面板热键注册失败: {App.CurrentSettings.PanelHotkey}");
        }
        else AppLog.Write($"面板热键无效: {App.CurrentSettings.PanelHotkey}");

        if (paste is { } p2)
        {
            if (!_hotkey.Register(p2.Modifiers | HotkeyService.MOD_NOREPEAT, p2.Vk, OnPasteLatestHotkey))
                AppLog.Write($"粘贴热键注册失败: {App.CurrentSettings.PasteHotkey}");
        }
        else AppLog.Write($"粘贴热键无效: {App.CurrentSettings.PasteHotkey}");

        // 悬浮查看选中内容热键（App 层执行：复制选中 → 打开置顶查看窗口）
        var view = HotkeyParser.Parse(App.CurrentSettings.ViewHotkey);
        if (view is { } v3)
        {
            if (!_hotkey.Register(v3.Modifiers | HotkeyService.MOD_NOREPEAT, v3.Vk, () => ViewSelectedRequested?.Invoke()))
                AppLog.Write($"查看热键注册失败: {App.CurrentSettings.ViewHotkey}");
        }
        else AppLog.Write($"查看热键无效: {App.CurrentSettings.ViewHotkey}");

        // 固定热键：修复键盘输入（重置卡住的修饰键）
        if (!_hotkey.Register(HotkeyService.MOD_CONTROL | HotkeyService.MOD_ALT | HotkeyService.MOD_NOREPEAT,
                HotkeyService.VK_K, OnResetKeysHotkey))
            AppLog.Write("热键 Ctrl+Alt+K 注册失败");
    }

    private void OnResetKeysHotkey()
    {
        PasteService.ReleaseModifiers();
        AppLog.Write("已重置键盘修饰键状态");
    }

    /// <summary>呼出/收起：隐藏→展开显示；展开→隐藏；窄条→展开。</summary>
    public void ToggleVisibility()
    {
        if (!IsVisible)
        {
            Show();
            Expand();
            WakeTopmost(); // 唤醒即临时置顶展示（无论当前置顶设置）
        }
        else if (_expanded)
        {
            Collapse();
            ClearWakeTopmost(); // 收起隐藏时恢复用户的置顶设置
            Hide();
        }
        else
        {
            Expand();
            WakeTopmost();
        }
    }

    /// <summary>唤醒：显示窗口并临时置顶展示（托盘双击/唤醒入口用，与置顶设置无关）。</summary>
    public void WakeToFront()
    {
        if (!IsVisible) Show();
        Expand();
        WakeTopmost();
        TopmostService.AssertTopmost(this);
    }

    private void WakeTopmost()
    {
        _wakeTopmost = true;
        TopmostService.SetTopmost(this, on: true);
    }

    private void ClearWakeTopmost()
    {
        if (!_wakeTopmost) return;
        _wakeTopmost = false;
        if (!_pinned) TopmostService.SetTopmost(this, on: false);
    }

    /// <summary>若本窗口处于激活状态（搜索输入或无障碍工具激活），返回应归还的前台窗口句柄。</summary>
    public IntPtr TakeForegroundTargetForPaste()
    {
        if (!IsActive) return IntPtr.Zero;
        var ourHwnd = new WindowInteropHelper(this).Handle;
        var target = _preSearchForeground != IntPtr.Zero ? _preSearchForeground
            : _foregroundAtExpand != IntPtr.Zero && _foregroundAtExpand != ourHwnd ? _foregroundAtExpand
            : _lastForeignForeground;
        _preSearchForeground = IntPtr.Zero;
        _foregroundAtExpand = IntPtr.Zero;
        return target;
    }

    /// <summary>应用置顶状态（设置页与图钉按钮共用）。
    /// 置顶=悬浮模式（不抢焦点）；取消置顶=正常窗口（可被切换激活的软件覆盖）。</summary>
    public void ApplyPinned(bool pinned)
    {
        _pinned = pinned;
        if (!pinned) _wakeTopmost = false; // 用户明确取消置顶时，同时清除临时置顶
        TopmostService.SetTopmost(this, pinned);
        TopmostService.SetNoActivate(this, on: pinned);
        PinButton.Opacity = pinned ? 1.0 : 0.5;
        PinButton.ToolTip = pinned ? "取消置顶" : "恢复置顶";
    }

    /// <summary>请求开启/关闭剪贴板监听（由 App 层执行真正的注册/注销与持久化）。</summary>
    public event Action<bool>? MonitoringToggleRequested;

    /// <summary>程序化同步开关状态中，忽略随之而来的 Checked/Unchecked 事件。</summary>
    private bool _syncingMonitorToggle;

    /// <summary>刷新监听开关外观（悬浮条开关、托盘菜单、设置页任一入口修改后统一调用）。</summary>
    public void ApplyMonitoringState(bool enabled)
    {
        if (MonitorToggle.IsChecked != enabled)
        {
            _syncingMonitorToggle = true;
            MonitorToggle.IsChecked = enabled;
            _syncingMonitorToggle = false;
        }
        MonitorToggle.ToolTip = enabled ? MonitoringOnTip : MonitoringOffTip;
        MonitorLabel.Foreground = enabled
            ? new SolidColorBrush(Color.FromRgb(0x7C, 0xE3, 0x8B))
            : new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
    }

    /// <summary>开关被用户点击（或无障碍工具切换）后：立即反馈，再由 App 同步真实监听状态。</summary>
    private void OnMonitorToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingMonitorToggle) return;
        var enabled = MonitorToggle.IsChecked == true;
        ApplyMonitoringState(enabled);
        MonitoringToggleRequested?.Invoke(enabled);
    }

    /// <summary>打开设置窗口（图钉旁 ⚙ 与托盘菜单共用）。</summary>
    public void OpenSettings()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(App.CurrentSettings, this, ReconfigureHotkeys);
        _settingsWindow.Show();
    }

    // ---- 面板形态 ----

    private void Expand()
    {
        _expanded = true;
        ExpandedPanel.Visibility = Visibility.Visible;
        ExpandButton.Content = "▴";
        ExpandButton.ToolTip = "折叠";
        HintText.Text = $"{App.CurrentSettings.PanelHotkey} 折叠 · 点文字可选中复制";
        _lastPointerOver = DateTime.UtcNow;
        // 记录展开时的前台窗口（目标应用），供粘贴前归还焦点；若前台就是自己则跳过
        if (_preSearchForeground == IntPtr.Zero)
        {
            var fg = TopmostService.GetForeground();
            if (fg != new WindowInteropHelper(this).Handle)
                _foregroundAtExpand = fg;
        }
    }

    private void Collapse()
    {
        _expanded = false;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        ExpandButton.Content = "▾";
        ExpandButton.ToolTip = "展开历史";
        HintText.Text = $"{App.CurrentSettings.PanelHotkey} 呼出历史 · 不抢焦点";
    }

    private void OnAutoCollapseTick(object? sender, EventArgs e)
    {
        if (!_expanded) return;
        if (IsMouseOver)
        {
            _lastPointerOver = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - _lastPointerOver > TimeSpan.FromSeconds(4))
            Collapse();
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
        => _lastPointerOver = DateTime.UtcNow;

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        // 同一个三角图标：展开/折叠切换
        if (_expanded) Collapse();
        else Expand();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
        => (DataContext as FloatingViewModel)?.ClearHistoryCommand.Execute(null);

    // ---- 键盘焦点输入区（搜索框 / 条目预览文本）：临时允许激活获取键盘焦点 ----

    private void BeginTemporaryActivation()
    {
        if (!_pinned) return; // 取消置顶时窗口本就是可激活的
        _preSearchForeground = TopmostService.GetForeground();
        TopmostService.AllowActivation(this, allow: true);
    }

    private void EndTemporaryActivation(bool returnForeground)
    {
        if (!_pinned) return;
        var target = _preSearchForeground;
        _preSearchForeground = IntPtr.Zero;
        TopmostService.AllowActivation(this, allow: false);
        if (returnForeground && target != IntPtr.Zero)
            TopmostService.ActivateWindow(target);
        TopmostService.AssertTopmost(this);
    }

    private void OnSearchGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => BeginTemporaryActivation();

    private void OnSearchLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => EndTemporaryActivation(returnForeground: false);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        // Esc：收起面板、清空搜索、恢复 NOACTIVATE、把前台还给之前的应用
        EndTemporaryActivation(returnForeground: true);
        SearchBox.Clear();
        Collapse();
    }

    private void OnContentGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => BeginTemporaryActivation();

    private void OnContentLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => EndTemporaryActivation(returnForeground: false);

    private void OnContentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        // Esc：退出文本选择模式，归还前台
        EndTemporaryActivation(returnForeground: true);
        Keyboard.ClearFocus();
    }

    // ---- 历史条目交互：点击复制 + 拖拽 ----

    private void OnHistoryListMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = false;
        if (e.ChangedButton != MouseButton.Left) return;
        var src = e.OriginalSource as DependencyObject;
        // 条目内工具按钮（查看完整内容等）：由按钮自身处理，不触发复制
        if (FindParent<Button>(src) is not null) return;
        // 预览文本区：用于选中文字，不触发整条复制
        if (FindParent<TextBox>(src) is not null) return;
        if (FindParent<ListBoxItem>(src)?.DataContext is not ClipItemViewModel vm) return;
        if (DataContext is not FloatingViewModel model) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            model.CopyPlainCommand.Execute(vm);          // Ctrl+单击：仅复制（原样）
        else
            model.CopyCommand.Execute(vm);               // 单击：复制（按设置自动粘贴）
        e.Handled = true;
    }

    private void OnHistoryListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        // 条目内工具按钮：交给按钮
        if (FindParent<Button>(src) is not null) return;
        // 预览文本区：文本选择/拖选优先，不启动条目拖拽
        if (FindParent<TextBox>(src) is not null) return;
        if (FindParent<ListBoxItem>(src)?.DataContext is ClipItemViewModel vm)
        {
            _dragCandidate = true;
            _dragItem = vm;
            _dragStartPoint = e.GetPosition(this);
        }
    }

    /// <summary>条目右侧"查看完整内容"快捷图标。</summary>
    private void OnItemViewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is ClipItemViewModel vm)
            OpenContentViewer(vm);
        e.Handled = true;
    }

    private void OnHistoryListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || _dragItem is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = false;
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = false;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(HistoryList, ClipboardWriter.BuildDataObject(item.Item), DragDropEffects.Copy);
        e.Handled = true;
    }

    private void OnHistoryListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindParent<ListBoxItem>(src)?.DataContext is ClipItemViewModel vm)
        {
            _contextItem = vm;
            HistoryList.ContextMenu ??= BuildItemMenu();
        }
        else
        {
            e.Handled = true; // 空白处不弹条目菜单
        }
    }

    private ContextMenu BuildItemMenu()
    {
        var menu = new ContextMenu();
        var view = new MenuItem { Header = "查看完整内容…" };
        view.Click += (_, _) => OpenContentViewer(_contextItem);
        menu.Items.Add(view);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("复制（自动粘贴）", model => model.CopyCommand.Execute(_contextItem)));
        menu.Items.Add(MenuItem("仅复制", model => model.CopyPlainCommand.Execute(_contextItem)));
        menu.Items.Add(MenuItem("复制为纯文本", model => model.CopyPlainCommand.Execute(_contextItem)));
        menu.Items.Add(MenuItem("粘贴为纯文本", model => model.PastePlainCommand.Execute(_contextItem)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("从历史删除", model => model.DeleteCommand.Execute(_contextItem)));
        return menu;
    }

    /// <summary>打开完整内容查看窗口（由 App 注入"写剪贴板前标记自身"回调以抑制回灌）。</summary>
    public Action? MarkSelfWriteAction { get; set; }

    /// <summary>触发"悬浮查看选中内容"热键（App 层实现：复制选中 → 打开置顶查看窗口）。</summary>
    public event Action? ViewSelectedRequested;

    /// <summary>以自动置顶方式打开完整内容查看窗口（热键流程用）。</summary>
    public void OpenPinnedViewer(ClipItem item)
    {
        var viewer = new ContentViewerWindow(item, MarkSelfWriteAction ?? (() => { }), autoPin: true);
        viewer.Show();
    }

    private void OpenContentViewer(ClipItemViewModel? vm)
    {
        if (vm is null) return;
        new ContentViewerWindow(vm.Item, MarkSelfWriteAction ?? (() => { })).Show();
    }

    private static MenuItem MenuItem(string header, Action<FloatingViewModel> action)
    {
        var item = new MenuItem { Header = header };
        item.Tag = action;
        item.Click += (_, _) =>
        {
            if (item.Tag is Action<FloatingViewModel> act
                && Window.GetWindow(item)?.DataContext is FloatingViewModel vm)
                act(vm);
        };
        return item;
    }

    // ---- 窄条按钮 ----

    private void OnAutoPasteClick(object sender, RoutedEventArgs e)
    {
        App.CurrentSettings.AutoPaste = !App.CurrentSettings.AutoPaste;
        SettingsStore.Save(App.CurrentSettings);
        AutoPasteButton.Opacity = App.CurrentSettings.AutoPaste ? 1.0 : 0.5;
        AutoPasteButton.ToolTip = App.CurrentSettings.AutoPaste ? AutoPasteOnTip : AutoPasteOffTip;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        App.CurrentSettings.Pinned = !_pinned;
        SettingsStore.Save(App.CurrentSettings);
        ApplyPinned(App.CurrentSettings.Pinned);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        Collapse();
        ClearWakeTopmost();
        Hide();
    }

    // ---- 热键动作 ----

    private void OnPasteLatestHotkey()
    {
        // Ctrl+Alt+P：把当前剪贴板内容粘贴到前台窗口
        // 先等热键修饰键松开再注入，避免 Alt 残留导致 Ctrl+V 变成 Ctrl+Alt+V
        PasteService.RunAfterModifiersReleased(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            var target = TakeForegroundTargetForPaste();
            PasteService.PasteToForeground(target);
            AppLog.Write("热键粘贴最新");
        })));
    }

    // ---- 拖动与边缘吸附 ----

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var src = e.OriginalSource as DependencyObject;
        if (FindParent<ButtonBase>(src) is not null) return;
        if (FindParent<ListBoxItem>(src) is not null) return;

        _dragStart = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (IsMouseOver) _lastPointerOver = DateTime.UtcNow;
        if (!_dragging) return;
        var p = e.GetPosition(this);
        Left += p.X - _dragStart.X;
        Top += p.Y - _dragStart.Y;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        SnapToEdges();
        e.Handled = true;
    }

    private void SnapToEdges()
    {
        var wa = SystemParameters.WorkArea;
        const double snap = 12;
        double nx = Left, ny = Top;

        if (Math.Abs(Left - wa.Left) < snap) nx = wa.Left;
        else if (Math.Abs(Left + Width - wa.Right) < snap) nx = wa.Right - Width;

        if (Math.Abs(Top - wa.Top) < snap) ny = wa.Top;
        else if (Math.Abs(Top + Height - wa.Bottom) < snap) ny = wa.Bottom - Height;

        if (Math.Abs(nx - Left) > 0.1 || Math.Abs(ny - Top) > 0.1)
        {
            Left = nx;
            Top = ny;
        }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
