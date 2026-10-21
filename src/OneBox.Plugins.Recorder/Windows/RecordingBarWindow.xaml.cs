using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OneBox.Plugins.Recorder.Windows;

/// <summary>
/// 录制中显示的迷你控制条：计时、暂停/继续、停止。
///
/// 它是浮层，固定深色、不跟随主题；窗口本身会被 Record 排除在画面之外。
/// 用 ShowActivated=false 打开，避免用户按热键开始录制时把焦点从正在操作的窗口抢走。
/// </summary>
public partial class RecordingBarWindow : Window
{
    private bool _allowClose;
    private bool _finishing;

    /// <summary>用户点了停止（控制条自身不负责停录制流程）。</summary>
    public event Action? StopRequested;

    /// <summary>用户点了暂停/继续。</summary>
    public event Action? PauseRequested;

    public RecordingBarWindow()
    {
        InitializeComponent();

        var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(850))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        RecordDot.BeginAnimation(OpacityProperty, pulse);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 点控制条不激活窗口：否则录制演示时一点「暂停」，前台就切到控制条，
        // 正在操作的程序失去焦点，画面会跟着变。
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(NoActivateHook);
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;

        if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;

        handled = true;
        return new IntPtr(MA_NOACTIVATE);
    }

    /// <summary>窗口句柄；用于交给录制器做「排除出画面」。Show 之后一定有效。</summary>
    public IntPtr Handle
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle != IntPtr.Zero ? handle : new WindowInteropHelper(this).EnsureHandle();
        }
    }

    public void UpdateElapsed(TimeSpan elapsed)
    {
        if (_finishing) return;
        ElapsedText.Text = Format(elapsed);
    }

    public void SetPaused(bool paused)
    {
        if (_finishing) return;

        PauseButton.Content = paused ? "\uE768" : "\uE769";
        PauseButton.ToolTip = paused ? "继续录制" : "暂停录制";
        RecordDot.Fill = new SolidColorBrush(paused
            ? Color.FromRgb(0xF2, 0xB1, 0x3C)
            : Color.FromRgb(0xFF, 0x5B, 0x5B));
    }

    /// <summary>已请求停止，等待文件落盘。</summary>
    public void SetFinishing()
    {
        _finishing = true;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        RecordDot.Visibility = Visibility.Collapsed;
        ElapsedText.Text = "正在保存…";
    }

    /// <summary>插件主动关闭（不触发用户停止事件的二次处理）。</summary>
    public void CloseSilently()
    {
        _allowClose = true;
        try
        {
            Close();
        }
        catch
        {
            // 关闭异常忽略
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 没有关闭按钮；Alt+F4 也不该让控制条消失而录制还在继续
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
        }
        catch
        {
            // 拖动过程中的鼠标状态异常忽略
        }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_finishing) return;
        PauseRequested?.Invoke();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_finishing) return;
        StopButton.IsEnabled = false;
        StopRequested?.Invoke();
    }

    private static string Format(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
