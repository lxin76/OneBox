using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OneBox.Plugins.Screenshot.Core;
using OneBox.Plugins.Screenshot.Services;
using RectI = System.Drawing.Rectangle;

namespace ScrollIntegration;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var diagnoseOnly = args.Contains("--diagnose-capture-only");
        if (!args.Contains("--run-controlled-input") && !diagnoseOnly)
        {
            Console.WriteLine("仅构建不触发输入。经确认后运行：dotnet run --project tools/scroll-integration/ScrollIntegration.csproj -- --run-controlled-input");
            return 0;
        }
        var processDpiSet = Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        var processDpiError = Marshal.GetLastWin32Error();
        var output = Path.Combine(AppContext.BaseDirectory, "artifacts");
        // 由项目目录锚定输出，不依赖调用者的工作目录。
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project is not null && !File.Exists(Path.Combine(project.FullName, "ScrollIntegration.csproj"))) project = project.Parent;
        if (project is null) throw new InvalidOperationException("无法定位测试项目目录。");
        output = Path.Combine(project.FullName, "artifacts", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var fixture = new Fixture();
        var viewer = new ScrollViewer
        {
            Content = fixture, Background = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, PanningMode = PanningMode.None,
        };
        var window = new Window
        {
            Title = "OneBox 受控滚动集成测试 — 移动鼠标 / 切换窗口 / Esc 即停止",
            Width = 640, Height = 500, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White, Content = viewer, UseLayoutRounding = true, SnapsToDevicePixels = true,
        };
        var ticks = 0;
        var maxTickGap = 0d;
        var watch = Stopwatch.StartNew();
        var lastTick = watch.Elapsed.TotalMilliseconds;
        ControlledSource? source = null;
        using var controller = new ScrollCaptureController(new ScrollCaptureOptions
        {
            MaxWheelNotches = 1, MaxFrames = 160, MaxDuration = TimeSpan.FromSeconds(100), StepDelay = TimeSpan.FromMilliseconds(120),
        });
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            var now = watch.Elapsed.TotalMilliseconds;
            maxTickGap = Math.Max(maxTickGap, now - lastTick);
            lastTick = now;
            if (source is not null) _ = source.IsTargetValid; // 及时锁定用户接管，永不重新激活/重新定位。
        };
        window.Closing += (_, _) => controller.RequestCancel();
        var exit = 2;
        window.Loaded += async (_, _) =>
        {
            ScrollCaptureResult? result = null;
            ScrollStitcher? stitcher = null;
            object? validation = null;
            string? error = null;
            object? startup = null;
            var progress = new ConcurrentQueue<object>();
            var captureStartedTicks = 0;
            try
            {
                var activated = window.Activate(); // 唯一一次启动激活，只针对本进程测试窗。
                var root = new WindowInteropHelper(window).Handle;
                var foregroundBefore = Native.GetForegroundWindow();
                var setForeground = foregroundBefore == root || Native.SetForegroundWindow(root);
                var foregroundError = Marshal.GetLastWin32Error();
                await Task.Delay(250);
                viewer.UpdateLayout();
                var p0 = viewer.PointToScreen(new Point(0, 0));
                var p1 = viewer.PointToScreen(new Point(viewer.ActualWidth, viewer.ActualHeight));
                var region = RectI.FromLTRB((int)Math.Ceiling(p0.X), (int)Math.Ceiling(p0.Y), (int)Math.Floor(p1.X), (int)Math.Floor(p1.Y));
                if (diagnoseOnly)
                {
                    var dpiForTruth = VisualTreeHelper.GetDpi(viewer);
                    var full = new Fixture { Width = viewer.ActualWidth };
                    full.Measure(new Size(viewer.ActualWidth, Fixture.DocumentHeight));
                    full.Arrange(new Rect(0, 0, viewer.ActualWidth, Fixture.DocumentHeight));
                    var truthOnly = new RenderTargetBitmap(region.Width, (int)Math.Round(Fixture.DocumentHeight * dpiForTruth.DpiScaleY),
                        dpiForTruth.PixelsPerInchX, dpiForTruth.PixelsPerInchY, PixelFormats.Pbgra32);
                    truthOnly.Render(full);
                    Save(truthOnly, Path.Combine(output, "groundtruth.png"));
                    var rows = new List<object>();
                    startup = new { processDpiSet, processDpiError, root = root.ToInt64(), foreground = Native.GetForegroundWindow().ToInt64(),
                        p0, p1, region, desktop = Native.DesktopDiagnostic(), samples = rows };
                    void Sample(string name, bool scoped)
                    {
                        var before = Native.DpiDiagnostic(root);
                        var previous = scoped ? Native.SetThreadDpiAwarenessContext(new IntPtr(-4)) : IntPtr.Zero;
                        var setError = Marshal.GetLastWin32Error();
                        try
                        {
                            var active = Native.DpiDiagnostic(root);
                            var ownForeground = Native.GetForegroundWindow() == root;
                            var unobscured = ScrollOcclusionGuard.IsRegionUnobscured(root, region);
                            if (!ownForeground || !unobscured)
                            {
                                rows.Add(new { name, scoped, previousContext = previous.ToInt64(), setError, before, active, ownForeground, unobscured, skipped = true });
                                return;
                            }
                            var image = ScreenCapture.CaptureRegion(region) ?? throw new InvalidOperationException("diagnostic capture failed");
                            Save(image, Path.Combine(output, name + ".png"));
                            rows.Add(new { name, scoped, previousContext = previous.ToInt64(), setError, before, active, ownForeground, unobscured });
                        }
                        finally { if (scoped && previous != IntPtr.Zero) Native.SetThreadDpiAwarenessContext(previous); }
                    }
                    Sample("ui-default", false);
                    Sample("ui-pmv2", true);
                    await Task.Run(() => { Sample("worker-default", false); Sample("worker-pmv2", true); });
                    startup = new { processDpiSet, processDpiError, root = root.ToInt64(), foreground = Native.GetForegroundWindow().ToInt64(),
                        p0, p1, region, desktop = Native.DesktopDiagnostic(), samples = rows };
                    exit = 0;
                    return;
                }
                var foregroundAfter = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(foregroundAfter, out var foregroundPid);
                startup = new { root = root.ToInt64(), foregroundBefore = foregroundBefore.ToInt64(),
                    foregroundAfter = foregroundAfter.ToInt64(), foregroundPid, ownPid = Environment.ProcessId, activated, setForeground, foregroundError,
                    region, dpi = VisualTreeHelper.GetDpi(viewer).DpiScaleY,
                    rootRect = Native.WindowRectOf(root), foregroundRect = Native.WindowRectOf(foregroundAfter),
                    desktop = Native.DesktopDiagnostic() };
                if (region.Width < 100 || region.Height < 100)
                    throw new InvalidOperationException($"正文物理区域尺寸无效：{region}。");
                if (foregroundAfter != root)
                    throw new InvalidOperationException($"自家测试窗口未进入前台，拒绝注入：root=0x{root.ToInt64():X}, foreground=0x{foregroundAfter.ToInt64():X}, pid={foregroundPid}, activate={activated}, SetForegroundWindow={setForeground}, error={foregroundError}。");
                var x = region.Left + region.Width / 2;
                var y = region.Top + region.Height / 2;
                if (!ScrollInjector.AreInputButtonsReleased() || ScrollInjector.IsEscapePressed())
                    throw new InvalidOperationException("启动时用户按键未释放，拒绝移动光标。");
                if (!Native.SetCursorPos(x, y)) throw new InvalidOperationException($"SetCursorPos failed: {Marshal.GetLastWin32Error()}");
                source = new ControlledSource(root, region, viewer, output, () => ticks);
                timer.Start();
                await Task.Delay(150);
                if (!source.IsTargetValid) throw new InvalidOperationException(source.LostReason);
                var dpi = VisualTreeHelper.GetDpi(viewer);
                var truthFixture = new Fixture { Width = viewer.ActualWidth };
                truthFixture.Measure(new Size(viewer.ActualWidth, Fixture.DocumentHeight));
                truthFixture.Arrange(new Rect(0, 0, viewer.ActualWidth, Fixture.DocumentHeight));
                var truth = new RenderTargetBitmap(region.Width, (int)Math.Round(Fixture.DocumentHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                truth.Render(truthFixture);
                truth.Freeze();
                Save(truth, Path.Combine(output, "groundtruth.png"));
                var preview = ScreenCapture.CaptureRegion(region) ?? throw new InvalidOperationException("首屏捕获失败。");
                Save(preview, Path.Combine(output, "window-preview.png"));
                var previewValidation = Validate(preview, truth, dpi.DpiScaleX, dpi.DpiScaleY);
                if (!previewValidation.ActualParagraphIds.Take(3).SequenceEqual(new[] { 0, 1, 2 }))
                    throw new InvalidOperationException("ScreenCapture 首屏缺少自家 fixture 的 0/1/2 编号色条；像素不属于预期正文，拒绝发送任何滚轮。");
                stitcher = new ScrollStitcher(region.Width, 10000, 24);
                captureStartedTicks = ticks;
                result = await Task.Run(() => controller.RunAsync(source, stitcher,
                    (height, frames, status) => progress.Enqueue(new { atMs = watch.ElapsedMilliseconds, height, frames, status,
                        delta = stitcher.LastScrollDelta, matchedRows = stitcher.LastMatchedRows, bestPosition = stitcher.LastBestPosition })));
                if (stitcher.TotalHeight > 0)
                {
                    var stitched = ImageCodec.FromBgraBytes(stitcher.BuildBgra(), region.Width, stitcher.TotalHeight);
                    Save(stitched, Path.Combine(output, "stitched.png"));
                    validation = Validate(stitched, truth, dpi.DpiScaleX, dpi.DpiScaleY);
                }
                var heightPass = Math.Abs(stitcher.TotalHeight - truth.PixelHeight) <= 2;
                var offsetsAtBottom = Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) <= 0.5;
                // 不改变生产控制器的保守完整性结果；独立 fixture 证据另行报告。
                exit = heightPass && offsetsAtBottom && source.LostReason is null && ticks - captureStartedTicks >= 5
                    && validation is Validation { MarkerOrderPass: true } ? 0 : 1;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                Console.Error.WriteLine(error);
                exit = 2;
            }
            finally
            {
                timer.Stop();
                var metadata = new
                {
                    utc = DateTimeOffset.UtcNow, exitCode = exit, error, startup, result, validation,
                    fixture = new { numberedParagraphs = Fixture.Paragraphs, expectedLast = "END-024", heightDip = Fixture.DocumentHeight,
                        viewer.ExtentHeight, viewer.ViewportHeight, viewer.VerticalOffset, viewer.ScrollableHeight },
                    ui = new { timerTicks = ticks, captureTicks = ticks - captureStartedTicks, maxTickGapMs = maxTickGap },
                    wheelCalls = source?.WheelCalls ?? 0, deliveredWheels = source?.DeliveredWheels ?? 0,
                    source?.LostReason, source?.Region, frames = stitcher?.FrameCount, stitchedHeight = stitcher?.TotalHeight,
                    events = source?.Events.ToArray(), progress = progress.ToArray(),
                    limitations = "非原子前台检查；RenderTargetBitmap 与屏幕文字抗锯齿可能不同。控制器不凭静止确认到底；fixture 用高度与唯一颜色段落编号独立验证，无 OCR 冒充。",
                };
                File.WriteAllText(Path.Combine(output, "metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Artifacts: {output}");
                app.Shutdown(exit);
            }
        };
        app.Run(window);
        return exit;
    }

    private static void Save(BitmapSource image, string path) => File.WriteAllBytes(path, ImageCodec.EncodePng(image));

    private sealed record Validation(bool HeightPass, int ActualHeight, int ExpectedHeight, bool MarkerOrderPass,
        int[] ActualParagraphIds, int[] ExpectedParagraphIds, double PixelMeanAbsoluteDifference, string EndMarker);

    private static Validation Validate(BitmapSource actual, BitmapSource truth, double sx, double sy)
    {
        var a = ImageCodec.ToBgraBytes(actual, out var stride, out var width, out var height);
        var t = ImageCodec.ToBgraBytes(truth, out var truthStride, out _, out var truthHeight);
        var x = Math.Clamp((int)Math.Round((width / sx - 55) * sx), 0, width - 1);
        var ids = new List<int>();
        var previous = -1;
        for (var y = 0; y < height; y++)
        {
            var at = y * stride + x * 4;
            var id = -1;
            for (var candidate = 0; candidate < Fixture.Paragraphs; candidate++)
            {
                var color = Fixture.MarkerColor(candidate);
                if (Math.Abs(a[at] - color.B) <= 2 && Math.Abs(a[at + 1] - color.G) <= 2 && Math.Abs(a[at + 2] - color.R) <= 2)
                { id = candidate; break; }
            }
            if (id >= 0 && id != previous) ids.Add(id);
            previous = id;
        }
        double difference = 0;
        long samples = 0;
        for (var y = 0; y < Math.Min(height, truthHeight); y++)
            for (var xx = 0; xx < width; xx++)
                for (var c = 0; c < 3; c++)
                { difference += Math.Abs(a[y * stride + xx * 4 + c] - t[y * truthStride + xx * 4 + c]); samples++; }
        var expected = Enumerable.Range(0, Fixture.Paragraphs).ToArray();
        return new Validation(Math.Abs(height - truthHeight) <= 2, height, truthHeight, ids.SequenceEqual(expected),
            ids.ToArray(), expected, samples == 0 ? 0 : difference / samples,
            ids.Contains(Fixture.Paragraphs - 1) ? "END-024 marker present" : "END-024 marker missing");
    }

    private sealed class Fixture : FrameworkElement
    {
        public const int Paragraphs = 25;
        public const double DocumentHeight = Paragraphs * 100;
        public static Color MarkerColor(int id) => Color.FromRgb((byte)(25 + id * 7), (byte)(50 + id * 3), (byte)(90 + id * 5));
        protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 620 : availableSize.Width, DocumentHeight);
        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, DocumentHeight));
            for (var i = 0; i < Paragraphs; i++)
            {
                var y = i * 100d;
                var title = i == Paragraphs - 1 ? "END-024 / unique final paragraph" : $"PARA-{i:D3} / controlled fixture {i * 37 + 19:D4}";
                var text = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 17, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(88 + i % 3 * 9, y + 12));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(215 - i * 3), (byte)(225 - i * 2), 240)), null,
                    new Rect(95, y + 45, 150 + i * 11, 8 + i % 5 * 3));
                dc.DrawLine(new Pen(Brushes.SlateGray, 1), new Point(110, y + 85), new Point(width - 90 - i % 4 * 20, y + 85));
                dc.DrawRectangle(new SolidColorBrush(MarkerColor(i)), null, new Rect(width - 80, y + 70, 50, 8));
                // 编号色条与不等宽结构提供非重复证据；左侧 80 DIP 始终留白。
            }
        }
    }

    private sealed class ControlledSource(IntPtr root, RectI region, ScrollViewer viewer, string output, Func<int> ticks) : IScrollCaptureSource
    {
        private readonly int _x = region.Left + region.Width / 2;
        private readonly int _y = region.Top + region.Height / 2;
        private string? _lostReason;
        private int _frame;
        public ConcurrentQueue<object> Events { get; } = new();
        public RectI Region => region;
        public int WheelCalls { get; private set; }
        public int DeliveredWheels { get; private set; }
        public string? LostReason => Volatile.Read(ref _lostReason);
        public bool IsCancelRequested => ScrollInjector.IsEscapePressed();
        public bool IsTargetValid
        {
            get
            {
                if (LostReason is not null) return false;
                var cursor = ScreenCapture.GetCursorPosition();
                string? failure = cursor.X != _x || cursor.Y != _y ? "用户移动光标，已停止且不恢复光标" : null;
                if (failure is null && !ScrollInjector.AreInputButtonsReleased()) failure = "用户按下修饰键或鼠标键";
                if (failure is null && !ScrollOcclusionGuard.IsRegionUnobscured(root, region)) failure = "正文区域被其他窗口遮挡";
                foreach (var p in new[] { (_x, _y), (region.Left + 2, region.Top + 2), (region.Right - 3, region.Top + 2),
                    (region.Left + 2, region.Bottom - 3), (region.Right - 3, region.Bottom - 3) })
                    if (failure is null && !ScrollInjector.IsTargetReady(root, p.Item1, p.Item2)) failure = "目标失去前台/失效/正文被遮挡";
                if (failure is not null) Interlocked.CompareExchange(ref _lostReason, failure, null);
                return LostReason is null;
            }
        }
        public async Task<bool> ScrollAsync(int wheelDelta, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsTargetValid || IsCancelRequested || Native.GetForegroundWindow() != root) return false;
            var before = await viewer.Dispatcher.InvokeAsync(() => viewer.VerticalOffset);
            if (!IsTargetValid || IsCancelRequested || Native.GetForegroundWindow() != root) return false;
            WheelCalls++;
            var sent = ScrollInjector.TrySendWheel(root, _x, _y, wheelDelta);
            if (sent) DeliveredWheels++;
            Events.Enqueue(new { kind = "wheel", wheelDelta, sent, offsetBefore = before, ticks = ticks() });
            await Task.Yield();
            return sent;
        }
        public async Task<ScrollCaptureFrame> CaptureAsync(CancellationToken token)
        {
            var previous = await Grab(token);
            var stable = 0;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                await DelayAsync(TimeSpan.FromMilliseconds(80), token);
                var next = await Grab(token);
                stable = previous.Pixels.AsSpan().SequenceEqual(next.Pixels) ? stable + 1 : 0;
                previous = next;
                if (stable >= 3) break;
            }
            var frame = previous with { Stable = stable >= 3 };
            var offset = await viewer.Dispatcher.InvokeAsync(() => viewer.VerticalOffset);
            var index = Interlocked.Increment(ref _frame);
            Save(ImageCodec.FromBgraBytes(frame.Pixels, frame.Width, frame.Height), Path.Combine(output, $"frame-{index:D3}.png"));
            Events.Enqueue(new { kind = "frame", index, offset, frame.Stable, ticks = ticks() });
            return frame;
        }
        private Task<ScrollCaptureFrame> Grab(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsTargetValid) throw new InvalidOperationException(LostReason);
            var image = ScreenCapture.CaptureRegion(region) ?? throw new InvalidOperationException("ScreenCapture failed");
            if (!IsTargetValid) throw new InvalidOperationException(LostReason);
            var bytes = ImageCodec.ToBgraBytes(image, out var stride, out var width, out var height);
            return new ScrollCaptureFrame(bytes, width, height, stride, false);
        }, token);
        public async Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < delay)
            {
                token.ThrowIfCancellationRequested();
                if (!IsTargetValid || IsCancelRequested) return;
                var left = delay - watch.Elapsed;
                if (left > TimeSpan.Zero) await Task.Delay(left < TimeSpan.FromMilliseconds(20) ? left : TimeSpan.FromMilliseconds(20), token);
            }
        }
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        internal static object DpiDiagnostic(IntPtr root)
        {
            var context = GetThreadDpiAwarenessContext();
            return new { threadId = GetCurrentThreadId(), context = context.ToInt64(), awareness = GetAwarenessFromDpiAwarenessContext(context),
                dpiForWindow = GetDpiForWindow(root), rootRect = WindowRectOf(root), virtualX = GetSystemMetrics(76), virtualY = GetSystemMetrics(77),
                virtualWidth = GetSystemMetrics(78), virtualHeight = GetSystemMetrics(79) };
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out WinRect rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder info, uint length, out uint needed);
        [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }
        internal static object WindowRectOf(IntPtr hwnd)
        {
            var ok = GetWindowRect(hwnd, out var r);
            var error = Marshal.GetLastWin32Error();
            return new { ok, error, r.Left, r.Top, r.Right, r.Bottom };
        }
        private static string DesktopName(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return "<null>";
            var name = new System.Text.StringBuilder(256);
            return GetUserObjectInformation(handle, 2, name, 512, out _) ? name.ToString() : $"<error:{Marshal.GetLastWin32Error()}>";
        }
        internal static object DesktopDiagnostic()
        {
            var thread = GetThreadDesktop(GetCurrentThreadId());
            var input = OpenInputDesktop(0, false, 1);
            var error = Marshal.GetLastWin32Error();
            try { return new { threadName = DesktopName(thread), inputName = DesktopName(input), inputOpened = input != IntPtr.Zero, inputError = error }; }
            finally { if (input != IntPtr.Zero) CloseDesktop(input); }
        }
    }
}
