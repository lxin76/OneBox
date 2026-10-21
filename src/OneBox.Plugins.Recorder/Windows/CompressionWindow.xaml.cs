using System.ComponentModel;
using System.Windows;
using OneBox.Plugins.Recorder.Core;

namespace OneBox.Plugins.Recorder.Windows;

/// <summary>压缩转码进度窗。转码本身在 UI 线程之外推进，这里只负责显示与取消。</summary>
public partial class CompressionWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _allowClose;

    public CompressionWindow(CompressionTarget target)
    {
        InitializeComponent();
        TargetText.Text = $"{target.Label} · {target.Description}";
    }

    public CancellationToken Token => _cts.Token;

    public IProgress<double> ProgressReporter => new Progress<double>(value =>
    {
        var percent = Math.Clamp(value, 0, 100);
        if (percent <= 0) return;

        Progress.IsIndeterminate = false;
        Progress.Value = percent;
        StatusText.Text = $"正在压缩… {percent:F0}%";
    });

    public void CloseSilently()
    {
        _allowClose = true;
        try
        {
            Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
    }

    public void RequestCancel()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消，原始录制会保留…";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // 关窗等于取消：等转码停下来，插件会负责清理半成品
            RequestCancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => RequestCancel();
}
