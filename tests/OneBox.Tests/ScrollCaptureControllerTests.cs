using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class ScrollCaptureControllerTests
{
    private const int Width = 96;
    private const int Height = 240;
    private const int Stride = Width * 4;

    private static ScrollCaptureFrame Frame(int start, bool stable = true, int seed = 0)
    {
        var pixels = new byte[Stride * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var hash = unchecked((uint)((start + y) * 2654435761u + seed * 7919));
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            var shade = (byte)(20 + hash % 210);
            var p = y * Stride + x * 4;
            pixels[p] = (byte)Math.Min(255, shade + x % 9);
            pixels[p + 1] = shade;
            pixels[p + 2] = shade;
            pixels[p + 3] = 255;
        }
        return new(pixels, Width, Height, Stride, stable);
    }

    private static ScrollCaptureController Controller(int maxFrames = 100, TimeSpan? duration = null) =>
        new(new ScrollCaptureOptions { MaxFrames = maxFrames, MaxDuration = duration ?? TimeSpan.FromSeconds(10), StepDelay = TimeSpan.Zero });

    private sealed class FakeSource(params ScrollCaptureFrame[] frames) : IScrollCaptureSource
    {
        public bool IsTargetValid { get; set; } = true;
        public bool IsCancelRequested { get; set; }
        public List<int> Wheels { get; } = [];
        public int Captures { get; private set; }
        public int Delays { get; private set; }
        public Action<FakeSource>? OnCapture { get; set; }
        public Func<CancellationToken, Task<ScrollCaptureFrame>>? CaptureOverride { get; set; }
        public Func<CancellationToken, Task>? DelayOverride { get; set; }
        public bool ScrollAccepted { get; set; } = true;
        public Task<ScrollCaptureFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            Captures++;
            OnCapture?.Invoke(this);
            return CaptureOverride?.Invoke(cancellationToken) ?? Task.FromResult(frames[Math.Min(Captures - 1, frames.Length - 1)]);
        }
        public Task<bool> ScrollAsync(int wheelDelta, CancellationToken cancellationToken)
        {
            Assert.True(IsTargetValid, "Invalid targets must not receive input.");
            Wheels.Add(wheelDelta);
            return Task.FromResult(ScrollAccepted);
        }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays++;
            return DelayOverride?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MultipleFramesStopWithoutClaimingConfirmedBottom()
    {
        var source = new FakeSource(Frame(0), Frame(80), Frame(160), Frame(240));
        var stitcher = new ScrollStitcher(Width, 5000);
        var progress = new List<int>();
        var result = await Controller().RunAsync(source, stitcher, (h, _, _) => progress.Add(h));
        Assert.Equal(ScrollCaptureEndReason.NoFurtherMotion, result.EndReason);
        Assert.True(result.CanExport);
        Assert.False(result.IsComplete);
        Assert.Contains("无法确认", result.Message);
        Assert.Equal(480, stitcher.TotalHeight);
        Assert.Equal(7, source.Captures);
        Assert.Equal(new[] { 240, 320, 400, 480 }, progress);
        Assert.Equal(120, source.Wheels[0]);
        Assert.All(source.Wheels, wheel => Assert.True(wheel % 120 == 0 && wheel is >= 120 and <= 720));
        Assert.Equal(Frame(0).Pixels.Concat(Frame(240).Pixels).ToArray(), stitcher.BuildBgra());
    }

    [Fact]
    public async Task StationaryTargetStopsAfterThreeNoChangeFrames()
    {
        var source = new FakeSource(Frame(0));
        var result = await Controller().RunAsync(source, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.UnableToScroll, result.EndReason);
        Assert.Equal(4, source.Captures);
        Assert.Equal(new[] { 120, 120, 120 }, source.Wheels);
    }

    [Fact]
    public async Task UnstableFirstFrameIsRetriedWithoutAnyInput()
    {
        var source = new FakeSource(Frame(900, false));
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.Incomplete, result.EndReason);
        Assert.False(result.CanExport);
        Assert.Equal(3, source.Captures);
        Assert.Contains("Unstable", result.Message);
        Assert.Empty(source.Wheels);
        Assert.Equal(0, stitcher.TotalHeight);
        Assert.Equal(0, stitcher.FrameCount);
    }

    [Fact]
    public async Task UnstableFramesCanRecoverByRecapturingInPlace()
    {
        var source = new FakeSource(Frame(0), Frame(80, false), Frame(80, false), Frame(80));
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.NoFurtherMotion, result.EndReason);
        Assert.Equal(320, stitcher.TotalHeight);
        Assert.Equal(7, source.Captures);
        Assert.Equal(4, source.Wheels.Count);
    }

    [Fact]
    public async Task NoOverlapRecapturesThenRollsBackAndRecoversPositiveDisplacement()
    {
        var source = new FakeSource(Frame(0), Frame(900), Frame(900), Frame(900), Frame(80));
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.NoFurtherMotion, result.EndReason);
        Assert.Equal(new[] { 120, -120, 120, 120, 120 }, source.Wheels);
        Assert.Equal(320, stitcher.TotalHeight);
        Assert.Equal(Frame(0).Pixels, stitcher.BuildBgra().AsSpan(0, Height * Stride).ToArray());
        Assert.Equal(Frame(80).Pixels.AsSpan((Height - 80) * Stride).ToArray(), stitcher.BuildBgra().AsSpan(Height * Stride).ToArray());
    }

    [Fact]
    public async Task FailedFramesNeverReplaceSuccessfulRegistrationBaseline()
    {
        var source = new FakeSource(Frame(0), Frame(60), Frame(900), Frame(100));
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.NoFurtherMotion, result.EndReason);
        Assert.Equal(340, stitcher.TotalHeight);
        var expected = Frame(0).Pixels.Concat(Frame(100).Pixels.AsSpan((Height - 100) * Stride).ToArray()).ToArray();
        Assert.Equal(expected, stitcher.BuildBgra());
    }

    [Fact]
    public async Task ReturnToExactBaselineRetriesSmallDownwardStep()
    {
        var source = new FakeSource(Frame(0), Frame(900), Frame(900), Frame(900), Frame(0), Frame(80));
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.NoFurtherMotion, result.EndReason);
        Assert.Equal(new[] { 120, -120, 120, 120, 120, 120 }, source.Wheels);
        Assert.Equal(320, stitcher.TotalHeight);
    }

    [Fact]
    public async Task BaselineRetryIsLimitedToTwoSmallDownwardAttempts()
    {
        var source = new FakeSource(Frame(0), Frame(900), Frame(900), Frame(900), Frame(0));
        var result = await Controller().RunAsync(source, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.Incomplete, result.EndReason);
        Assert.Equal(new[] { 120, -120, 120, 120 }, source.Wheels);
    }

    [Fact]
    public async Task FallingBehindBaselineNeverBlindlyScrollsDownOrAppends()
    {
        var source = new FakeSource(Frame(100), Frame(900), Frame(900), Frame(900), Frame(50), Frame(0), Frame(0));
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.Incomplete, result.EndReason);
        Assert.Equal(new[] { 120, -120, -120, -120 }, source.Wheels);
        Assert.Equal(Frame(100).Pixels, stitcher.BuildBgra());
        Assert.Equal(7, source.Captures);
    }

    [Fact]
    public async Task TargetLostAfterCapturePreventsAllInputAndCommit()
    {
        var source = new FakeSource(Frame(0)) { OnCapture = s => s.IsTargetValid = false };
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.TargetLost, result.EndReason);
        Assert.Empty(source.Wheels);
        Assert.False(result.CanExport);
        Assert.Equal(0, stitcher.TotalHeight);
    }

    [Fact]
    public async Task TargetLostInProgressCallbackPreventsInjection()
    {
        var source = new FakeSource(Frame(0));
        var result = await Controller().RunAsync(source, new ScrollStitcher(Width, 5000), (_, _, _) => source.IsTargetValid = false);
        Assert.Equal(ScrollCaptureEndReason.TargetLost, result.EndReason);
        Assert.Empty(source.Wheels);
        Assert.True(result.CanExport);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinishAndCancelInterruptUncooperativeDelay(bool finish)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(Frame(0)) { DelayOverride = _ => { entered.TrySetResult(); return never.Task; } };
        var controller = Controller();
        var stitcher = new ScrollStitcher(Width, 5000);
        var running = controller.RunAsync(source, stitcher);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (finish) controller.RequestFinish(); else controller.RequestCancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(finish ? ScrollCaptureEndReason.Finished : ScrollCaptureEndReason.Cancelled, result.EndReason);
        Assert.Equal(finish, result.CanExport);
        Assert.False(result.IsComplete);
        Assert.Equal(Height, stitcher.TotalHeight);
        never.TrySetResult();
    }

    [Fact]
    public async Task ExternalTokenCancellationDiscardsCommittedContent()
    {
        using var cancel = new CancellationTokenSource();
        var source = new FakeSource(Frame(0));
        var result = await Controller().RunAsync(source, new ScrollStitcher(Width, 5000), (_, _, _) => cancel.Cancel(), cancel.Token);
        Assert.Equal(ScrollCaptureEndReason.Cancelled, result.EndReason);
        Assert.False(result.CanExport);
        Assert.Empty(source.Wheels);
    }

    [Fact]
    public async Task EscapeFlagCancelsAndDominatesFinish()
    {
        var source = new FakeSource(Frame(0));
        var controller = Controller();
        var result = await controller.RunAsync(source, new ScrollStitcher(Width, 5000), (_, _, _) =>
        {
            source.IsCancelRequested = true;
            controller.RequestFinish();
        });
        Assert.Equal(ScrollCaptureEndReason.Cancelled, result.EndReason);
        Assert.False(result.CanExport);
    }

    [Fact]
    public async Task DurationLimitInterruptsUncooperativeCapture()
    {
        var never = new TaskCompletionSource<ScrollCaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(Frame(0)) { CaptureOverride = _ => never.Task };
        var result = await Controller(duration: TimeSpan.FromMilliseconds(50)).RunAsync(source, new ScrollStitcher(Width, 5000)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ScrollCaptureEndReason.DurationLimit, result.EndReason);
        Assert.False(result.CanExport);
        never.TrySetResult(Frame(0));
    }

    [Fact]
    public async Task FrameLimitCountsFailedAndUnstableCaptureAttempts()
    {
        var source = new FakeSource(Frame(0), Frame(80, false));
        var result = await Controller(maxFrames: 3).RunAsync(source, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.FrameLimit, result.EndReason);
        Assert.Equal(3, source.Captures);
        Assert.True(result.CanExport);
    }

    [Fact]
    public async Task HeightBudgetStopsWithExportablePartialContent()
    {
        var source = new FakeSource(Frame(0), Frame(80));
        var stitcher = new ScrollStitcher(Width, 280);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.StitcherLimit, result.EndReason);
        Assert.True(result.CanExport);
        Assert.False(result.IsComplete);
        Assert.InRange(stitcher.TotalHeight, Height, 280);
    }

    [Fact]
    public async Task UnstableInitialFrameCanRecoverBeforeFirstScroll()
    {
        var source = new FakeSource(Frame(900, false), Frame(0));
        var result = await Controller().RunAsync(source, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.UnableToScroll, result.EndReason);
        Assert.Equal(5, source.Captures);
        Assert.Equal(new[] { 120, 120, 120 }, source.Wheels);
    }

    [Fact]
    public async Task FixedContentStopsImmediatelyAndRequestsRecrop()
    {
        var first = Frame(0);
        var second = Frame(80);
        // 固定栏必须含正文内部的横向纹理，不再用整行近似纯色冒充固定文字栏。
        for (var y = 0; y < 24; y++)
        for (var x = 16; x < Width - 16; x++)
        {
            var at = y * Stride + x * 4;
            var shade = (byte)((x / 4 + y / 3) % 2 == 0 ? 25 : 230);
            first.Pixels[at] = first.Pixels[at + 1] = first.Pixels[at + 2] = shade;
        }
        first.Pixels.AsSpan(0, 24 * Stride).CopyTo(second.Pixels.AsSpan(0, 24 * Stride));
        var source = new FakeSource(first, second);
        var stitcher = new ScrollStitcher(Width, 5000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.Incomplete, result.EndReason);
        Assert.Contains("重新裁选", result.Message);
        Assert.Equal(new[] { 120 }, source.Wheels);
        Assert.Equal(2, source.Captures);
        Assert.Equal(first.Pixels, stitcher.BuildBgra());
    }

    [Fact]
    public async Task MemoryBudgetPreservesAlreadyCommittedContent()
    {
        var source = new FakeSource(Frame(0), Frame(80));
        var stitcher = new ScrollStitcher(Width, 5000, 24, 500_000);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.StitcherLimit, result.EndReason);
        Assert.True(result.CanExport);
        Assert.Equal(new[] { 120 }, source.Wheels);
        Assert.Equal(Frame(0).Pixels, stitcher.BuildBgra());
    }

    [Fact]
    public async Task MemoryBudgetStopsWithoutTryingRecoveryInput()
    {
        var source = new FakeSource(Frame(0));
        var stitcher = new ScrollStitcher(Width, 5000, 24, 1);
        var result = await Controller().RunAsync(source, stitcher);
        Assert.Equal(ScrollCaptureEndReason.StitcherLimit, result.EndReason);
        Assert.Equal(1, source.Captures);
        Assert.Empty(source.Wheels);
        Assert.False(result.CanExport);
    }

    [Fact]
    public async Task CalibrationUsesMeasuredDisplacementAndConfiguredNotchCap()
    {
        var source = new FakeSource(Frame(0), Frame(10), Frame(20));
        using var controller = new ScrollCaptureController(new ScrollCaptureOptions { MaxWheelNotches = 2 });
        var result = await controller.RunAsync(source, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.NoFurtherMotion, result.EndReason);
        Assert.Equal(new[] { 120, 240, 240, 120, 120 }, source.Wheels);
    }

    [Fact]
    public async Task DisposeWhileWaitingCancelsAndRepeatedSignalsAreSafe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(Frame(0)) { DelayOverride = _ => { entered.TrySetResult(); return never.Task; } };
        var controller = Controller();
        var running = controller.RunAsync(source, new ScrollStitcher(Width, 5000));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        controller.Dispose();
        controller.RequestFinish();
        controller.RequestCancel();
        controller.Dispose();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ScrollCaptureEndReason.Cancelled, result.EndReason);
        Assert.False(result.CanExport);
        never.TrySetResult();
    }

    [Fact]
    public async Task InvalidFrameIsRejectedWithoutAnyInput()
    {
        var source = new FakeSource(new ScrollCaptureFrame(new byte[2], Width, Height, Stride, true));
        var result = await Controller().RunAsync(source, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.InvalidFrame, result.EndReason);
        Assert.Empty(source.Wheels);
    }

    [Fact]
    public async Task CaptureAndScrollFailuresAreDistinct()
    {
        var failedCapture = new FakeSource(Frame(0)) { CaptureOverride = _ => throw new InvalidOperationException("capture") };
        var captureResult = await Controller().RunAsync(failedCapture, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.CaptureFailed, captureResult.EndReason);
        Assert.Contains("capture", captureResult.Message);
        var failedScroll = new FakeSource(Frame(0)) { ScrollAccepted = false };
        var scrollResult = await Controller().RunAsync(failedScroll, new ScrollStitcher(Width, 5000));
        Assert.Equal(ScrollCaptureEndReason.ScrollFailed, scrollResult.EndReason);
        Assert.True(scrollResult.CanExport);
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) { lock (_queue) _queue.Enqueue((callback, state)); }
        public bool Pump()
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (_queue)
            {
                if (_queue.Count == 0) return false;
                work = _queue.Dequeue();
            }
            work.Callback(work.State);
            return true;
        }
    }

    [Fact]
    public void EveryStepYieldsWhenAllSourceTasksAreAlreadyCompleted()
    {
        var prior = SynchronizationContext.Current;
        var context = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var source = new FakeSource(Frame(0));
            var controller = Controller();
            var running = controller.RunAsync(source, new ScrollStitcher(Width, 5000));
            Assert.False(running.IsCompleted);
            Assert.Equal(0, source.Captures);
            Assert.True(context.Pump());
            Assert.Equal(0, source.Captures); // Capture 再次让出，而不是沿 Run 的 continuation 同步执行。
            controller.RequestCancel();
            while (context.Pump()) { }
        }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
    }
}
