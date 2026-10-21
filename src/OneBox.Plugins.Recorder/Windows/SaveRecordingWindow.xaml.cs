using System.IO;
using System.Windows;
using OneBox.Plugins.Recorder.Core;
using OneBox.Plugins.Recorder.Services;

namespace OneBox.Plugins.Recorder.Windows;

/// <summary>
/// 停止录制后的保存选择：保持当前画质，或按更小的分辨率/码率再压一遍。
/// 默认选中「不压缩」，避免用户随手回车就把原始画质丢掉。
/// </summary>
public partial class SaveRecordingWindow : Window
{
    private readonly SaveRecordingInfo _info;

    public SaveRecordingWindow(SaveRecordingInfo info)
    {
        _info = info;
        InitializeComponent();

        SummaryText.Text = $"时长 {FormatDuration(info.Duration)} · 原始文件 {info.FileSize / 1024.0 / 1024.0:F1} MB";
        QualityText.Text = $"{info.QualityLabel} · {info.Width}×{info.Height} · {info.Bitrate / 1000} kbps";
        PathText.Text = Path.GetDirectoryName(info.FilePath) ?? info.FilePath;

        TargetBox.ItemsSource = info.Targets;
        if (info.Targets.Count > 0)
        {
            TargetBox.SelectedIndex = 0;
        }
        else
        {
            CompressRadio.IsEnabled = false;
            NoTargetHint.Visibility = Visibility.Visible;
        }

        KeepRadio.IsChecked = true;
        ApplyMode();
        UpdateTargetHint();
    }

    /// <summary>用户选择；窗口被直接关闭时为 null（按保留原始录制处理）。</summary>
    public SaveRecordingChoice? Choice { get; private set; }

    /// <summary>宿主退出时由插件直接关闭，不走用户交互。</summary>
    public void CloseSilently()
    {
        try
        {
            Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        var compress = CompressRadio.IsChecked == true;
        CompressPanel.IsEnabled = compress;
        CompressPanel.Opacity = compress ? 1 : 0.55;
        TargetBox.IsEnabled = compress;
        KeepOriginalCheck.IsEnabled = compress;
    }

    private void OnTargetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateTargetHint();

    private void UpdateTargetHint()
    {
        if (TargetBox.SelectedItem is not CompressionTarget target || _info.Duration <= TimeSpan.Zero)
        {
            TargetHint.Text = "只提供比原始画面更小的档位，避免把已压缩的画面再放大。";
            return;
        }

        var size = RecordingSavePlan.EstimateCompressedBytes(target, _info.Duration, _info.HasAudio);
        TargetHint.Text = $"{target.Description} · 预计约 {size / 1024.0 / 1024.0:F1} MB";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        AppLog.Write(
            $"保存选择：compress={CompressRadio.IsChecked == true}, "
            + $"target={(TargetBox.SelectedItem as CompressionTarget)?.Width.ToString() ?? "-"}, "
            + $"keepOriginal={KeepOriginalCheck.IsChecked == true}, keepEnabled={KeepOriginalCheck.IsEnabled}");

        if (CompressRadio.IsChecked == true && TargetBox.SelectedItem is CompressionTarget target)
        {
            Choice = new SaveRecordingChoice(
                RecordingSaveMode.Compress, target, KeepOriginalCheck.IsChecked == true);
        }
        else
        {
            Choice = new SaveRecordingChoice(RecordingSaveMode.KeepOriginal, null, true);
        }

        DialogResult = true;
    }

    private void OnKeepOriginal(object sender, RoutedEventArgs e)
    {
        Choice = new SaveRecordingChoice(RecordingSaveMode.KeepOriginal, null, true);
        DialogResult = true;
    }

    private static string FormatDuration(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
