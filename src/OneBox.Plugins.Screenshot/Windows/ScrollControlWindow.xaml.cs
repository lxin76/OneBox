using System.Windows;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>
/// 滚动截长图的浮动控制条：显示进度、允许提前完成或取消。
/// 它以置顶但不抢焦点的形式浮在选区旁，避免打断页面的滚动。
/// </summary>
public partial class ScrollControlWindow : Window
{
    /// <summary>用户点了“完成并保存”（保留已拼接的部分）。</summary>
    public event Action? FinishRequested;

    /// <summary>用户点了“取消”（丢弃本次结果）。</summary>
    public event Action? CancelRequested;

    public ScrollControlWindow(System.Drawing.Rectangle region)
    {
        InitializeComponent();

        // 放在选区下方；空间不足时移到选区上方
        var screen = ScreenCapture.GetVirtualScreenDip(this);
        var regionDip = ScreenCapture.ToDip(this, region);
        var left = regionDip.Left + regionDip.Width - 300;
        var top = regionDip.Bottom + 12;
        if (top + 150 > screen.Bottom) top = Math.Max(screen.Top, regionDip.Top - 150);
        Left = Math.Max(screen.Left, left);
        Top = Math.Max(screen.Top, top);

        Loaded += (_, _) =>
        {
            TopmostKeeper.ApplyPasteStyle(this, topmost: true);
            UpdateLayout();
        };
    }

    /// <summary>更新进度显示。</summary>
    public void Report(ScrollProgress progress, int maxHeight)
    {
        StatusText.Text = progress.Status;
        Progress.Value = maxHeight <= 0
            ? 0
            : Math.Min(100, progress.CapturedHeight * 100.0 / maxHeight);
    }

    /// <summary>本次流程结束（无论成功、取消还是失败），关闭控制条。</summary>
    public void CloseControl()
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

    private void OnStopClick(object sender, RoutedEventArgs e) => FinishRequested?.Invoke();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
