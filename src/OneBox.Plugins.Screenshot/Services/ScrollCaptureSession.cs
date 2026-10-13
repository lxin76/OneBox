using System.Windows.Media.Imaging;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>滚动截图的进度回调。</summary>
public sealed record ScrollProgress(int CapturedHeight, int FrameCount, string Status)
{
    public static ScrollProgress Empty { get; } = new(0, 0, "准备中…");
}

/// <summary>
/// 滚动截长图的控制器：按固定间隔抓取选区、判断是否还在滚动，并把新增部分拼接到结果。
/// 抓取与拼接都跑在后台线程，避免阻塞界面。
/// </summary>
public sealed class ScrollCaptureSession
{
    /// <summary>两帧之间的等待时间上限：滚轮发出后等页面滚停。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(320);

    /// <summary>轮询间隔：用来判断滚动动画是否已经停下。</summary>
    private static readonly TimeSpan StabilityPollInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>等待画面稳定的最长时长；超过就取最后一帧，避免卡死。</summary>
    private static readonly TimeSpan StabilityTimeout = TimeSpan.FromMilliseconds(1600);

    /// <summary>判定“画面已静止”的阈值：两帧平均差异小于该值即认为动画结束。</summary>
    private const int StabilityThreshold = 2;

    /// <summary>连续多少帧没有变化就认为到底了。</summary>
    private const int NoChangeStopThreshold = 2;

    /// <summary>连续多少帧匹配失败就放弃（避免无限重试）。</summary>
    private const int NoOverlapTolerance = 3;

    private static readonly TimeSpan WheelInputDelay = TimeSpan.FromMilliseconds(40);

    private readonly System.Drawing.Rectangle _region;
    private readonly ScreenshotSettings _settings;
    private readonly ScrollStitcher _stitcher;
    private readonly CancellationTokenSource _cts = new();

    public ScrollCaptureSession(System.Drawing.Rectangle region, ScreenshotSettings settings)
    {
        _region = region;
        _settings = settings;
        _stitcher = new ScrollStitcher(region.Width, settings.ScrollMaxHeight, settings.ScrollMinMatchRows);
    }

    /// <summary>请求停止（用户按 Esc 或点停止按钮）。</summary>
    public void Stop() => _cts.Cancel();

    /// <summary>
    /// 执行完整流程：抓第一帧 → 循环（滚动鼠标 → 抓帧 → 拼接）→ 返回结果。
    /// </summary>
    public async Task<BitmapSource?> RunAsync(
        Action<ScrollProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var token = linked.Token;

        try
        {
            return await Task.Run(() => RunCore(onProgress, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户中断：把已经拼好的部分返回，不至于白截
            return _stitcher.TotalHeight > 0 ? BuildResult() : null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"滚动截图异常: {ex}");
            return _stitcher.TotalHeight > 0 ? BuildResult() : null;
        }
    }

    private BitmapSource? RunCore(Action<ScrollProgress>? onProgress, CancellationToken token)
    {
        // 滚轮事件会发给光标下的窗口，光标不在选区内就会滚错目标，
        // 因此开始时把光标移到选区中心，结束后再放回原处。
        var originalCursor = ScreenCapture.GetCursorPosition();
        MoveCursorToRegionCenter();

        try
        {
            return RunCoreInner(onProgress, token);
        }
        finally
        {
            RestoreCursor(originalCursor);
        }
    }

    /// <summary>把光标移到选区中心，保证滚轮事件落在目标窗口上。</summary>
    private void MoveCursorToRegionCenter()
    {
        try
        {
            Native.SetCursorPos(_region.Left + _region.Width / 2, _region.Top + _region.Height / 2);
            // 给目标窗口一点时间处理悬停，避免拼接时混入悬停高亮
            Thread.Sleep(120);
        }
        catch (Exception ex)
        {
            AppLog.Write($"移动光标失败: {ex.Message}");
        }
    }

    private static void RestoreCursor(System.Drawing.Point original)
    {
        if (original == System.Drawing.Point.Empty) return;
        try
        {
            Native.SetCursorPos(original.X, original.Y);
        }
        catch
        {
            // 恢复光标失败无伤大雅
        }
    }

    private BitmapSource? RunCoreInner(Action<ScrollProgress>? onProgress, CancellationToken token)
    {
        // 第一帧
        if (!TryGrabFrame(out var pixels, out var height, out var stride))
        {
            AppLog.Write("滚动截图：首帧抓取失败");
            return null;
        }

        _stitcher.Add(pixels, height, stride);
        onProgress?.Invoke(new ScrollProgress(_stitcher.TotalHeight, 1, "正在滚动…"));

        var noChangeCount = 0;
        var noOverlapCount = 0;

        while (!token.IsCancellationRequested)
        {
            if (_stitcher.TotalHeight >= _settings.ScrollMaxHeight)
            {
                onProgress?.Invoke(new ScrollProgress(
                    _stitcher.TotalHeight, _stitcher.FrameCount, $"已达高度上限 {_settings.ScrollMaxHeight}px"));
                break;
            }

            // 滚动一屏，然后等页面滚停再抓帧：
            // 浏览器是平滑滚动，固定延时常抓在动画中途，抓到的帧与锚点无法匹配。
            ScrollOnce(token);
            if (!WaitWithCancel(FrameInterval, token)) break;

            if (!TryGrabFrame(out var next, out var nextHeight, out var nextStride))
            {
                AppLog.Write("滚动截图：抓帧失败，提前结束");
                break;
            }

            WaitForStableFrame(ref next, ref nextHeight, ref nextStride, token);

            var result = _stitcher.Add(next, nextHeight, nextStride);
            AppLog.Write($"滚动帧 #{_stitcher.FrameCount}: {result.Status} "
                         + $"新增={result.AppendedRows} 重叠={result.OverlapRows} "
                         + $"匹配={_stitcher.LastMatchedRows}/{_stitcher.LastRequiredRows} "
                         + $"位置={_stitcher.LastBestPosition} 总高={_stitcher.TotalHeight}");

            switch (result.Status)
            {
                case ScrollAppendStatus.NoChange:
                    noChangeCount++;
                    if (noChangeCount >= NoChangeStopThreshold)
                    {
                        onProgress?.Invoke(new ScrollProgress(
                            _stitcher.TotalHeight, _stitcher.FrameCount, "已到底部，拼接完成"));
                        goto done;
                    }
                    break;

                case ScrollAppendStatus.NoOverlap:
                    noOverlapCount++;
                    if (noOverlapCount >= NoOverlapTolerance)
                    {
                        AppLog.Write("滚动截图：多次找不到重叠区，停止拼接");
                        goto done;
                    }
                    // 回退一点重新尝试，提高找回重叠区的机会
                    ScrollUp(Math.Max(1, _settings.ScrollWheelNotches), token);
                    break;

                case ScrollAppendStatus.LimitReached:
                    onProgress?.Invoke(new ScrollProgress(
                        _stitcher.TotalHeight, _stitcher.FrameCount, "已达高度上限"));
                    goto done;

                default:
                    noChangeCount = 0;
                    noOverlapCount = 0;
                    onProgress?.Invoke(new ScrollProgress(
                        _stitcher.TotalHeight, _stitcher.FrameCount,
                        $"已拼接 {_stitcher.TotalHeight}px（第 {_stitcher.FrameCount} 帧）"));
                    break;
            }
        }

    done:
        return _stitcher.TotalHeight > 0 ? BuildResult() : null;
    }

    private BitmapSource? BuildResult()
    {
        try
        {
            var buffer = _stitcher.BuildBgra();
            return ImageCodec.FromBgraBytes(buffer, _region.Width, _stitcher.TotalHeight);
        }
        catch (Exception ex)
        {
            AppLog.Write($"滚动截图：导出失败 {ex.Message}");
            return null;
        }
    }

    private bool TryGrabFrame(out byte[] pixels, out int height, out int stride)
    {
        pixels = [];
        height = 0;
        stride = 0;

        var frame = ScreenCapture.CaptureRegion(_region);
        if (frame is null) return false;

        pixels = ImageCodec.ToBgraBytes(frame, out stride, out var width, out height);
        if (height <= 0 || width != _region.Width) return false;
        return true;
    }

    /// <summary>
    /// 反复抓帧直到画面静止（或超时），把最后一帧写回参数。
    /// 平滑滚动的动画中途帧会让锚点匹配全部失败，必须等到静止。
    /// </summary>
    private void WaitForStableFrame(ref byte[] pixels, ref int height, ref int stride, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + StabilityTimeout;
        var previous = pixels;

        while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (!WaitWithCancel(StabilityPollInterval, token)) return;

            if (!TryGrabFrame(out var candidate, out var candidateHeight, out var candidateStride)) return;

            var diff = AverageDifference(previous, candidate);
            pixels = candidate;
            height = candidateHeight;
            stride = candidateStride;
            previous = candidate;

            if (diff <= StabilityThreshold) return; // 已静止
        }
    }

    /// <summary>两帧之间的平均灰度差异，用于判断画面是否已静止。</summary>
    private static int AverageDifference(byte[] a, byte[] b)
    {
        if (a.Length == 0 || b.Length == 0) return int.MaxValue;
        var length = Math.Min(a.Length, b.Length);
        if (length == 0) return int.MaxValue;

        // 采样比较即可，无需逐像素
        const int maxSamples = 20000;
        var step = Math.Max(4, (length / maxSamples / 4) * 4);

        long total = 0;
        long count = 0;
        for (int i = 0; i + 2 < length; i += step)
        {
            var la = (a[i + 2] * 77 + a[i + 1] * 151 + a[i] * 28) >> 8;
            var lb = (b[i + 2] * 77 + b[i + 1] * 151 + b[i] * 28) >> 8;
            total += Math.Abs(la - lb);
            count++;
        }
        return count == 0 ? int.MaxValue : (int)(total / count);
    }

    /// <summary>向下滚动若干格滚轮。</summary>
    private void ScrollOnce(CancellationToken token) => SendWheel(-_settings.ScrollWheelNotches, token);

    private void ScrollUp(int notches, CancellationToken token) => SendWheel(notches, token);

    /// <summary>用 mouse_event 发送滚轮事件（负数为向下滚动）。</summary>
    private void SendWheel(int notches, CancellationToken token)
    {
        if (notches == 0) return;
        if (token.IsCancellationRequested) return;

        // 每次发送一格，步子小一些更接近真实滚轮，页面滚动也更稳定
        var steps = Math.Abs(notches);
        var amount = notches > 0 ? Native.WHEEL_DELTA : -Native.WHEEL_DELTA;
        for (int i = 0; i < steps; i++)
        {
            if (token.IsCancellationRequested) return;
            Native.mouse_event(Native.MOUSEEVENTF_WHEEL, 0, 0, amount, UIntPtr.Zero);
            Thread.Sleep(WheelInputDelay);
        }
    }

    private static bool WaitWithCancel(TimeSpan duration, CancellationToken token)
    {
        try
        {
            return !token.WaitHandle.WaitOne(duration);
        }
        catch
        {
            return false;
        }
    }
}
