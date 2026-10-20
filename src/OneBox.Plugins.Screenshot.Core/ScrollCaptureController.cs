using System.Diagnostics;
using System.Security.Cryptography;

namespace OneBox.Plugins.Screenshot.Core;

/// <summary>由平台适配器负责捕获稳定帧、检查目标及注入滚轮；正值向下，负值向上。</summary>
public interface IScrollCaptureSource
{
    Task<ScrollCaptureFrame> CaptureAsync(CancellationToken cancellationToken);
    Task<bool> ScrollAsync(int wheelDelta, CancellationToken cancellationToken);
    bool IsTargetValid { get; }
    bool IsCancelRequested { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed record ScrollCaptureFrame(byte[] Pixels, int Width, int Height, int Stride, bool Stable);

public enum ScrollCaptureEndReason
{
    Finished, Cancelled, UnableToScroll, NoFurtherMotion, Incomplete, TargetLost,
    CaptureFailed, ScrollFailed, InvalidFrame, FrameLimit, DurationLimit, StitcherLimit
}

/// <summary>Cancelled 时调用方必须丢弃其持有的拼接器；其他退出可导出已提交内容。</summary>
public sealed record ScrollCaptureResult(
    ScrollCaptureEndReason EndReason, string Message, bool CanExport, bool IsComplete);

public sealed record ScrollCaptureOptions
{
    /// <summary>捕获尝试数（含重采），并非仅成功追加数。</summary>
    public int MaxFrames { get; init; } = 300;
    public int MaxWheelNotches { get; init; } = 6;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StepDelay { get; init; } = TimeSpan.FromMilliseconds(150);
}

/// <summary>
/// 一次性、无 UI 依赖的滚动控制器。只有拼接器确认的正位移才推进基准。
/// 没有底部传感器时不会把静止画面宣称为“确认到底”；手动 Finish 也不声明内容完整。
/// </summary>
public sealed class ScrollCaptureController : IDisposable
{
    private readonly ScrollCaptureOptions _options;
    /// <summary>可选匹配元数据回调；不含像素，不改变状态转换。</summary>
    public Action<string>? DiagnosticLog { get; set; }
    private readonly CancellationTokenSource _stop = new();
    private readonly object _signalGate = new();
    private bool _disposed;
    private int _finishRequested;
    private int _cancelRequested;
    private int _started;

    public ScrollCaptureController(ScrollCaptureOptions? options = null)
    {
        _options = options ?? new ScrollCaptureOptions();
        if (_options.MaxFrames <= 0 || _options.MaxWheelNotches is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaxDuration <= TimeSpan.Zero || _options.MaxDuration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.StepDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public void RequestFinish()
    {
        Interlocked.Exchange(ref _finishRequested, 1);
        SignalStop();
    }

    public void RequestCancel()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        SignalStop();
    }

    private void SignalStop()
    {
        // 第三方 CancellationToken 回调的异常不能破坏线程安全的停止信号。
        lock (_signalGate)
        {
            if (_disposed) return;
            try { _stop.Cancel(); }
            catch (AggregateException) { }
        }
    }

    /// <summary>应在 RunAsync 返回后释放；运行中释放等同于 Cancel。</summary>
    public void Dispose()
    {
        RequestCancel();
        lock (_signalGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Dispose();
        }
    }

    public async Task<ScrollCaptureResult> RunAsync(
        IScrollCaptureSource source, ScrollStitcher stitcher,
        Action<int, int, string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stitcher);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A scroll capture controller can only run once.");

        using var deadline = new CancellationTokenSource(_options.MaxDuration);
        CancellationTokenSource CreateLinked()
        {
            lock (_signalGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token, deadline.Token);
            }
        }
        using var linked = CreateLinked();
        var token = linked.Token;
        var clock = Stopwatch.StartNew();
        // 先建立停止关联，再让出；Dispose 与 RunAsync 启动并发时也能安全中止。
        await Task.Yield();
        ScrollCaptureFrame? baseline = null;
        byte[]? baselineFingerprint = null;

        byte[] Fingerprint(ScrollCaptureFrame frame)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var y = 0; y < frame.Height; y++)
                hash.AppendData(frame.Pixels.AsSpan(y * frame.Stride, frame.Width * 4));
            return hash.GetHashAndReset();
        }
        var captures = 0;
        var notches = 1;
        var consecutiveNoChange = 0;
        var hasProgress = false;
        var lastFailure = "Unknown";

        ScrollCaptureResult Result(ScrollCaptureEndReason reason, string message) =>
            new(reason, message, reason != ScrollCaptureEndReason.Cancelled && stitcher.TotalHeight > 0, false);

        void Check()
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _cancelRequested) != 0 || source.IsCancelRequested)
                throw new StopCapture(ScrollCaptureEndReason.Cancelled, "已取消，截图内容已丢弃。");
            if (Volatile.Read(ref _finishRequested) != 0)
                throw new StopCapture(ScrollCaptureEndReason.Finished, "已手动完成，保留已拼接内容。");
            if (deadline.IsCancellationRequested || clock.Elapsed >= _options.MaxDuration)
                throw new StopCapture(ScrollCaptureEndReason.DurationLimit, "已达到捕获时间上限，保留已拼接内容。");
            if (!source.IsTargetValid)
                throw new StopCapture(ScrollCaptureEndReason.TargetLost, "滚动目标已失效，已停止捕获。");
            token.ThrowIfCancellationRequested();
        }

        async Task Move(int delta)
        {
            await Task.Yield();
            Check(); // 每次注入之前重新检查，失效目标绝不接收滚轮。
            if (captures >= _options.MaxFrames)
                throw new StopCapture(ScrollCaptureEndReason.FrameLimit, "已达到捕获帧数上限，保留已拼接内容。");
            bool moved;
            try { moved = await source.ScrollAsync(delta, token).WaitAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new StopCapture(ScrollCaptureEndReason.ScrollFailed, "滚轮注入失败：" + ex.Message); }
            Check();
            if (!moved) throw new StopCapture(ScrollCaptureEndReason.ScrollFailed, "目标未接受滚轮输入，已停止捕获。");
            await Pause();
        }

        async Task Pause()
        {
            await Task.Yield();
            Check();
            try { await source.DelayAsync(_options.StepDelay, token).WaitAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new StopCapture(ScrollCaptureEndReason.CaptureFailed, "等待稳定画面失败：" + ex.Message); }
            Check();
        }

        async Task<(ScrollAppendStatus? Status, ScrollCaptureFrame Frame)> Capture()
        {
            await Task.Yield();
            Check();
            if (captures >= _options.MaxFrames)
                throw new StopCapture(ScrollCaptureEndReason.FrameLimit, "已达到捕获帧数上限，保留已拼接内容。");
            captures++;
            ScrollCaptureFrame frame;
            try { frame = await source.CaptureAsync(token).WaitAsync(token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new StopCapture(ScrollCaptureEndReason.CaptureFailed, "画面捕获失败：" + ex.Message); }
            Check();
            if (frame is null || frame.Pixels is null || frame.Width <= 0 || frame.Height <= 0 ||
                frame.Stride < (long)frame.Width * 4 || (long)frame.Stride * frame.Height > frame.Pixels.LongLength ||
                (baseline is not null && (frame.Width != baseline.Width || frame.Height != baseline.Height)))
                throw new StopCapture(ScrollCaptureEndReason.InvalidFrame, "捕获画面的尺寸或像素缓冲区无效。");
            if (!frame.Stable)
            {
                lastFailure = "Unstable";
                return (null, frame); // 不稳定帧绝不交给拼接器。
            }
            ScrollAppendResult added;
            try { added = await Task.Run(() => stitcher.Add(frame.Pixels, frame.Height, frame.Stride)).ConfigureAwait(false); }
            catch (OutOfMemoryException) { throw new StopCapture(ScrollCaptureEndReason.StitcherLimit, "已达到截图内存预算，保留已拼接内容。"); }
            catch (ArgumentException ex) { throw new StopCapture(ScrollCaptureEndReason.InvalidFrame, "拼接帧无效：" + ex.Message); }
            try { DiagnosticLog?.Invoke($"status={added.Status} appended={added.AppendedRows} overlap={added.OverlapRows} diagnostics={stitcher.LastDiagnostics}"); }
            catch (Exception) { /* 可选诊断不能改变捕获状态或放宽匹配结果。 */ }
            if (added.Status is ScrollAppendStatus.First or ScrollAppendStatus.Appended)
            {
                if (added.Status == ScrollAppendStatus.Appended) hasProgress = true;
                // 仅保留尺寸和指纹，不额外持有整帧，内存预算由拼接器统一管理。
                baselineFingerprint = Fingerprint(frame);
                baseline = frame with { Pixels = Array.Empty<byte>() };
                onProgress?.Invoke(stitcher.TotalHeight, stitcher.FrameCount, "正在拼接滚动截图");
            }
            else lastFailure = added.Status.ToString();
            Check(); // Add 不可中途放弃，避免返回后仍有后台线程修改已导出的内容。
            if (added.Status == ScrollAppendStatus.FixedContent)
                throw new StopCapture(ScrollCaptureEndReason.Incomplete, "检测到固定头尾区域，请重新裁选以排除固定内容；截图不完整。");
            if (added.Status is ScrollAppendStatus.LimitReached or ScrollAppendStatus.MemoryLimitReached)
                throw new StopCapture(ScrollCaptureEndReason.StitcherLimit, "已达到截图高度或内存预算，保留已拼接内容。");
            return (added.Status, frame);
        }

        bool Accepted(ScrollAppendStatus? status) => status is ScrollAppendStatus.First or ScrollAppendStatus.Appended;
        bool AtBaseline((ScrollAppendStatus? Status, ScrollCaptureFrame Frame) capture)
        {
            if (capture.Status != ScrollAppendStatus.NoChange || baseline is null) return false;
            // 恢复时 NoChange 不足以证明已回到基准：另校验完整有效像素的 SHA-256 指纹。
            return Fingerprint(capture.Frame).AsSpan().SequenceEqual(baselineFingerprint);
        }

        async Task<bool> RetryDownFromBaseline()
        {
            for (var retry = 0; retry < 2; retry++)
            {
                await Move(120);
                var next = await Capture();
                if (Accepted(next.Status)) return true;
                for (var recapture = 0; recapture < 2 && !AtBaseline(next); recapture++)
                {
                    await Pause();
                    next = await Capture();
                    if (Accepted(next.Status)) return true;
                }
                // 未重新确认基准时，不能再向下盲滚。
                if (!AtBaseline(next)) return false;
            }
            return false;
        }

        async Task<bool> Recover()
        {
            var confirmedBaseline = false;
            for (var retry = 0; retry < 2; retry++)
            {
                await Pause();
                var next = await Capture();
                if (Accepted(next.Status)) return true;
                confirmedBaseline = AtBaseline(next);
            }
            if (confirmedBaseline) return await RetryDownFromBaseline();
            if (baseline is null) return false;
            for (var rollback = 0; rollback < 3; rollback++)
            {
                await Move(-120);
                var next = await Capture();
                if (Accepted(next.Status)) return true; // 仍在基准前方，已重新找到可靠的正位移。
                if (AtBaseline(next)) return await RetryDownFromBaseline();
                // 可能落后基准：继续有界向上取样，不接失败帧，也绝不盲目向下。
            }
            return false;
        }

        try
        {
            var first = await Capture();
            if (!Accepted(first.Status) && !await Recover())
                return Result(ScrollCaptureEndReason.Incomplete, $"无法取得稳定且可拼接的首帧（{lastFailure}）。");
            while (true)
            {
                await Move(notches * 120);
                var next = await Capture();
                if (Accepted(next.Status))
                {
                    consecutiveNoChange = 0;
                    var delta = stitcher.LastScrollDelta;
                    if (delta > 0)
                        notches = Math.Clamp((int)Math.Round(next.Frame.Height * 0.45 * notches / delta), 1, _options.MaxWheelNotches);
                    continue;
                }
                if (next.Status == ScrollAppendStatus.NoChange)
                {
                    if (++consecutiveNoChange >= 3)
                        return hasProgress
                            ? Result(ScrollCaptureEndReason.NoFurtherMotion, "连续多次未检测到进一步位移；无法确认已经到底，保留已拼接内容。")
                            : Result(ScrollCaptureEndReason.UnableToScroll, "目标未产生可确认的滚动位移。");
                    notches = 1;
                    continue;
                }
                consecutiveNoChange = 0;
                if (!await Recover())
                    return Result(ScrollCaptureEndReason.Incomplete, $"无法恢复与最后成功帧的可靠衔接（{lastFailure}），截图不完整；保留已拼接内容。");
                notches = 1;
            }
        }
        catch (StopCapture stopped) { return Result(stopped.Reason, stopped.Message); }
        catch (OperationCanceledException)
        {
            // 优先级：Cancel（含 Esc、外部 token） > Finish > deadline。
            try { Check(); }
            catch (StopCapture stopped) { return Result(stopped.Reason, stopped.Message); }
            return Result(ScrollCaptureEndReason.CaptureFailed, "捕获源意外中止操作，已停止捕获。");
        }
    }

    private sealed class StopCapture(ScrollCaptureEndReason reason, string message) : Exception(message)
    {
        public ScrollCaptureEndReason Reason { get; } = reason;
    }
}
