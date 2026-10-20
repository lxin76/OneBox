using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Plugins.Screenshot.Services;

public sealed record ScrollProgress(int CapturedHeight, int FrameCount, string Status)
{
    public static ScrollProgress Empty { get; } = new(0, 0, "准备中…");
}

/// <summary>WPF/Win32 适配器；滚动决策与恢复由可测试的 Core 控制器处理。</summary>
public sealed class ScrollCaptureSession : IDisposable
{
    private readonly System.Drawing.Rectangle _region;
    private readonly ScreenshotSettings _settings;
    private readonly IntPtr _targetWindow;
    private readonly ScrollCaptureController _controller;
    private readonly CancellationTokenSource _lifetime = new();
    public ScrollCaptureResult? Outcome { get; private set; }
    private DesktopScrollSource? _source;

    /// <summary>仅用户明确同意后将有界内存中的采样写到本地，不自动上传。</summary>
    public Task<string?> SaveFailureDiagnosticsAsync() => _source?.SaveFailureDiagnosticsAsync() ?? Task.FromResult<string?>(null);

    public ScrollCaptureSession(System.Drawing.Rectangle region, ScreenshotSettings settings,
        Action<bool>? progressControl = null, IntPtr targetWindow = default)
    {
        _region = region;
        _settings = settings;
        _targetWindow = targetWindow;
        _controller = new ScrollCaptureController(new ScrollCaptureOptions { MaxWheelNotches = Math.Clamp(settings.ScrollWheelNotches, 1, 6) })
        { DiagnosticLog = message => AppLog.Write("滚动配准诊断：" + message) };
    }

    /// <summary>显式完成保留已确认内容；取消和 Esc 永不导出。</summary>
    public void Stop() => _controller.RequestFinish();
    public void Cancel() { _controller.RequestCancel(); _lifetime.Cancel(); }
    public void Dispose() { _controller.Dispose(); _lifetime.Dispose(); }

    public async Task<BitmapSource?> RunAsync(Action<ScrollProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = linked.Token;
        var dispatcher = Application.Current?.Dispatcher;
        DispatcherTimer? keyTimer = null;
        var original = ScreenCapture.GetCursorPosition();
        var movedCursor = false;
        DesktopScrollSource? source = null;
        var x = _region.Left + _region.Width / 2;
        var y = _region.Top + _region.Height / 2;
        void Report(string text)
        {
            var update = new ScrollProgress(0, 0, text);
            if (dispatcher is null || dispatcher.CheckAccess()) onProgress?.Invoke(update);
            else dispatcher.BeginInvoke(new Action(() => onProgress?.Invoke(update)));
        }
        try
        {
            // 让首次 Show/Loaded 和按钮消息先得到处理；这里不再同步占用 UI Dispatcher。
            await Task.Yield();
            if (dispatcher is not null)
            {
                keyTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
                keyTimer.Tick += (_, _) =>
                {
                    if (ScrollInjector.IsEscapePressed()) Cancel();
                    else if ((Native.GetAsyncKeyState(0x77) & 0x8000) != 0) Stop(); // F8，当前会话内轮询，不注册全局热键。
                };
                keyTimer.Start();
            }
            Report("请松开修饰键和鼠标键；F8 完成，Esc 取消");
            var releaseDeadline = Stopwatch.StartNew();
            while (!ScrollInjector.AreInputButtonsReleased())
            {
                if (releaseDeadline.Elapsed > TimeSpan.FromSeconds(5))
                    return Fail(ScrollCaptureEndReason.ScrollFailed, "按键未释放，未发送任何滚动输入。", onProgress);
                await Task.Delay(40, token);
            }
            if (_targetWindow == IntPtr.Zero || !Native.IsWindow(_targetWindow) || Native.IsIconic(_targetWindow))
                return Fail(ScrollCaptureEndReason.TargetLost, "目标窗口无效或已最小化。", onProgress);
            Native.GetWindowThreadProcessId(_targetWindow, out var pid);
            if (pid == Environment.ProcessId)
                return Fail(ScrollCaptureEndReason.TargetLost, "选区命中了截图工具自身，请重新选择正文区域。", onProgress);
            // 只在选择结束时请求一次前台，不 AttachThreadInput、不抢回用户随后切换的窗口。
            if (Native.GetForegroundWindow() != _targetWindow) Native.SetForegroundWindow(_targetWindow);
            await Task.Delay(150, token);
            if (Native.GetForegroundWindow() != _targetWindow)
                return Fail(ScrollCaptureEndReason.TargetLost, "无法确认目标前台，请切回目标页面后重新截图。", onProgress);
            if (!Native.SetCursorPos(x, y))
                return Fail(ScrollCaptureEndReason.ScrollFailed, "无法定位滚动作用点。", onProgress);
            movedCursor = true;
            await Task.Delay(120, token);
            if ((long)_region.Width * _region.Height * 4 > ScrollStitcher.DefaultMemoryBudgetBytes / 6)
                return Fail(ScrollCaptureEndReason.StitcherLimit, "选区首帧过大，无法在内存预算内安全采样，请缩小选区。", onProgress);
            source = new DesktopScrollSource(_region, _targetWindow, _controller, _settings.ScrollMinMatchRows, Report);
            _source = source;
            if (!source.IsTargetValid)
                return Fail(ScrollCaptureEndReason.TargetLost, "选区被其他窗口遮挡，或跨出了目标窗口。", onProgress);
            var stitcher = new ScrollStitcher(_region.Width, _settings.ScrollMaxHeight, _settings.ScrollMinMatchRows);
            AppLog.Write($"滚动截图 v2 开始：选区 {_region} 目标=0x{_targetWindow.ToInt64():X} PID={pid}；小步滚轮，相邻原始帧配准");
            Outcome = await Task.Run(() => _controller.RunAsync(source, stitcher,
                (height, frames, status) =>
                {
                    AppLog.Write($"滚动截图 v2: 帧={frames} 高={height} {status}");
                    var update = new ScrollProgress(height, frames, status);
                    if (dispatcher is null) onProgress?.Invoke(update);
                    else dispatcher.BeginInvoke(new Action(() => onProgress?.Invoke(update)), DispatcherPriority.Background);
                }, token), token);
            AppLog.Write($"滚动截图 v2 结束：{Outcome.EndReason} 可导出={Outcome.CanExport} 完整={Outcome.IsComplete} {Outcome.Message}");
            if (token.IsCancellationRequested)
                Outcome = new(ScrollCaptureEndReason.Cancelled, "已取消，本次结果已丢弃。", false, false);
            if (!Outcome.CanExport || stitcher.TotalHeight == 0) return null;
            var image = await Task.Run(() => ImageCodec.FromBgraBytes(stitcher.BuildBgra(), _region.Width, stitcher.TotalHeight), token);
            // 导出也可能耗时；期间收到取消时不得把已经生成的部分结果交给预览/剪贴板。
            token.ThrowIfCancellationRequested();
            await source.CompleteDiagnosticsAsync(discard: false);
            // 清理等待也允许用户取消，最后返回前再设一道闸门。
            token.ThrowIfCancellationRequested();
            return image;
        }
        catch (OperationCanceledException)
        {
            Outcome = new(ScrollCaptureEndReason.Cancelled, "已取消，本次结果已丢弃。", false, false);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"滚动截图 v2 异常: {ex}");
            return Fail(ScrollCaptureEndReason.CaptureFailed, "截图失败：" + ex.Message, onProgress);
        }
        finally
        {
            keyTimer?.Stop();
            if (source is not null)
                await source.CompleteDiagnosticsAsync(Outcome?.EndReason == ScrollCaptureEndReason.Cancelled || token.IsCancellationRequested);
            // 用户已移动鼠标时不抢回；只有仍停留在本次定位点才恢复。
            var current = ScreenCapture.GetCursorPosition();
            if (movedCursor && Math.Abs(current.X - x) <= 3 && Math.Abs(current.Y - y) <= 3)
                Native.SetCursorPos(original.X, original.Y);
        }
    }

    private BitmapSource? Fail(ScrollCaptureEndReason reason, string message, Action<ScrollProgress>? progress)
    {
        Outcome = new(reason, message, false, false);
        AppLog.Write($"滚动截图 v2 停止：{reason} {message}");
        progress?.Invoke(new ScrollProgress(0, 0, message));
        return null;
    }

    private sealed class DesktopScrollSource : IScrollCaptureSource
    {
        private readonly System.Drawing.Rectangle _region;
        private readonly IntPtr _root;
        private readonly Native.RECT _initialRect;
        private readonly ScrollCaptureController _controller;
        private readonly Action<string> _report;
        private readonly string? _diagnostics;
        private int _frameIndex;
        private readonly List<ScrollCaptureFrame> _failureFrames = [];
        private readonly List<int> _failureFrameIndices = [];
        private long _failureBytes;
        private readonly List<object> _inputEvents = [];
        private readonly int _minMatchRows;
        private readonly SemaphoreSlim _captureGate = new(1, 1);
        private readonly object _diagnosticWriteGate = new();
        private int X => _region.Left + _region.Width / 2;
        private int Y => _region.Top + _region.Height / 2;

        public DesktopScrollSource(System.Drawing.Rectangle region, IntPtr root, ScrollCaptureController controller, int minMatchRows, Action<string> report)
        {
            _region = region;
            _root = root;
            _controller = controller;
            _report = report;
            _minMatchRows = minMatchRows;
            Native.GetWindowRect(root, out _initialRect);
            if (Environment.GetEnvironmentVariable("ONEBOX_SCROLL_DIAGNOSTICS") == "1")
            {
                _diagnostics = Path.Combine(AppPaths.DataDirectory, "scroll-diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(_diagnostics);
                File.WriteAllText(Path.Combine(_diagnostics, "session.json"), JsonSerializer.Serialize(new { version = 2, width = region.Width, height = region.Height, minMatchRows }));
                AppLog.Write($"滚动截图：已显式启用原始帧诊断，目录 {_diagnostics}；图像可能包含敏感内容。");
            }
        }

        public bool IsCancelRequested => ScrollInjector.IsEscapePressed();

        public bool IsTargetValid
        {
            get
            {
                if (!ScrollInjector.IsTargetReady(_root, X, Y) || !Native.GetWindowRect(_root, out var now)) return false;
                if (now.Left != _initialRect.Left || now.Top != _initialRect.Top || now.Right != _initialRect.Right || now.Bottom != _initialRect.Bottom) return false;
                // 检查整个选区是否与目标上方的可见窗口相交，九点命中只作辅助归属校验。
                if (!ScrollOcclusionGuard.IsRegionUnobscured(_root, _region)) return false;
                foreach (var x in new[] { _region.Left + 2, X, _region.Right - 3 })
                foreach (var y in new[] { _region.Top + 2, Y, _region.Bottom - 3 })
                {
                    var hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
                    var root = Native.GetAncestor(hit, Native.GA_ROOT);
                    if (hit != _root && root != _root) return false;
                }
                return true;
            }
        }

        public async Task<bool> ScrollAsync(int wheelDelta, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (IsCancelRequested) { _controller.RequestCancel(); return false; }
            // 用户移向完成/取消按钮时先暂停，不抢回鼠标，也不抢先导出部分结果。
            var takeover = Stopwatch.StartNew();
            var pauseReported = false;
            System.Drawing.Point actual;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsTargetValid) throw new InvalidOperationException("目标失去前台、位置改变或选区被遮挡，未发送滚轮。");
                actual = ScreenCapture.GetCursorPosition();
                var ready = ScrollInputPolicy.IsInsideCapture(_region.Left, _region.Top, _region.Width, _region.Height, actual.X, actual.Y, ScrollInjector.AreInputButtonsReleased())
                    && ScrollInjector.IsTargetReady(_root, actual.X, actual.Y);
                if (ready) break;
                var detail = $"作用点={X},{Y} 实际光标={actual.X},{actual.Y} 按键已释放={ScrollInjector.AreInputButtonsReleased()}";
                if (!pauseReported)
                {
                    pauseReported = true;
                    AppLog.Write("滚动截图暂停：" + detail);
                    _report("已暂停：请把鼠标移到选区正文内并松开按键；F8 完成，Esc 取消");
                }
                if (takeover.Elapsed > TimeSpan.FromSeconds(8))
                {
                    AppLog.Write("滚动截图暂停超时：" + detail);
                    throw new InvalidOperationException("鼠标已移出选区正文或按键未释放，等待 8 秒后停止；尚未发送本次滚轮。截图期间请将鼠标留在选区正文内，无需对准中心。");
                }
                await DelayAsync(TimeSpan.FromMilliseconds(40), token);
            }
            // 使用选区内当前真实落点；投递器仍复核目标归属、前台和瞬时坐标，不追随选区外鼠标。
            var sent = ScrollInjector.TrySendWheel(_root, actual.X, actual.Y, wheelDelta);
            lock (_diagnosticWriteGate) _inputEvents.Add(new { timestamp = DateTime.UtcNow, wheelDelta, sent, cursorX = actual.X, cursorY = actual.Y });
            AppLog.Write($"滚动截图输入：delta={wheelDelta} accepted={sent} 光标={actual.X},{actual.Y}");
            if (!sent) AppLog.Write($"滚动输入被拒：root=0x{_root.ToInt64():X} 光标={actual.X},{actual.Y} 前台=0x{Native.GetForegroundWindow().ToInt64():X} 按键已释放={ScrollInjector.AreInputButtonsReleased()}");
            if (_diagnostics is not null)
            {
                lock (_diagnosticWriteGate)
                {
                    token.ThrowIfCancellationRequested();
                    File.AppendAllText(Path.Combine(_diagnostics, "events.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, wheelDelta, sent }) + Environment.NewLine);
                }
            }
            // 一次输入后异步让出，页面自身会应用系统的每格行数设置。
            await Task.Yield();
            return sent;
        }

        public async Task<ScrollCaptureFrame> CaptureAsync(CancellationToken token)
        {
            await _captureGate.WaitAsync(token);
            try { return await CaptureCoreAsync(token); }
            finally { _captureGate.Release(); }
        }

        private async Task<ScrollCaptureFrame> CaptureCoreAsync(CancellationToken token)
        {
            if (!IsTargetValid) throw new InvalidOperationException("目标窗口已变化或选区被遮挡。");
            var previous = await GrabAsync(token);
            var watch = Stopwatch.StartNew();
            var stableCount = 0;
            while (watch.Elapsed < TimeSpan.FromMilliseconds(2200))
            {
                await DelayAsync(TimeSpan.FromMilliseconds(100), token);
                if (!IsTargetValid) throw new InvalidOperationException("采样期间目标窗口丢失。");
                var next = await GrabAsync(token);
                if (!IsTargetValid) throw new InvalidOperationException("抓帧后目标或遮挡状态发生变化。");
                var score = await Task.Run(() => PixelChangeFraction(previous.Pixels, next.Pixels), token);
                stableCount = score < 0.0005 ? stableCount + 1 : 0;
                previous = next;
                if (stableCount >= 3)
                {
                    if (!IsTargetValid) throw new InvalidOperationException("提交帧前目标窗口丢失。");
                    return await RecordAsync(next with { Stable = true }, token);
                }
            }
            return await RecordAsync(previous with { Stable = false }, token);
        }

        private Task<ScrollCaptureFrame> GrabAsync(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var image = ScreenCapture.CaptureRegion(_region) ?? throw new InvalidOperationException("无法抓取选区像素。");
            var bytes = ImageCodec.ToBgraBytes(image, out var stride, out var width, out var height);
            return new ScrollCaptureFrame(bytes, width, height, stride, false);
        }, token);

        private async Task<ScrollCaptureFrame> RecordAsync(ScrollCaptureFrame frame, CancellationToken token)
        {
            var index = Interlocked.Increment(ref _frameIndex);
            // 仅内存保留首帧和最近采样，总计至多24MiB/4帧；不经确认绝不落盘。
            if (frame.Pixels.LongLength <= 8L * 1024 * 1024)
            {
                while (_failureFrames.Count > 1 && (_failureFrames.Count >= 4 || _failureBytes + frame.Pixels.LongLength > 24L * 1024 * 1024))
                {
                    // 优先保留首帧和第一次滚动后的邻帧，避免回退采样覆盖最重要的失败证据。
                    var remove = _failureFrames.Count > 2 ? 2 : 1;
                    _failureBytes -= _failureFrames[remove].Pixels.LongLength;
                    _failureFrames.RemoveAt(remove);
                    _failureFrameIndices.RemoveAt(remove);
                }
                if (_failureBytes + frame.Pixels.LongLength <= 24L * 1024 * 1024)
                {
                    _failureFrames.Add(frame);
                    _failureFrameIndices.Add(index);
                    _failureBytes += frame.Pixels.LongLength;
                }
            }
            if (_diagnostics is not null)
                await Task.Run(() =>
                {
                    var image = ImageCodec.FromBgraBytes(frame.Pixels, frame.Width, frame.Height);
                    ImageCodec.SaveTo(image, Path.Combine(_diagnostics, $"{index:D5}.png"), ShotImageFormat.Png, 100);
                    File.AppendAllText(Path.Combine(_diagnostics, "events.jsonl"), JsonSerializer.Serialize(new { frame = index, frame.Stable }) + Environment.NewLine);
                }, token);
            return frame;
        }

        public async Task<string?> SaveFailureDiagnosticsAsync()
        {
            await _captureGate.WaitAsync();
            try
            {
                if (_failureFrames.Count < 2) return null;
                var directory = Path.Combine(AppPaths.DataDirectory, "scroll-diagnostics", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-consented");
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(directory);
                    for (var i = 0; i < _failureFrames.Count; i++)
                    {
                        var frame = _failureFrames[i];
                        ImageCodec.SaveTo(ImageCodec.FromBgraBytes(frame.Pixels, frame.Width, frame.Height), Path.Combine(directory, $"{i + 1:D5}.png"), ShotImageFormat.Png, 100);
                    }
                    File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new { version = 2, minMatchRows = _minMatchRows, retainedFrames = _failureFrames.Count, originalFrameIndices = _failureFrameIndices.ToArray(), sampledSubset = true, warning = "仅首帧与最近采样，可能省略中间帧；不能当成完整滚动序列", events = _inputEvents.ToArray() }));
                });
                return directory;
            }
            finally { _captureGate.Release(); }
        }

        public async Task CompleteDiagnosticsAsync(bool discard)
        {
            // Controller 的 WaitAsync 可能先取消等待；必须等内部写入退出再清理，避免取消后补写。
            await _captureGate.WaitAsync();
            try
            {
                if (discard) { _failureFrames.Clear(); _failureFrameIndices.Clear(); _failureBytes = 0; }
                if (discard && _diagnostics is not null && Directory.Exists(_diagnostics))
                {
                    try { lock (_diagnosticWriteGate) Directory.Delete(_diagnostics, recursive: true); }
                    catch (Exception ex) { AppLog.Write($"取消后的诊断清理失败，请手动删除 {_diagnostics}: {ex.Message}"); }
                }
            }
            finally { _captureGate.Release(); }
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < delay)
            {
                token.ThrowIfCancellationRequested();
                if (IsCancelRequested) { _controller.RequestCancel(); throw new OperationCanceledException(token); }
                if ((Native.GetAsyncKeyState(0x77) & 0x8000) != 0) _controller.RequestFinish();
                var remaining = delay - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(40) ? remaining : TimeSpan.FromMilliseconds(40), token);
            }
        }

        private static double PixelChangeFraction(byte[] a, byte[] b)
        {
            if (a.Length != b.Length || a.Length == 0) return 1;
            long changed = 0;
            var pixels = a.Length / 4;
            // 不降采样，避免采样点恰好避开稀疏文字；允许少量颜色噪声。
            for (var i = 0; i < a.Length; i += 4)
                if (Math.Abs(a[i] - b[i]) > 8 || Math.Abs(a[i + 1] - b[i + 1]) > 8 || Math.Abs(a[i + 2] - b[i + 2]) > 8) changed++;
            return (double)changed / pixels;
        }
    }
}
