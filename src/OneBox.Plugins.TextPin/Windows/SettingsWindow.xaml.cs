using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OneBox.Plugins.TextPin.Core;
using OneBox.Plugins.TextPin.Services;

namespace OneBox.Plugins.TextPin.Windows;

/// <summary>置顶文本插件设置窗口：修改即应用并自动保存；热键录制后交给宿主统一注册。</summary>
public partial class SettingsWindow : Window
{
    private readonly TextPinPlugin _plugin;
    private readonly PinnedWindow _pinnedWindow;

    private bool _recordingToggle;
    private bool _recordingPaste;
    private bool _recordingCopy;
    private bool _recordingCapture;
    private bool _loading;
    private HotkeyRecorder? _recorder;
    private DispatcherTimer? _recordTimer;

    public SettingsWindow(TextPinPlugin plugin, PinnedWindow pinnedWindow)
    {
        InitializeComponent();
        _plugin = plugin;
        _pinnedWindow = pinnedWindow;

        // 深色标题栏（DWM 沉浸式暗色模式）
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };

        var settings = plugin.Settings;
        _loading = true;
        PinnedCheck.IsChecked = settings.Pinned;
        NoActivateCheck.IsChecked = settings.NoActivate;
        AutoPasteCheck.IsChecked = settings.AutoPaste;
        WordWrapCheck.IsChecked = settings.WordWrap;
        // 开机自启属于宿主级设置，插件设置页只展示状态
        AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
        AutoStartCheck.IsEnabled = false;
        _loading = false;

        FontSizeBox.Text = settings.FontSize.ToString("0.#");
        MaxLengthBox.Text = settings.MaxTextLength.ToString();
        DataDirText.Text = "数据目录：" + AppPaths.DataDirectory;
        ToggleHotkeyButton.Content = settings.ToggleHotkey;
        PasteHotkeyButton.Content = settings.PasteHotkey;
        CopyHotkeyButton.Content = settings.CopyHotkey;
        CaptureHotkeyButton.Content = settings.CaptureHotkey;

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

    // ---- 常规设置 ----

    private void OnPinnedChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _plugin.Settings.Pinned = PinnedCheck.IsChecked == true;
        _pinnedWindow.ApplyPinned(_plugin.Settings.Pinned);
        _plugin.SaveSettings();
        _plugin.RefreshSettingsAsync();
    }

    private void OnNoActivateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _plugin.Settings.NoActivate = NoActivateCheck.IsChecked == true;
        _pinnedWindow.ApplyNoActivate(_plugin.Settings.NoActivate);
        _plugin.SaveSettings();
        _plugin.RefreshSettingsAsync();
    }

    private void OnAutoPasteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _plugin.Settings.AutoPaste = AutoPasteCheck.IsChecked == true;
        _plugin.SaveSettings();
        _plugin.RefreshSettingsAsync();
    }

    private void OnWordWrapChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _plugin.Settings.WordWrap = WordWrapCheck.IsChecked == true;
        _pinnedWindow.ApplyWordWrap(_plugin.Settings.WordWrap);
        _plugin.SaveSettings();
        _plugin.RefreshSettingsAsync();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        // 开机自启由宿主管理，插件内不处理
    }

    private void OnFontSizeLostFocus(object sender, RoutedEventArgs e) => ApplyFontSize();

    private void OnFontSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyFontSize();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyFontSize()
    {
        if (double.TryParse(FontSizeBox.Text.Trim(), out var size) && size is >= 9 and <= 32)
        {
            _plugin.Settings.FontSize = Math.Round(size, 1);
            _plugin.SaveSettings();
            _pinnedWindow.ApplyFontSize(_plugin.Settings.FontSize);
            _plugin.RefreshSettingsAsync();
        }
        else
        {
            FontSizeBox.Text = _plugin.Settings.FontSize.ToString("0.#");
        }
    }

    private void OnMaxLengthLostFocus(object sender, RoutedEventArgs e) => ApplyMaxLength();

    private void OnMaxLengthKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyMaxLength();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyMaxLength()
    {
        if (int.TryParse(MaxLengthBox.Text.Trim(), out var n) && n is >= 1000 and <= 5_000_000)
        {
            _plugin.Settings.MaxTextLength = n;
            _plugin.SaveSettings();
            _plugin.RefreshSettingsAsync();
        }
        else
        {
            MaxLengthBox.Text = _plugin.Settings.MaxTextLength.ToString();
        }
    }

    // ---- 热键录制 ----

    private void OnRecordToggleHotkey(object sender, RoutedEventArgs e) => StartRecording(toggle: true);

    private void OnRecordPasteHotkey(object sender, RoutedEventArgs e) => StartRecording(toggle: false);

    private void OnRecordCopyHotkey(object sender, RoutedEventArgs e) => StartRecording(toggle: false, copy: true);

    private void OnRecordCaptureHotkey(object sender, RoutedEventArgs e) => StartRecording(toggle: false, capture: true);

    private void StartRecording(bool toggle, bool copy = false, bool capture = false)
    {
        _recordingToggle = toggle;
        _recordingPaste = !toggle && !copy && !capture;
        _recordingCopy = copy;
        _recordingCapture = capture;
        ShowHotkeyWarning(null);
        var button = toggle ? ToggleHotkeyButton
            : copy ? CopyHotkeyButton
            : capture ? CaptureHotkeyButton
            : PasteHotkeyButton;
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
            if (_recordingToggle || _recordingPaste || _recordingCopy || _recordingCapture)
                ShowHotkeyWarning("未检测到按键——若已按组合键却没反应，说明该组合可能已被其他软件占用");
        };
        _recordTimer.Start();
    }

    private void OnKeyCaptured(Key key, ModifierKeys modifiers)
    {
        if (!_recordingToggle && !_recordingPaste && !_recordingCopy && !_recordingCapture) return;

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

        if (_recordingToggle) _plugin.Settings.ToggleHotkey = combo;
        else if (_recordingCopy) _plugin.Settings.CopyHotkey = combo;
        else if (_recordingCapture) _plugin.Settings.CaptureHotkey = combo;
        else _plugin.Settings.PasteHotkey = combo;

        _plugin.SaveSettings();
        CancelRecording();
        _pinnedWindow.UpdateHintText();
        ShowHotkeyWarning(warning);

        // 重新向宿主声明热键；宿主注册完成后回报结果
        _ = ReapplyHotkeysAsync();
    }

    private async Task ReapplyHotkeysAsync()
    {
        await _plugin.DeclareHotkeysAsync();
        ShowHotkeyWarning(BuildHotkeyWarning() ?? CheckSelfConflict(_plugin.Settings.ToggleHotkey));
    }

    /// <summary>检查新热键是否与自身其他快捷键或固定热键重复。</summary>
    private string? CheckSelfConflict(string combo)
    {
        var current = _recordingToggle ? _plugin.Settings.ToggleHotkey
            : _recordingCopy ? _plugin.Settings.CopyHotkey
            : _recordingCapture ? _plugin.Settings.CaptureHotkey
            : _plugin.Settings.PasteHotkey;

        var others = new[]
            {
                _plugin.Settings.ToggleHotkey,
                _plugin.Settings.PasteHotkey,
                _plugin.Settings.CopyHotkey,
                _plugin.Settings.CaptureHotkey,
            }
            .Where(h => !string.Equals(h, current, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return HotkeyConflictChecker.FindSelfConflict(combo, others);
    }

    /// <summary>
    /// 宿主重新注册了热键后，刷新窗口里显示的键位与"未生效"提示。
    ///
    /// 窗口可能一直开着；用户这时在工具箱里改了键，若这里不刷新，
    /// 窗口会继续显示旧键位和过期的失败提示，与实际行为对不上。
    /// </summary>
    public void RefreshHotkeys()
    {
        // 正在录制时不打扰：用户此刻正在按组合键，改动按钮文字反而添乱
        if (_recorder is { IsRecording: true }) return;

        ToggleHotkeyButton.Content = _plugin.Settings.ToggleHotkey;
        PasteHotkeyButton.Content = _plugin.Settings.PasteHotkey;
        CopyHotkeyButton.Content = _plugin.Settings.CopyHotkey;
        CaptureHotkeyButton.Content = _plugin.Settings.CaptureHotkey;

        ShowHotkeyWarning(BuildHotkeyWarning());
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
        _recordingToggle = _recordingPaste = _recordingCopy = _recordingCapture = false;
        ToggleHotkeyButton.Content = _plugin.Settings.ToggleHotkey;
        PasteHotkeyButton.Content = _plugin.Settings.PasteHotkey;
        CopyHotkeyButton.Content = _plugin.Settings.CopyHotkey;
        CaptureHotkeyButton.Content = _plugin.Settings.CaptureHotkey;
    }

    // ---- 其他 ----

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });

    private void OnClearText(object sender, RoutedEventArgs e) => _pinnedWindow.ClearText();
}
