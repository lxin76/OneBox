using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;
using OneBox.Plugin.Sdk.Wpf;
using OneBox.Plugins.Recorder.Core;
using OneBox.Plugins.Recorder.Services;
using OneBox.Plugins.Recorder.Windows;

namespace OneBox.Plugins.Recorder;

/// <summary>
/// 录屏小工具主体：全局热键开始/停止，托盘菜单可区域录制、暂停、打开录制目录。
/// 录制本身交给 <see cref="RecorderService"/>，这里只负责状态机、窗口与宿主通信，
/// 保证任何时刻只有一路录制、任何退出路径都会先让文件落盘。
/// </summary>
public sealed class RecorderPlugin : IAsyncDisposable
{
    private const string HotkeyToggle = "toggle";

    private const string MenuToggle = "menu.toggle";
    private const string MenuRegion = "menu.region";
    private const string MenuPause = "menu.pause";
    private const string MenuOpenFolder = "menu.openFolder";

    private readonly PluginContext _context;
    private readonly Dispatcher _dispatcher;
    private readonly RecorderService _service = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _recordWatch = new();

    private RecorderSettings _settings = new();
    private RecordingBarWindow? _bar;
    private SaveRecordingWindow? _saveWindow;
    private CompressionWindow? _compressionWindow;
    private RegionSelectorWindow? _regionSelector;
    private Task? _compressionTask;
    private string? _currentFile;

    private int _lastWidth;
    private int _lastHeight;
    private int _lastFps;
    private int _lastBitrate;
    private bool _lastHasAudio;

    private bool _active;
    private bool _paused;
    private bool _finishing;
    private bool _busy;
    private bool _saving;
    private bool _disposed;

    private IReadOnlyList<HotkeyRegistrationResult> _hotkeyResults = [];

    public RecorderPlugin(PluginContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += (_, _) =>
        {
            if (_active) _bar?.UpdateElapsed(_recordWatch.Elapsed);
        };
    }

    /// <summary>启动插件：载入设置，向宿主声明热键/托盘菜单/设置项。</summary>
    public async Task StartAsync()
    {
        AppPaths.Initialize(_context.DataDirectory);
        _settings = SettingsStore.Load();

        // 保存/压缩窗口是常规窗口，跟随宿主主题；悬浮条与选区遮罩仍固定深色
        PluginTheme.Attach(_context);

        AppLog.Write($"录屏插件启动（数据目录 {AppPaths.DataDirectory}）");

        _context.HotkeyPressed += OnHotkeyPressed;
        _context.MenuInvoked += OnMenuInvoked;
        _context.SettingsChanged += OnSettingsChanged;
        _context.HotkeysChanged += OnHotkeysChanged;
        _context.ShutdownRequested += OnShutdownRequested;

        await DeclareHotkeysAsync().ConfigureAwait(true);
        await RefreshTrayMenuAsync().ConfigureAwait(true);
        await RefreshSettingsAsync().ConfigureAwait(true);

        await _context.ReadyAsync("录屏", BuildStatusText()).ConfigureAwait(true);
        await _context.LogInfoAsync($"录屏插件已就绪（保存目录 {_settings.ResolveSaveDirectory()}）")
            .ConfigureAwait(true);
    }

    // ---- 热键 / 菜单 / 设置声明 ----

    private async Task DeclareHotkeysAsync()
    {
        _hotkeyResults = await _context.DeclareHotkeysAsync(
        [
            new HotkeyDeclaration
            {
                ActionId = HotkeyToggle,
                Combination = "Ctrl+Alt+R",
                Description = "录屏：开始/停止录制",
            },
        ]).ConfigureAwait(true);

        ReportHotkeyFailures();
    }

    private void ReportHotkeyFailures()
    {
        var failed = _hotkeyResults.Where(r => !r.Registered).ToList();
        if (failed.Count == 0) return;

        var text = string.Join("；", failed.Select(r =>
            string.IsNullOrEmpty(r.Error) ? $"{r.Combination} 未生效" : $"{r.Combination}：{r.Error}"));
        _ = _context.LogWarningAsync($"热键注册失败：{text}");
    }

    private Task RefreshTrayMenuAsync() => _context.DeclareTrayMenuAsync(
    [
        new TrayMenuItem
        {
            Id = MenuToggle,
            Text = _active ? "停止录制" : "开始录制",
            Enabled = !_saving,
        },
        new TrayMenuItem
        {
            Id = MenuRegion,
            Text = "区域录制…",
            Enabled = !_active && !_busy && !_saving,
        },
        new TrayMenuItem
        {
            Id = MenuPause,
            Text = _paused ? "继续录制" : "暂停录制",
            Enabled = _active && !_finishing,
        },
        new TrayMenuItem { Id = "sep", Kind = TrayMenuItemKind.Separator },
        new TrayMenuItem { Id = MenuOpenFolder, Text = "打开录制目录" },
    ]);

    private Task RefreshSettingsAsync() => _context.DeclareSettingsAsync(RecorderSettingsSchema.Describe(_settings));

    private async Task RefreshSettingsSafeAsync()
    {
        try
        {
            await RefreshSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"刷新设置声明失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 刷新托盘菜单/设置声明的"安全版"：宿主断开时只记日志，
    /// 不让后台任务的异常变成未观察异常。
    /// </summary>
    private async Task RefreshTrayMenuSafeAsync()
    {
        try
        {
            await RefreshTrayMenuAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"刷新托盘菜单失败: {ex.Message}");
        }
    }

    private void OnSettingsChanged(SettingsChangedPayload payload)
    {
        if (!RecorderSettingsSchema.ApplyAll(_settings, payload.Changes)) return;

        SettingsStore.Save(_settings);
        AppLog.Write($"设置中心更新了 {payload.Changes.Count} 项设置");

        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            // 重新声明一次以回显夹取后的值（例如帧率被夹到范围内）
            _ = RefreshSettingsSafeAsync();
            _ = RefreshTrayMenuSafeAsync();
        }));
    }

    private void OnHotkeysChanged(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        _hotkeyResults = results;
        ReportHotkeyFailures();
        _ = _context.SetStatusAsync(BuildStatusText());
    }

    private string BuildStatusText()
    {
        if (_saving) return "正在保存…";
        if (_active) return _paused ? "已暂停" : "录制中";

        var failed = _hotkeyResults.Count(r => !r.Registered);
        return failed == 0 ? "就绪" : $"就绪 · {failed} 个热键被占用";
    }

    // ---- 宿主事件 ----

    /// <summary>宿主退出时把还开着的选择/压缩窗口收掉，避免进程被卡在模态循环里。</summary>
    private void OnShutdownRequested(string reason)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _regionSelector?.CloseSilently();
            _saveWindow?.CloseSilently();
            _compressionWindow?.RequestCancel();
        }));
    }

    private void OnHotkeyPressed(string actionId)
    {
        if (actionId != HotkeyToggle) return;
        _dispatcher.BeginInvoke(new Action(() => _ = ToggleRecordingAsync()));
    }

    private void OnMenuInvoked(MenuInvokedPayload invoked)
    {
        switch (invoked.ItemId)
        {
            case MenuToggle:
                _dispatcher.BeginInvoke(new Action(() => _ = ToggleRecordingAsync()));
                break;

            case MenuRegion:
                _dispatcher.BeginInvoke(new Action(() => _ = StartRegionRecordingAsync()));
                break;

            case MenuPause:
                _dispatcher.BeginInvoke(new Action(TogglePause));
                break;

            case MenuOpenFolder:
                _dispatcher.BeginInvoke(new Action(OpenRecordingsFolder));
                break;
        }
    }

    // ---- 录制流程 ----

    private Task ToggleRecordingAsync()
        => _active ? StopRecordingAsync() : StartRecordingAsync(null);

    private async Task StartRegionRecordingAsync()
    {
        if (_busy || _saving) return;
        if (_active)
        {
            await _context.NotifyAsync("正在录制", "请先停止当前录制，再选择区域。").ConfigureAwait(true);
            return;
        }

        _busy = true;
        System.Drawing.Rectangle? region = null;
        try
        {
            var selector = new RegionSelectorWindow();
            _regionSelector = selector;
            selector.ShowDialog();
            region = selector.ResultRegion;
            _regionSelector = null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"区域选择失败: {ex.Message}");
            await _context.LogErrorAsync($"区域选择失败：{ex.Message}").ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }

        if (region is { } selected) await StartRecordingAsync(selected).ConfigureAwait(true);
        else await RefreshTrayMenuSafeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 开始录制。<paramref name="region"/> 为 null 时录整块屏幕（鼠标所在屏或主屏，看设置）。
    /// </summary>
    private async Task StartRecordingAsync(System.Drawing.Rectangle? region)
    {
        if (_busy || _active || _saving) return;
        _busy = true;

        RecordingBarWindow? bar = null;
        try
        {
            var monitors = ScreenGeometry.GetMonitors();
            MonitorInfo? monitor;
            System.Drawing.Rectangle? localRect = null;

            if (region is { } selectedRegion)
            {
                if (!MonitorLayout.TryLocalizeRegion(ToMonitorBounds(monitors), selectedRegion, out var bounds, out var local))
                {
                    await _context.NotifyAsync("无法开始录制", "选区太小或不在任何屏幕内，请重新选择。").ConfigureAwait(true);
                    return;
                }

                monitor = FindMonitor(monitors, bounds);
                localRect = local;
            }
            else
            {
                monitor = _settings.FullScreenTarget == MonitorTarget.Primary
                    ? ScreenGeometry.GetPrimaryMonitor(monitors)
                    : ScreenGeometry.GetMonitorAtCursor(monitors);
            }

            if (monitor is null)
            {
                await _context.NotifyAsync("无法开始录制", "没有找到可用的显示器。").ConfigureAwait(true);
                return;
            }

            var deviceName = ResolveDisplayDevice(monitor);
            if (deviceName is null)
            {
                await _context.NotifyAsync("无法开始录制", "录屏组件没有找到对应的显示器。").ConfigureAwait(true);
                return;
            }

            var sourceWidth = localRect?.Width ?? monitor.Bounds.Width;
            var sourceHeight = localRect?.Height ?? monitor.Bounds.Height;
            var profile = RecordingQualityProfile.For(_settings.Quality);
            var (outputWidth, outputHeight) = MonitorLayout.FitOutputSize(sourceWidth, sourceHeight, profile.MaxWidth);
            var videoBitrate = profile.ResolveBitrate(outputWidth, outputHeight, _settings.Fps);

            var directory = _settings.ResolveSaveDirectory();
            Directory.CreateDirectory(directory);
            var baseName = RecordingFileName.BuildBaseName(DateTime.Now);
            var fileName = RecordingFileName.EnsureUnique(directory, baseName, File.Exists);
            var path = Path.Combine(directory, fileName);

            if (_settings.ShowControlBar)
            {
                bar = new RecordingBarWindow();
                bar.StopRequested += () => _ = StopRecordingAsync();
                bar.PauseRequested += TogglePause;
                bar.Show();
                bar.UpdateLayout();
                ScreenGeometry.PositionBottomCenter(bar, monitor);
                // 录制器初始化要几百毫秒；这期间先禁用控制条，避免用户点"停止"
                // 却因为录制还没真正开始而被忽略。
                bar.IsEnabled = false;
            }

            var request = new RecordingStartRequest
            {
                OutputPath = path,
                DisplayDeviceName = deviceName,
                SourceRect = localRect,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Fps = _settings.Fps,
                VideoBitrate = videoBitrate,
                RecordSystemAudio = _settings.RecordSystemAudio,
                RecordMicrophone = _settings.RecordMicrophone,
                RecordCursor = _settings.RecordCursor,
                ExcludeWindow = bar?.Handle ?? IntPtr.Zero,
            };

            if (!_service.Start(request, out var error))
            {
                bar?.CloseSilently();
                bar = null;
                TryDeleteEmptyFile(path);
                AppLog.Write($"开始录制失败: {error}");
                await _context.NotifyAsync("无法开始录制", error ?? "录制器初始化失败。").ConfigureAwait(true);
                return;
            }

            _active = true;
            _paused = false;
            _finishing = false;
            _currentFile = path;
            _bar = bar;
            _lastWidth = outputWidth;
            _lastHeight = outputHeight;
            _lastFps = _settings.Fps;
            _lastBitrate = videoBitrate;
            _lastHasAudio = _service.HasAudio;

            _recordWatch.Reset();
            _recordWatch.Start();
            _timer.Start();
            if (bar is not null)
            {
                bar.IsEnabled = true;
                bar.UpdateElapsed(TimeSpan.Zero);
            }

            AppLog.Write($"开始录制：{path}（{sourceWidth}×{sourceHeight} → {outputWidth}×{outputHeight}，"
                         + $"{_settings.Fps}fps，{videoBitrate / 1000}kbps，画质 {RecordingQualityProfile.Label(_settings.Quality)}，"
                         + $"屏幕 {monitor.DeviceName}，"
                         + $"区域 {(localRect is { } r ? $"{r.Left},{r.Top} {r.Width}×{r.Height}" : "整屏")}）");

            if (_service.Warning is { } warning)
            {
                AppLog.Write($"录制提示: {warning}");
                await _context.LogWarningAsync(warning).ConfigureAwait(true);
            }

            // 录制已经真正开始：之后即使与宿主通信失败，也只能记日志，
            // 不能把这里的异常当成"启动失败"去关控制条——录制其实还在跑。
            try
            {
                await RefreshTrayMenuAsync().ConfigureAwait(true);
                await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"录制已开始，但刷新宿主状态失败: {ex.Message}");
            }

            _ = WatchCompletionAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"开始录制异常: {ex}");
            bar?.CloseSilently();
            await _context.LogErrorAsync($"开始录制失败：{ex.Message}").ConfigureAwait(true);
            await _context.NotifyAsync("无法开始录制", ex.Message).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }
    }

    private Task StopRecordingAsync()
    {
        if (!_active || _finishing) return Task.CompletedTask;

        _finishing = true;
        _recordWatch.Stop();
        _bar?.SetFinishing();
        _ = RefreshTrayMenuSafeAsync();
        _ = _context.SetStatusAsync("正在保存…");

        AppLog.Write("请求停止录制");
        _service.Stop();
        _ = StopTimeoutAsync();
        return Task.CompletedTask;
    }

    /// <summary>底层迟迟不回报结束时的兜底，避免界面永远停在「正在保存」。</summary>
    private async Task StopTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(25)).ConfigureAwait(true);
        if (!_active || !_finishing) return;

        _active = false;
        _finishing = false;
        _timer.Stop();
        _bar?.CloseSilently();
        _bar = null;

        AppLog.Write("停止录制超时，未收到录制完成回调");
        await _context.LogErrorAsync("停止录制超时：未收到文件写入完成的回调。").ConfigureAwait(true);
        await _context.NotifyAsync("录屏未正常结束",
            "等待文件写入超时，请检查录制目录里的文件是否能播放。", LogLevel.Warning).ConfigureAwait(true);
        await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(true);
        await RefreshTrayMenuSafeAsync().ConfigureAwait(true);
    }

    private async Task WatchCompletionAsync()
    {
        RecordingOutcome outcome;
        try
        {
            outcome = await _service.Completion.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"等待录制结束失败: {ex.Message}");
            outcome = new RecordingOutcome(false, _currentFile, ex.Message);
        }

        if (_disposed) return;
        await OnRecordingFinishedAsync(outcome).ConfigureAwait(true);
    }

    private async Task OnRecordingFinishedAsync(RecordingOutcome outcome)
    {
        if (!_active) return;

        _active = false;
        _paused = false;
        _finishing = false;
        _timer.Stop();
        _recordWatch.Stop();

        var elapsed = _recordWatch.Elapsed;
        var path = string.IsNullOrWhiteSpace(outcome.FilePath) ? _currentFile : outcome.FilePath;
        var bar = _bar;
        _bar = null;
        bar?.CloseSilently();

        if (outcome.Success && path is not null && File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            AppLog.Write($"录制完成：{path}（{size / 1024.0 / 1024.0:F2} MB，{FormatDuration(elapsed)}）");

            // 收尾期间禁止开新录制，等用户选完保存方式或压缩完成
            _saving = true;
            await _context.SetStatusAsync("正在保存…").ConfigureAwait(true);
            await RefreshTrayMenuSafeAsync().ConfigureAwait(true);

            try
            {
                var targets = RecordingSavePlan.CompressionTargets(_lastWidth, _lastHeight, _lastFps);
                if (_settings.AskOnSave && targets.Count > 0)
                    await PromptSaveAsync(path, elapsed, targets).ConfigureAwait(true);
                else
                    await PublishSavedAsync(path, elapsed, null, keptOriginal: false).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"保存流程异常: {ex}");
                await _context.LogErrorAsync($"保存失败：{ex.Message}").ConfigureAwait(true);
                await _context.NotifyAsync("录屏已生成，但保存流程未完成",
                    $"{ex.Message}\n原始文件：{path}", LogLevel.Warning).ConfigureAwait(true);
                await _context.SetStatusAsync($"就绪 · 原始录制 {Path.GetFileName(path)}").ConfigureAwait(true);
            }
            finally
            {
                _saving = false;
                await RefreshTrayMenuSafeAsync().ConfigureAwait(true);
            }
        }
        else
        {
            var error = string.IsNullOrWhiteSpace(outcome.Error) ? "录制文件未生成。" : outcome.Error;
            AppLog.Write($"录制失败：{error}");
            await _context.LogErrorAsync($"录制失败：{error}").ConfigureAwait(true);
            await _context.NotifyAsync("录屏失败", error, LogLevel.Error).ConfigureAwait(true);
            await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(true);
            await RefreshTrayMenuSafeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>停止后弹出保存选择：保持原画质，或按更小的档位压缩。</summary>
    private async Task PromptSaveAsync(string path, TimeSpan elapsed, IReadOnlyList<CompressionTarget> targets)
    {
        var info = new SaveRecordingInfo(
            path,
            RecordingQualityProfile.Label(_settings.Quality),
            elapsed,
            new FileInfo(path).Length,
            _lastWidth,
            _lastHeight,
            _lastBitrate,
            _lastHasAudio,
            targets);

        var window = new SaveRecordingWindow(info);
        PluginTheme.ApplyToWindow(window);
        _saveWindow = window;
        var result = window.ShowDialog();
        _saveWindow = null;

        if (result != true
            || window.Choice is not { } choice
            || choice.Mode != RecordingSaveMode.Compress
            || choice.Target is not { } target)
        {
            // 暂不压缩、直接关窗、没有可用档位：都保留原始录制
            await PublishSavedAsync(path, elapsed, null, keptOriginal: false).ConfigureAwait(true);
            return;
        }

        _compressionTask = CompressAndSaveAsync(path, elapsed, target, choice.KeepOriginalFile);
        await _compressionTask.ConfigureAwait(true);
    }

    private async Task CompressAndSaveAsync(
        string sourcePath, TimeSpan elapsed, CompressionTarget target, bool keepOriginal)
    {
        var outputPath = RecordingSavePlan.BuildCompressedFileName(sourcePath, target.Width);
        var window = new CompressionWindow(target);
        PluginTheme.ApplyToWindow(window);
        _compressionWindow = window;
        window.Show();

        try
        {
            var actualPath = await RecordingCompressor.CompressAsync(
                new CompressionRequest(sourcePath, outputPath, target.Width, target.Height, _lastFps, target.Bitrate),
                window.ProgressReporter,
                window.Token).ConfigureAwait(true);

            if (!keepOriginal) TryDeleteFile(sourcePath);
            await PublishSavedAsync(actualPath, elapsed, target, keepOriginal).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("用户取消压缩，保留原始录制");
            await _context.NotifyAsync("已取消压缩", "原始录制已保留。").ConfigureAwait(true);
            await _context.SetStatusAsync($"就绪 · 原始录制 {Path.GetFileName(sourcePath)}").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Write($"压缩失败: {ex}");
            await _context.LogErrorAsync($"压缩失败：{ex.Message}").ConfigureAwait(true);
            await _context.NotifyAsync("压缩失败",
                $"{ex.Message}\n原始录制已保留：{Path.GetFileName(sourcePath)}", LogLevel.Error).ConfigureAwait(true);
            await _context.SetStatusAsync($"就绪 · 原始录制 {Path.GetFileName(sourcePath)}").ConfigureAwait(true);
        }
        finally
        {
            window.CloseSilently();
            _compressionWindow = null;
            _compressionTask = null;
        }
    }

    private async Task PublishSavedAsync(string path, TimeSpan elapsed, CompressionTarget? target, bool keptOriginal)
    {
        var size = new FileInfo(path).Length;
        var width = target?.Width ?? _lastWidth;
        var height = target?.Height ?? _lastHeight;
        var title = target is null ? "录屏已保存" : "录屏已压缩保存";
        var keptNote = keptOriginal && target is not null ? "\n原始文件已保留。" : "";

        AppLog.Write($"{(target is null ? "保存" : "压缩保存")}完成：{path}"
                     + $"（{size / 1024.0 / 1024.0:F2} MB，{FormatDuration(elapsed)}，{width}×{height}）");

        await _context.SetStatusAsync($"就绪 · 已保存 {Path.GetFileName(path)}").ConfigureAwait(true);
        await _context.NotifyAsync(title,
            $"{Path.GetFileName(path)}\n{size / 1024.0 / 1024.0:F1} MB · {FormatDuration(elapsed)} · {width}×{height}{keptNote}")
            .ConfigureAwait(true);

        if (_settings.OpenFolderAfterStop) LocateInExplorer(path);
    }

    private void TogglePause()
    {
        if (!_active || _finishing) return;

        if (_paused)
        {
            _service.Resume();
            _paused = false;
            _recordWatch.Start();
        }
        else
        {
            _service.Pause();
            _paused = true;
            _recordWatch.Stop();
        }

        _bar?.SetPaused(_paused);
        _bar?.UpdateElapsed(_recordWatch.Elapsed);
        _ = RefreshTrayMenuSafeAsync();
        _ = _context.SetStatusAsync(BuildStatusText());
        AppLog.Write(_paused ? "已暂停录制" : "已继续录制");
    }

    // ---- 路径与坐标 ----

    private static IReadOnlyList<MonitorBounds> ToMonitorBounds(IReadOnlyList<MonitorInfo> monitors)
        => monitors
            .Select(m => new MonitorBounds(m.Index, m.DeviceName, m.Bounds, m.WorkArea, m.IsPrimary))
            .ToList();

    private static MonitorInfo? FindMonitor(IReadOnlyList<MonitorInfo> monitors, MonitorBounds bounds)
        => monitors.FirstOrDefault(m => m.Index == bounds.Index)
           ?? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, bounds.DeviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 录屏库的显示器标识也是 \\.\DISPLAY1 这种，正常情况下与 Win32 一致；
    /// 对不上时按枚举位置兜底，避免多屏顺序差异导致录错屏幕。
    /// </summary>
    private static string? ResolveDisplayDevice(MonitorInfo monitor)
    {
        try
        {
            var displays = ScreenRecorderLib.Recorder.GetDisplays();
            var exact = displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.DeviceName;

            if (monitor.Index >= 0 && monitor.Index < displays.Count) return displays[monitor.Index].DeviceName;
            return displays.FirstOrDefault()?.DeviceName;
        }
        catch (Exception ex)
        {
            AppLog.Write($"枚举显示器失败: {ex.Message}");
            return null;
        }
    }

    private void OpenRecordingsFolder()
    {
        try
        {
            var directory = _settings.ResolveSaveDirectory();
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write($"打开录制目录失败: {ex.Message}");
            _ = _context.LogErrorAsync($"打开录制目录失败：{ex.Message}");
        }
    }

    /// <summary>初始化失败时可能留下一个 0 字节的占位文件，顺手删掉，别让用户以为是坏录像。</summary>
    private static void TryDeleteEmptyFile(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path);
        }
        catch
        {
            // 删除失败不影响主流程
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Write($"删除原始录制失败: {ex.Message}");
        }
    }

    private static void LocateInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 定位失败不影响录制结果
        }
    }

    private static string FormatDuration(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    // ---- 生命周期 ----

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();

        // 若正在压缩，先请求取消并给它一点时间删掉半成品
        _compressionWindow?.RequestCancel();
        if (_compressionTask is { } compression)
        {
            try
            {
                await Task.WhenAny(compression, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
            }
            catch
            {
                // 取消过程中的异常忽略
            }
        }

        // 插件被禁用/宿主退出时，先把正在录的文件收尾，避免留下坏文件
        if (_active)
        {
            _finishing = true;
            _bar?.SetFinishing();
            try
            {
                _service.Stop();
                await _service.Completion.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
            }
            catch
            {
                // 超时或底层异常：不再等待，交给宿主结束进程
            }
            _active = false;
        }

        _regionSelector?.CloseSilently();
        _saveWindow?.CloseSilently();
        _compressionWindow?.RequestCancel();
        _bar?.CloseSilently();
        _bar = null;
        _saveWindow = null;
        _compressionWindow = null;
        _regionSelector = null;
        _service.Dispose();

        SettingsStore.Save(_settings);
        await _context.DisposeAsync().ConfigureAwait(true);
    }
}
