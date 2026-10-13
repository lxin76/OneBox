using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OneBox.Plugins.Clipboard.Core;
using OneBox.Plugins.Clipboard.Services;

namespace OneBox.Plugins.Clipboard.Windows;

/// <summary>剪贴板插件设置窗口：修改即应用并自动保存；热键录制后交给宿主统一注册。</summary>
public partial class SettingsWindow : Window
{
    private readonly ClipboardPlugin _plugin;
    private readonly FloatingWindow _floatingWindow;

    private bool _recordingPanel;
    private bool _recordingPaste;
    private bool _recordingView;
    private bool _loading;
    private HotkeyRecorder? _recorder;
    private DispatcherTimer? _recordTimer;

    public SettingsWindow(ClipboardPlugin plugin, FloatingWindow floatingWindow)
    {
        InitializeComponent();
        _plugin = plugin;
        _floatingWindow = floatingWindow;

        // 深色标题栏（DWM 沉浸式暗色模式）
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };

        var settings = plugin.Settings;
        AutoPasteCheck.IsChecked = settings.AutoPaste;
        PinnedCheck.IsChecked = settings.Pinned;
        _loading = true;
        // 开机自启属于宿主级设置，插件设置页只展示状态
        AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
        AutoStartCheck.IsEnabled = false;
        MonitorCheck.IsChecked = settings.MonitoringEnabled;
        _loading = false;
        MaxHistoryBox.Text = settings.MaxHistory.ToString();
        DataDirText.Text = "数据目录：" + AppPaths.DataDirectory;
        PanelHotkeyButton.Content = settings.PanelHotkey;
        PasteHotkeyButton.Content = settings.PasteHotkey;
        ViewHotkeyButton.Content = settings.ViewHotkey;

        ShowHotkeyWarning(BuildHotkeyWarning());

        Loaded += (_, _) => Activate();
        Closed += (_, _) =>
        {
            _recordTimer?.Stop();
            _recorder?.Dispose();
            _recorder = null;
        };
    }

    /// <summary>宿主注册热键时的失败项提示（例如被其他小工具或软件占用）。</summary>
    private string? BuildHotkeyWarning()
    {
        var failed = _plugin.HotkeyResults.Where(r => !r.Registered).ToList();
        if (failed.Count == 0) return null;

        // 固定热键失败不是用户改的，单独说明更清楚
        var lines = failed.Select(f => $"· {f.Combination}：{f.Error}");
        return "⚠ 以下热键未能生效\n" + string.Join("\n", lines);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnAutoPasteChanged(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.AutoPaste = AutoPasteCheck.IsChecked == true;
        _plugin.SaveSettings();
    }

    private void OnPinnedChanged(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.Pinned = PinnedCheck.IsChecked == true;
        _floatingWindow.ApplyPinned(_plugin.Settings.Pinned);
        _plugin.SaveSettings();
    }

    /// <summary>监听开关：交给插件主体统一注册/注销剪贴板监听器（同时刷新悬浮条开关与托盘勾选）。</summary>
    private void OnMonitorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _plugin.SetMonitoring(MonitorCheck.IsChecked == true);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        // 开机自启由宿主管理，插件内不处理
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

        // 自身重复检查（仅警告，不阻止保存）；跨插件与外部占用由宿主仲裁后回报
        var warning = CheckSelfConflict(combo);

        if (_recordingPanel) _plugin.Settings.PanelHotkey = combo;
        else if (_recordingView) _plugin.Settings.ViewHotkey = combo;
        else _plugin.Settings.PasteHotkey = combo;

        _plugin.SaveSettings();
        CancelRecording();
        _floatingWindow.UpdateHintText();
        ShowHotkeyWarning(warning);

        // 重新向宿主声明热键；宿主注册完成后回报结果
        _ = ReapplyHotkeysAsync();
    }

    private async Task ReapplyHotkeysAsync()
    {
        await _plugin.DeclareHotkeysAsync();
        ShowHotkeyWarning(BuildHotkeyWarning() ?? CheckSelfConflict(_plugin.Settings.PanelHotkey));
    }

    /// <summary>检查新热键是否与自身其他快捷键或固定热键重复。</summary>
    private string? CheckSelfConflict(string combo)
    {
        var current = _recordingPanel ? _plugin.Settings.PanelHotkey
            : _recordingView ? _plugin.Settings.ViewHotkey
            : _plugin.Settings.PasteHotkey;

        var others = new[]
            {
                _plugin.Settings.PanelHotkey,
                _plugin.Settings.PasteHotkey,
                _plugin.Settings.ViewHotkey,
            }
            .Where(h => !string.Equals(h, current, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return HotkeyConflictChecker.FindSelfConflict(combo, others);
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
        PanelHotkeyButton.Content = _plugin.Settings.PanelHotkey;
        PasteHotkeyButton.Content = _plugin.Settings.PasteHotkey;
        ViewHotkeyButton.Content = _plugin.Settings.ViewHotkey;
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
            _plugin.Settings.MaxHistory = n;
            _plugin.SaveSettings();
        }
        else
        {
            MaxHistoryBox.Text = _plugin.Settings.MaxHistory.ToString();
        }
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_floatingWindow.DataContext is ViewModels.FloatingViewModel vm)
            vm.ClearHistoryCommand.Execute(null);
    }
}
