using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneBox.Plugins.Screenshot.Core;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>截图插件的设置窗口。改动即时保存并通知插件主体。</summary>
public partial class ScreenshotSettingsWindow : Window
{
    private readonly ScreenshotSettings _settings;
    private readonly Action _onChanged;
    private readonly Func<Task> _onHotkeysChanged;
    private HotkeyRecorder? _recorder;
    private bool _loading = true;

    public ScreenshotSettingsWindow(ScreenshotSettings settings, Action onChanged, Func<Task> onHotkeysChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;
        _onHotkeysChanged = onHotkeysChanged;

        LoadFromSettings();
        _loading = false;
    }

    /// <summary>
    /// 外部（宿主设置中心）改动了同一份设置对象后，刷新界面上的显示值。
    /// <see cref="_loading"/> 期间控件事件被忽略，因此不会反过来触发保存。
    /// </summary>
    public void ReloadFrom(ScreenshotSettings settings)
    {
        _loading = true;
        LoadFromSettings();
        _loading = false;
    }

    private void LoadFromSettings()
    {
        SaveDirBox.Text = _settings.SaveDirectory;
        TemplateBox.Text = _settings.FileNameTemplate;
        FormatBox.SelectedIndex = _settings.Format == ShotImageFormat.Jpeg ? 1 : 0;
        QualitySlider.Value = Math.Clamp(_settings.JpegQuality, 40, 100);
        QualityText.Text = ((int)QualitySlider.Value).ToString();

        CopyCheck.IsChecked = _settings.CopyToClipboard;
        PinCheck.IsChecked = _settings.PinTopmost;

        WheelBox.Text = _settings.ScrollWheelNotches.ToString();
        WheelHint.Text = $"每格约等于一屏的 1/{Math.Max(1, _settings.ScrollWheelNotches)}";
        MaxHeightBox.Text = _settings.ScrollMaxHeight.ToString();

        LoadOcrLanguages();
        StripSpacesCheck.IsChecked = _settings.OcrStripCjkSpaces;
        EnhanceCheck.IsChecked = _settings.OcrEnhanceImage;

        RegionHotkeyButton.Content = _settings.RegionHotkey;
        FullHotkeyButton.Content = _settings.FullScreenHotkey;
        ScrollHotkeyButton.Content = _settings.ScrollHotkey;
        OcrHotkeyButton.Content = _settings.OcrClipboardHotkey;

        DataDirText.Text = $"数据目录：{AppPaths.DataDirectory}";
    }

    private void LoadOcrLanguages()
    {
        OcrLanguageBox.Items.Clear();
        OcrLanguageBox.Items.Add(new ComboBoxItem { Content = "跟随系统语言", Tag = "" });

        var languages = OcrService.AvailableLanguages();
        foreach (var tag in languages)
            OcrLanguageBox.Items.Add(new ComboBoxItem { Content = tag, Tag = tag });

        // 选中当前设置
        var selectedIndex = 0;
        for (int i = 0; i < OcrLanguageBox.Items.Count; i++)
        {
            if (OcrLanguageBox.Items[i] is ComboBoxItem { Tag: string tag } && tag == _settings.OcrLanguage)
            {
                selectedIndex = i;
                break;
            }
        }
        OcrLanguageBox.SelectedIndex = selectedIndex;
        OcrLanguageBox.SelectionChanged += OnOcrLanguageChanged;

        OcrHint.Text = languages.Count == 0
            ? "系统未安装 OCR 语言包。请在「设置 → 时间和语言 → 语言和区域」里为目标语言添加「光学字符识别」功能。"
            : $"系统可用语言：{string.Join("、", languages)}";
    }

    // ---- 常规 ----

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = Directory.Exists(_settings.ResolveSaveDirectory())
                ? _settings.ResolveSaveDirectory()
                : AppDataSafeDirectory(),
        };

        if (dialog.ShowDialog(this) != true) return;
        SaveDirBox.Text = dialog.FolderName;
        _settings.SaveDirectory = dialog.FolderName;
        Save();
    }

    private static string AppDataSafeDirectory()
    {
        // 便携模式：不给用户"我的图片"，直接在插件自己的数据目录下选，
        // 免得默认值把截图散落到系统用户目录里
        var fallback = Screenshot.Core.AppPaths.DefaultSaveDirectory;
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private void OnOpenSaveDir(object sender, RoutedEventArgs e) => OpenDirectory(_settings.ResolveSaveDirectory());

    private void OnCopyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.CopyToClipboard = CopyCheck.IsChecked == true;
        Save();
    }

    // 自动保存设置项已移除：截图只做预览，保存一律由用户「另存为」选择位置
    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.PinTopmost = PinCheck.IsChecked == true;
        Save();
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Format = FormatBox.SelectedIndex == 1 ? ShotImageFormat.Jpeg : ShotImageFormat.Png;
        Save();
    }

    private void OnStripSpacesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OcrStripCjkSpaces = StripSpacesCheck.IsChecked == true;
        Save();
    }

    private void OnEnhanceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OcrEnhanceImage = EnhanceCheck.IsChecked == true;
        Save();
    }

    private void OnOcrLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.OcrLanguage = (OcrLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        Save();
    }

    // ---- 数值项：失焦或回车时提交 ----

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        CommitNumericFields();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_recorder is { IsRecording: true })
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitNumericFields();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void CommitNumericFields()
    {
        var changed = false;

        if (int.TryParse(WheelBox.Text.Trim(), out var wheel))
        {
            wheel = Math.Clamp(wheel, 1, 15);
            if (wheel != _settings.ScrollWheelNotches) { _settings.ScrollWheelNotches = wheel; changed = true; }
            WheelBox.Text = wheel.ToString();
            WheelHint.Text = $"每格约等于一屏的 1/{wheel}";
        }
        else WheelBox.Text = _settings.ScrollWheelNotches.ToString();

        if (int.TryParse(MaxHeightBox.Text.Trim(), out var maxHeight))
        {
            maxHeight = Math.Clamp(maxHeight, 1000, 100000);
            if (maxHeight != _settings.ScrollMaxHeight) { _settings.ScrollMaxHeight = maxHeight; changed = true; }
            MaxHeightBox.Text = maxHeight.ToString();
        }
        else MaxHeightBox.Text = _settings.ScrollMaxHeight.ToString();

        var quality = (int)QualitySlider.Value;
        if (quality != _settings.JpegQuality) { _settings.JpegQuality = quality; changed = true; }
        QualityText.Text = quality.ToString();

        var template = TemplateBox.Text.Trim();
        if (template.Length > 0 && template != _settings.FileNameTemplate)
        {
            _settings.FileNameTemplate = template;
            changed = true;
        }

        var dir = SaveDirBox.Text.Trim();
        if (dir != _settings.SaveDirectory)
        {
            _settings.SaveDirectory = dir;
            changed = true;
        }

        if (changed) Save();
    }

    // ---- 热键录制 ----

    private void OnRecordRegionHotkey(object sender, RoutedEventArgs e)
        => StartRecording(RegionHotkeyButton, combo =>
        {
            _settings.RegionHotkey = combo;
            RegionHotkeyButton.Content = combo;
            Save();
            _ = _onHotkeysChanged();
        });

    private void OnRecordFullHotkey(object sender, RoutedEventArgs e)
        => StartRecording(FullHotkeyButton, combo =>
        {
            _settings.FullScreenHotkey = combo;
            FullHotkeyButton.Content = combo;
            Save();
            _ = _onHotkeysChanged();
        });

    private void OnRecordScrollHotkey(object sender, RoutedEventArgs e)
        => StartRecording(ScrollHotkeyButton, combo =>
        {
            _settings.ScrollHotkey = combo;
            ScrollHotkeyButton.Content = combo;
            Save();
            _ = _onHotkeysChanged();
        });

    private void OnRecordOcrHotkey(object sender, RoutedEventArgs e)
        => StartRecording(OcrHotkeyButton, combo =>
        {
            _settings.OcrClipboardHotkey = combo;
            OcrHotkeyButton.Content = combo;
            Save();
            _ = _onHotkeysChanged();
        });

    private void StartRecording(Button button, Action<string> onRecorded)
    {
        _recorder?.Stop();
        button.Content = "按下组合键…";
        HotkeyWarningText.Visibility = Visibility.Collapsed;

        _recorder = new HotkeyRecorder(this,
            onRecorded: combo =>
            {
                onRecorded(combo);
                RestoreHotkeyLabels();
            },
            onCancel: RestoreHotkeyLabels);
        _recorder.Start();
    }

    private void RestoreHotkeyLabels()
    {
        RegionHotkeyButton.Content = _settings.RegionHotkey;
        FullHotkeyButton.Content = _settings.FullScreenHotkey;
        ScrollHotkeyButton.Content = _settings.ScrollHotkey;
        OcrHotkeyButton.Content = _settings.OcrClipboardHotkey;
    }

    /// <summary>
    /// 宿主重新注册热键后刷新窗口上的键位标签。
    ///
    /// 用户在工具箱里改键时，本窗口可能一直开着；若不刷新，
    /// 按钮上会继续显示旧组合，与宿主里实际生效的键对不上。
    /// </summary>
    public void RefreshHotkeys()
    {
        if (_recorder is { IsRecording: true }) return; // 正在录制时不打断
        RestoreHotkeyLabels();
    }

    /// <summary>宿主注册热键后回报的失败项，显示在窗口里。</summary>
    public void ShowHotkeyWarning(string message)
    {
        HotkeyWarningText.Text = message;
        HotkeyWarningText.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ---- 数据 ----

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenDirectory(AppPaths.DataDirectory);

    private void OnOpenShotsDir(object sender, RoutedEventArgs e)
        => OpenDirectory(_settings.ResolveSaveDirectory());

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "恢复默认设置？热键与保存路径都会回到初始值。", "恢复默认设置",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var defaults = new ScreenshotSettings();
        _settings.SaveDirectory = defaults.SaveDirectory;
        _settings.FileNameTemplate = defaults.FileNameTemplate;
        _settings.Format = defaults.Format;
        _settings.JpegQuality = defaults.JpegQuality;
        _settings.CopyToClipboard = defaults.CopyToClipboard;
        _settings.PinTopmost = defaults.PinTopmost;
        _settings.ScrollWheelNotches = defaults.ScrollWheelNotches;
        _settings.ScrollMaxHeight = defaults.ScrollMaxHeight;
        _settings.OcrLanguage = defaults.OcrLanguage;
        _settings.OcrStripCjkSpaces = defaults.OcrStripCjkSpaces;
        _settings.OcrEnhanceImage = defaults.OcrEnhanceImage;
        _settings.RegionHotkey = defaults.RegionHotkey;
        _settings.FullScreenHotkey = defaults.FullScreenHotkey;
        _settings.ScrollHotkey = defaults.ScrollHotkey;
        _settings.OcrClipboardHotkey = defaults.OcrClipboardHotkey;

        _loading = true;
        LoadFromSettings();
        _loading = false;
        Save();
        _ = _onHotkeysChanged();
    }

    private void Save()
    {
        SettingsStore.Save(_settings);
        _onChanged();
    }

    private static void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Write($"打开目录失败 {path}: {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _recorder?.Stop();
        CommitNumericFields();
        base.OnClosed(e);
    }
}