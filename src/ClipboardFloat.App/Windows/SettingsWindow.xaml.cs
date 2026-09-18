using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipboardFloat.App.Services;
using ClipboardFloat.App.ViewModels;
using ClipboardFloat.Core;

namespace ClipboardFloat.App.Windows;

/// <summary>设置窗口：修改即应用并自动保存；支持热键录制。</summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly FloatingWindow _floatingWindow;
    private readonly Action? _onHotkeysChanged;

    private bool _recordingPanel;
    private bool _recordingPaste;
    private bool _recordingView;
    private bool _loading;
    private HotkeyRecorder? _recorder;
    private DispatcherTimer? _recordTimer;

    public SettingsWindow(Settings settings, FloatingWindow floatingWindow, Action? onHotkeysChanged = null)
    {
        InitializeComponent();
        _settings = settings;
        _floatingWindow = floatingWindow;
        _onHotkeysChanged = onHotkeysChanged;

        // 深色标题栏（DWM 沉浸式暗色模式）
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };

        AutoPasteCheck.IsChecked = settings.AutoPaste;
        PinnedCheck.IsChecked = settings.Pinned;
        _loading = true;
        AutoStartCheck.IsChecked = AutoStartService.IsEnabled(); // 以注册表为准
        MonitorCheck.IsChecked = settings.MonitoringEnabled;
        _loading = false;
        MaxHistoryBox.Text = settings.MaxHistory.ToString();
        DataDirText.Text = "数据目录：" + AppPaths.DataDirectory;
        PanelHotkeyButton.Content = settings.PanelHotkey;
        PasteHotkeyButton.Content = settings.PasteHotkey;
        ViewHotkeyButton.Content = settings.ViewHotkey;

        Loaded += (_, _) => Activate();
        Closed += (_, _) =>
        {
            _recordTimer?.Stop();
            _recorder?.Dispose();
            _recorder = null;
        };
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnAutoPasteChanged(object sender, RoutedEventArgs e)
    {
        _settings.AutoPaste = AutoPasteCheck.IsChecked == true;
        Save();
    }

    private void OnPinnedChanged(object sender, RoutedEventArgs e)
    {
        _settings.Pinned = PinnedCheck.IsChecked == true;
        _floatingWindow.ApplyPinned(_settings.Pinned);
        Save();
    }

    /// <summary>监听开关：交给 App 层统一注册/注销剪贴板监听器（同时刷新悬浮条开关与托盘勾选）。</summary>
    private void OnMonitorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        (Application.Current as App)?.SetMonitoring(MonitorCheck.IsChecked == true);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AutoStartCheck.IsChecked == true)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                AutoStartService.Enable(exePath);
                AutoStartTip.Text = $"已启用：开机自动启动 {exePath}";
            }
        }
        else
        {
            AutoStartService.Disable();
            AutoStartTip.Text = "已关闭开机自动启动";
        }
    }

    // ---- 热键录制 ----

    private void OnRecordPanelHotkey(object sender, RoutedEventArgs e) => StartRecording(panel: true);

    private void OnRecordPasteHotkey(object sender, RoutedEventArgs e) => StartRecording(panel: false, view: false);

    private void OnRecordViewHotkey(object sender, RoutedEventArgs e) => StartRecording(panel: false, view: true);

    private void StartRecording(bool panel, bool view = false)
    {
        _recordingPanel = panel;
        _recordingPaste = !panel && !view;
        _recordingView = view;
        ShowHotkeyWarning(null);
        var button = panel ? PanelHotkeyButton : view ? ViewHotkeyButton : PasteHotkeyButton;
        button.Content = "按下新组合键…";
        button.Focus();

        // 低级键盘钩子：能捕获被其他软件占用的组合键
        _recorder ??= new HotkeyRecorder();
        _recorder.KeyCaptured -= OnKeyCaptured;
        _recorder.KeyCaptured += OnKeyCaptured;
        _recorder.Start();

        // 超时提示：按了却没捕获到，多半是被其他软件占用的组合
        _recordTimer?.Stop();
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _recordTimer.Tick += (_, _) =>
        {
            _recordTimer.Stop();
            if (_recordingPanel || _recordingPaste || _recordingView)
                ShowHotkeyWarning("未检测到按键——若已按组合键却没反应，说明该组合可能已被其他软件占用");
        };
        _recordTimer.Start();
    }

    private void OnKeyCaptured(Key key, ModifierKeys modifiers)
    {
        if (!_recordingPanel && !_recordingPaste && !_recordingView) return;

        if (key == Key.Escape)
        {
            _recordTimer?.Stop();
            CancelRecording();
            return;
        }

        var combo = HotkeyInputParser.FromWpfKey(key, modifiers);
        if (combo is null) return; // 纯修饰键等

        _recordTimer?.Stop();
        _recorder!.KeyCaptured -= OnKeyCaptured;

        // 冲突检查：仅警告，不阻止保存
        string? warning = CheckHotkeyConflict(combo);

        if (_recordingPanel) _settings.PanelHotkey = combo;
        else if (_recordingView) _settings.ViewHotkey = combo;
        else _settings.PasteHotkey = combo;
        Save();
        _onHotkeysChanged?.Invoke();
        CancelRecording();
        ShowHotkeyWarning(warning);
    }

    /// <summary>检查新热键冲突：与自身其他快捷键重复 / 与其他软件占用。返回警告文案（无冲突返回 null）。</summary>
    private string? CheckHotkeyConflict(string combo)
    {
        // 自身重复检查（排除当前正在修改的项）
        var current = _recordingPanel ? _settings.PanelHotkey
            : _recordingView ? _settings.ViewHotkey
            : _settings.PasteHotkey;
        var others = new[] { _settings.PanelHotkey, _settings.PasteHotkey, _settings.ViewHotkey }
            .Where(h => !string.Equals(h, current, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selfConflict = HotkeyConflictChecker.FindSelfConflict(combo, others);
        if (selfConflict is not null) return selfConflict;

        // 探测是否被其他进程占用
        var parsed = HotkeyParser.Parse(combo);
        if (parsed is { } pk)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!HotkeyService.ProbeAvailable(pk.Modifiers, pk.Vk, hwnd))
                return HotkeyConflictChecker.OtherAppConflictMessage;
        }
        return null;
    }

    private void ShowHotkeyWarning(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            HotkeyWarningText.Text = "";
            HotkeyWarningText.Visibility = Visibility.Collapsed;
        }
        else
        {
            HotkeyWarningText.Text = message;
            HotkeyWarningText.Visibility = Visibility.Visible;
        }
    }

    private void CancelRecording()
    {
        _recordingPanel = _recordingPaste = _recordingView = false;
        PanelHotkeyButton.Content = _settings.PanelHotkey;
        PasteHotkeyButton.Content = _settings.PasteHotkey;
        ViewHotkeyButton.Content = _settings.ViewHotkey;
    }

    // ---- 其他 ----

    private void OnMaxHistoryLostFocus(object sender, RoutedEventArgs e) => ApplyMaxHistory();

    private void OnMaxHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyMaxHistory();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyMaxHistory()
    {
        if (int.TryParse(MaxHistoryBox.Text.Trim(), out var n) && n is > 0 and <= 10000)
        {
            _settings.MaxHistory = n;
            Save();
        }
        else
        {
            MaxHistoryBox.Text = _settings.MaxHistory.ToString();
        }
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_floatingWindow.DataContext is FloatingViewModel vm)
            vm.ClearHistoryCommand.Execute(null);
    }

    private void Save() => SettingsStore.Save(_settings);
}
