using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;
using OneBox.Plugin.Sdk.Wpf;
using OneBox.Plugins.Screenshot.Core;
using OneBox.Plugins.Screenshot.Services;
using OneBox.Plugins.Screenshot.Windows;

namespace OneBox.Plugins.Screenshot;

/// <summary>
/// 截图小工具插件主体：全屏 / 区域 / 滚动截长图、贴图置顶、保存与文本识别。
/// 全局热键、托盘菜单、日志与设置目录全部由宿主提供（见 <see cref="PluginContext"/>）。
/// </summary>
public sealed class ScreenshotPlugin : IAsyncDisposable
{
    // 热键动作标识（与宿主转发回来的 actionId 对应）
    private const string HotkeyRegion = "region";
    private const string HotkeyFull = "full";
    private const string HotkeyScroll = "scroll";
    private const string HotkeyOcr = "ocr";

    // 托盘菜单项标识
    private const string MenuRegion = "menu.region";
    private const string MenuFull = "menu.full";
    private const string MenuScroll = "menu.scroll";
    private const string MenuOcrClipboard = "menu.ocrClipboard";
    private const string MenuClosePins = "menu.closePins";
    private const string MenuTogglePins = "menu.togglePins";
    private const string MenuSettings = "menu.settings";

    private readonly PluginContext _context;
    private readonly Dispatcher _dispatcher;

    /// <summary>已创建的贴图窗口。</summary>
    private readonly List<PinWindow> _pins = [];

    private ScreenshotSettingsWindow? _settingsWindow;

    /// <summary>同一时刻只允许一个截图流程（区域选择 / 滚动截图）。</summary>
    private bool _captureInProgress;

    // 仅恢复本次截图前可见的窗口，不把用户原本隐藏的贴图重新显示。
    private readonly List<PinWindow> _captureHiddenPins = [];
    private ScreenshotSettingsWindow? _captureHiddenSettings;

    private bool _disposed;

    public ScreenshotPlugin(PluginContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    /// <summary>当前设置（改动即保存）。</summary>
    public ScreenshotSettings Settings { get; private set; } = new();

    /// <summary>宿主是否已注册某热键（供设置页提示）。</summary>
    public IReadOnlyList<HotkeyRegistrationResult> HotkeyResults { get; private set; } = [];

    /// <summary>启动插件：载入设置、创建常驻窗口、向宿主声明热键与托盘菜单。</summary>
    public async Task StartAsync()
    {
        // 跟随宿主主题：设置窗与识别结果窗引用主题键，这里把调色板刷进应用资源
        PluginTheme.Attach(_context);

        AppPaths.Initialize(_context.DataDirectory);
        Settings = SettingsStore.Load();

        // 宿主未连接时的本地日志兜底
        AppLog.Write($"截图插件启动（数据目录 {AppPaths.DataDirectory}）");

        _context.HotkeyPressed += OnHotkeyPressed;
        _context.MenuInvoked += OnMenuInvoked;
        _context.SettingsChanged += OnSettingsChanged;
        // 用户在工具箱改键后，宿主会推最新注册结果；状态栏与设置页的占用提示要跟着刷新
        _context.HotkeysChanged += OnHotkeysChanged;

        // 先声明热键与菜单，宿主注册完成后回报结果
        await DeclareHotkeysAsync().ConfigureAwait(false);
        await RefreshTrayMenuAsync().ConfigureAwait(false);
        await RefreshSettingsAsync().ConfigureAwait(false);

        await _context.ReadyAsync("截图", BuildStatusText()).ConfigureAwait(false);
        await _context.LogInfoAsync($"截图插件已就绪（保存目录 {Settings.ResolveSaveDirectory()}）")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 宿主重新注册了热键（用户改键或注册状态变化）。
    ///
    /// 更新 <see cref="HotkeyResults"/> 并刷新状态栏与设置页提示：
    /// 否则用户在工具箱里改完键，截图插件仍显示"1 个热键被占用"这类过期信息。
    /// </summary>
    private void OnHotkeysChanged(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        HotkeyResults = results;
        _ = _context.SetStatusAsync(BuildStatusText());
        ReportHotkeyFailures();
        _settingsWindow?.RefreshHotkeys();
    }

    private string BuildStatusText()
    {
        var failed = HotkeyResults.Count(r => !r.Registered);
        var text = failed == 0 ? "已就绪" : $"已就绪 · {failed} 个热键被占用";
        var languages = OcrService.AvailableLanguages();
        if (languages.Count == 0) text += " · 未安装识别语言包";
        return text;
    }

    // ---- 热键与菜单声明 ----

    /// <summary>
    /// 向宿主声明热键，等待注册结果写回 <see cref="HotkeyResults"/>。
    ///
    /// 这里刻意用 async/await，而不是先前的
    /// <c>_dispatcher.InvokeAsync(...).Task.Unwrap()</c>：对 InvokeAsync 返回的
    /// DispatcherOperation 再取 <c>.Task</c> 会得到另一个 Task，
    /// Unwrap 出来的与调用方 await 的不是同一个，异常被静默丢弃，
    /// 启动阶段"这次没声明成功"就退化成"这个插件没有热键"，且不留任何日志。
    /// </summary>
    private async Task DeclareHotkeysAsync()
    {
        HotkeyResults = await _context.DeclareHotkeysAsync(
        [
            new HotkeyDeclaration
            {
                ActionId = HotkeyRegion,
                Combination = Settings.RegionHotkey,
                Description = "截图：区域截图",
            },
            new HotkeyDeclaration
            {
                ActionId = HotkeyFull,
                Combination = Settings.FullScreenHotkey,
                Description = "截图：全屏截图",
            },
            new HotkeyDeclaration
            {
                ActionId = HotkeyScroll,
                Combination = Settings.ScrollHotkey,
                Description = "截图：滚动截长图",
            },
            new HotkeyDeclaration
            {
                ActionId = HotkeyOcr,
                Combination = Settings.OcrClipboardHotkey,
                Description = "截图：识别剪贴板图片文字",
            },
        ]).ConfigureAwait(true);

        ReportHotkeyFailures();
    }

    /// <summary>把注册失败的热键显示在设置窗口里（若开着）。</summary>
    private void ReportHotkeyFailures()
    {
        var failed = HotkeyResults.Where(r => !r.Registered).ToList();
        if (failed.Count == 0)
        {
            _settingsWindow?.ShowHotkeyWarning("");
            return;
        }

        var text = string.Join("；", failed.Select(r =>
            string.IsNullOrEmpty(r.Error) ? $"{r.Combination} 未生效" : $"{r.Combination}：{r.Error}"));
        _settingsWindow?.ShowHotkeyWarning($"以下热键被占用，未生效：{text}");

        _ = _context.LogWarningAsync($"热键注册失败：{text}");
    }

    private Task RefreshTrayMenuAsync() => _context.DeclareTrayMenuAsync(
    [
        new TrayMenuItem { Id = MenuRegion, Text = "区域截图" },
        new TrayMenuItem { Id = MenuFull, Text = "全屏截图" },
        new TrayMenuItem { Id = MenuScroll, Text = "滚动截长图" },
        new TrayMenuItem { Id = MenuOcrClipboard, Text = "识别剪贴板图片文字" },
        new TrayMenuItem { Id = "sep1", Kind = TrayMenuItemKind.Separator },
        new TrayMenuItem
        {
            Id = MenuTogglePins,
            Text = "贴图置顶",
            Kind = TrayMenuItemKind.Toggle,
            Checked = Settings.PinTopmost,
        },
        new TrayMenuItem { Id = MenuClosePins, Text = "关闭全部贴图" },
        new TrayMenuItem { Id = "sep2", Kind = TrayMenuItemKind.Separator },
        new TrayMenuItem { Id = MenuSettings, Text = "截图设置…" },
    ]);

    /// <summary>把当前设置项声明给宿主，供设置中心渲染。</summary>
    private Task RefreshSettingsAsync()
        => _context.DeclareSettingsAsync(ScreenshotSettingsSchema.Describe(Settings));

    /// <summary>
    /// 用户在宿主设置中心改了设置。插件是设置值的唯一来源，
    /// 因此这里自行应用并持久化，然后重新声明一次以刷新界面上的值
    /// （例如数字被夹取后要把夹取结果回显）。
    /// </summary>
    private void OnSettingsChanged(SettingsChangedPayload payload)
    {
        if (!ScreenshotSettingsSchema.ApplyAll(Settings, payload.Changes)) return;

        SettingsStore.Save(Settings);
        AppLog.Write($"设置中心更新了 {payload.Changes.Count} 项设置");

        // 贴图置顶开关可能与贴图窗口的实际状态不一致，需要同步
        SyncPinTopmostFromSettings();

        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            _settingsWindow?.ReloadFrom(Settings);
            _ = RefreshTrayMenuAsync();
            _ = RefreshSettingsAsync();
        }));
    }

    /// <summary>
    /// 把设置里的置顶偏好应用到所有贴图窗口。
    /// 与托盘菜单里的「贴图置顶」开关行为一致：作用于全部贴图。
    /// </summary>
    private void SyncPinTopmostFromSettings()
    {
        foreach (var pin in _pins.ToArray())
        {
            if (pin.IsLoaded) pin.SetPinned(Settings.PinTopmost);
        }
    }

    private void OnHotkeyPressed(string actionId)
    {
        AppLog.Write($"收到热键：{actionId}");
        switch (actionId)
        {
            case HotkeyRegion:
                _dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync(CaptureMode.Region)));
                break;
            case HotkeyFull:
                _dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync(CaptureMode.FullScreen)));
                break;
            case HotkeyScroll:
                _dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync(CaptureMode.Scroll)));
                break;
            case HotkeyOcr:
                _dispatcher.BeginInvoke(new Action(() => _ = RecognizeClipboardAsync()));
                break;
        }
    }

    private void OnMenuInvoked(MenuInvokedPayload invoked)
    {
        switch (invoked.ItemId)
        {
            case MenuRegion:
                _dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync(CaptureMode.Region)));
                break;
            case MenuFull:
                _dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync(CaptureMode.FullScreen)));
                break;
            case MenuScroll:
                _dispatcher.BeginInvoke(new Action(() => _ = StartCaptureAsync(CaptureMode.Scroll)));
                break;
            case MenuOcrClipboard:
                _dispatcher.BeginInvoke(new Action(() => _ = RecognizeClipboardAsync()));
                break;

            case MenuTogglePins:
                Settings.PinTopmost = invoked.Checked;
                SettingsStore.Save(Settings);
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var pin in _pins.ToList()) pin.SetPinned(Settings.PinTopmost);
                }));
                break;

            case MenuClosePins:
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var pin in _pins.ToList()) pin.Close();
                }));
                break;

            case MenuSettings:
                _dispatcher.BeginInvoke(new Action(ShowSettingsWindow));
                break;
        }
    }

    // ---- 截图入口 ----

    private enum CaptureMode
    {
        Region,
        FullScreen,
        Scroll,
    }

    private async Task StartCaptureAsync(CaptureMode mode)
    {
        if (_captureInProgress)
        {
            AppLog.Write("已有截图流程在进行中，忽略本次请求");
            return;
        }

        _captureInProgress = true;

        // 整个流程（含用户拖选、滚动截长图）结束后才释放占用：
        // 以前在“打开选择遮罩”后就立刻释放，滚动截图跑起来的这段时间仍可被再次触发，
        // 第二次截图会插进来、连选区遮罩带控制条一起被截进长图。
        try
        {
            // 先把本插件自己的贴图与其他窗口藏起来，避免被截进画面
            await HideOwnWindowsAsync().ConfigureAwait(true);

            switch (mode)
            {
                case CaptureMode.FullScreen:
                    CaptureFullScreen();
                    break;

                case CaptureMode.Region:
                    await CaptureRegionAsync(enableScroll: false).ConfigureAwait(true);
                    break;

                case CaptureMode.Scroll:
                    await CaptureRegionAsync(enableScroll: true).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"截图失败: {ex}");
            await _context.LogErrorAsync($"截图失败：{ex.Message}").ConfigureAwait(true);
        }
        finally
        {
            RestoreOwnWindows();
            _captureInProgress = false;
        }
    }

    /// <summary>截图前隐藏本插件的贴图与设置窗口。</summary>
    private async Task HideOwnWindowsAsync()
    {
        _captureHiddenPins.Clear();
        foreach (var pin in _pins.Where(p => p.IsVisible).ToList())
        {
            _captureHiddenPins.Add(pin);
            pin.Hide();
        }
        _captureHiddenSettings = _settingsWindow is { IsVisible: true } ? _settingsWindow : null;
        _captureHiddenSettings?.Hide();

        // 等一帧让隐藏真正生效，避免刚 Hide 就抓图抓到残影
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(80).ConfigureAwait(true);
    }

    private void RestoreOwnWindows()
    {
        foreach (var pin in _captureHiddenPins.ToList())
        {
            if (_pins.Contains(pin) && !pin.IsClosed && !pin.IsVisible) pin.Show();
        }
        _captureHiddenPins.Clear();
        if (_captureHiddenSettings is { } settings && ReferenceEquals(settings, _settingsWindow)) settings.Show();
        _captureHiddenSettings = null;
    }

    private void CaptureFullScreen()
    {
        var bounds = ScreenCapture.GetVirtualScreenBounds();
        if (bounds is not { } region)
        {
            _ = _context.LogErrorAsync("无法读取屏幕尺寸。");
            RestoreOwnWindows();
            return;
        }

        var shot = ScreenCapture.CaptureRegion(region);
        RestoreOwnWindows();

        if (shot is null)
        {
            _ = _context.LogErrorAsync("全屏截图失败。");
            return;
        }

        // 全屏截图没有合适的位置可放贴图（会盖住整个屏幕），交给 PinWindow 的默认右下角
        FinishCapture(shot, "全屏截图");
    }

    /// <summary>区域选择：打开遮罩，用户确认后（或取消时）返回。滚动截长图在返回前跑完。</summary>
    private Task CaptureRegionAsync(bool enableScroll)
    {
        var selector = new RegionSelectorWindow();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        selector.Closed += (_, _) => finished.TrySetResult(true);

        selector.Show();
        selector.Activate();

        return FinishRegionSelectionAsync(selector, finished.Task, enableScroll);

        async Task FinishRegionSelectionAsync(RegionSelectorWindow window, Task closed, bool scroll)
        {
            await closed.ConfigureAwait(true);

            var region = window.ResultRegion;
            var title = window.ResultWindowTitle;
            // 滚动完成前保持原有贴图隐藏，避免污染原始帧和窗口命中。

            if (region is not { } rect)
            {
                AppLog.Write("区域选择已取消");
                return;
            }

            AppLog.Write($"区域选择完成：{rect.Left},{rect.Top} {rect.Width}×{rect.Height}"
                         + $"（窗口缩放 {window.ScaleInfo}）");

            // 遮罩已关闭，此时再取“选区中心点的窗口”才准（遮罩开着时只会取到遮罩自己）。
            var target = ResolveScrollTarget(rect, window.ResultWindowHandle);

            if (scroll) await RunScrollCaptureAsync(rect, target).ConfigureAwait(true);
            else CaptureSelectedRegion(rect, title);
        }
    }

    /// <summary>
    /// 取选区中心点所在的顶层窗口，作为滚动截图的滚轮投递目标。
    /// 遮罩关闭后调用，避免取到遮罩自身；取不到时使用选区阶段记录的句柄。
    /// </summary>
    private static IntPtr ResolveScrollTarget(System.Drawing.Rectangle region, IntPtr fallback)
    {
        try
        {
            var point = new Native.POINT
            {
                X = region.Left + region.Width / 2,
                Y = region.Top + region.Height / 2,
            };

            var hwnd = Native.WindowFromPoint(point);
            if (hwnd != IntPtr.Zero)
            {
                var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
                if (root != IntPtr.Zero) hwnd = root;

                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != (uint)Environment.ProcessId && Native.IsWindowVisible(hwnd))
                    return root != IntPtr.Zero ? hwnd : hwnd;
            }
        }
        catch
        {
            // 取不到就退回选区阶段记录的句柄
        }

        return fallback;
    }

    private void CaptureSelectedRegion(System.Drawing.Rectangle region, string? windowTitle)
    {
        var shot = ScreenCapture.CaptureRegion(region);
        if (shot is null)
        {
            _ = _context.LogErrorAsync("区域截图失败。");
            return;
        }

        var name = string.IsNullOrEmpty(windowTitle) ? "区域截图" : $"区域截图 · {windowTitle}";
        FinishCapture(shot, name!, region);
    }

    // ---- 滚动截长图 ----

    /// <summary>滚动截长图。<paramref name="targetWindow"/> 是选区所在的窗口，滚动前需要它在前台。</summary>
    private async Task RunScrollCaptureAsync(System.Drawing.Rectangle region, IntPtr targetWindow = default)
    {
        if (region.Height < 80 || region.Width < 40)
        {
            await _context.NotifyAsync("无法滚动截图", "选区过小，请选择完整正文区域，并避开固定头尾与侧栏。").ConfigureAwait(true);
            return;
        }

        var control = new ScrollControlWindow(region);
        using var session = new ScrollCaptureSession(region, Settings, targetWindow: targetWindow);
        control.FinishRequested += session.Stop;
        control.CancelRequested += session.Cancel;
        BitmapSource? result;
        try
        {
            control.Show();
            result = await session.RunAsync(p => control.Report(p, Settings.ScrollMaxHeight)).ConfigureAwait(true);
        }
        finally
        {
            control.CloseControl();
        }

        var outcome = session.Outcome;
        if (outcome?.EndReason == ScrollCaptureEndReason.Cancelled)
        {
            await _context.SetStatusAsync("滚动截图已取消，本次结果已丢弃。").ConfigureAwait(true);
            return;
        }
        if (result is null)
        {
            await _context.NotifyAsync("滚动截图未完成", outcome?.Message ?? "未取得可用内容，请重新选择正文区域。").ConfigureAwait(true);
            return;
        }

        if (outcome?.EndReason == ScrollCaptureEndReason.Incomplete)
        {
            var saveDiagnostics = MessageBox.Show("本次画面未能可靠对齐。是否将首帧及最近采样保存为本地诊断包？\n\n图像包含网页原文，可能涉及敏感信息；仅保存在本机，不自动上传。选择否不会保存。",
                "保存配准诊断包", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (saveDiagnostics == MessageBoxResult.Yes)
            {
                try
                {
                    var directory = await session.SaveFailureDiagnosticsAsync().ConfigureAwait(true);
                    MessageBox.Show(directory is null ? "可用采样不足，未生成诊断包。" : "诊断包已保存：\n" + directory + "\n\n分享前请检查敏感内容；不会自动上传。", "配准诊断包");
                    if (directory is not null) AppLog.Write("用户确认保存配准诊断包：" + directory);
                }
                catch (Exception ex) { MessageBox.Show("诊断包保存失败：" + ex.Message, "配准诊断包"); }
            }
        }

        if (outcome is not null && outcome.EndReason is not ScrollCaptureEndReason.Finished and not ScrollCaptureEndReason.NoFurtherMotion)
        {
            var preview = MessageBox.Show(outcome.Message + "\n\n是否预览已经确认拼接的部分？选择否将丢弃本次结果。",
                "滚动截图不完整", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (preview != MessageBoxResult.Yes) return;
        }
        var label = outcome?.EndReason == ScrollCaptureEndReason.Finished ? "用户完成" : "未确认完整";
        FinishCapture(result, $"滚动截长图 · {label} · {region.Width}×{result.PixelHeight}", region);
        if (outcome is { IsComplete: false })
            await _context.NotifyAsync("滚动截图结果", outcome.Message + "\n预览仅包含已确认拼接的部分；未确认整页完整。").ConfigureAwait(true);
    }

    // ---- 截图收尾：复制 / 贴图预览（不再自动落盘，保存走「另存为」） ----

    /// <summary>截图完成后统一处理：按设置复制到剪贴板，并以贴图形式预览在截图位置附近。</summary>
    private void FinishCapture(BitmapSource shot, string sourceName,
        System.Drawing.Rectangle? sourceRegion = null)
    {
        if (Settings.CopyToClipboard)
        {
            if (!ClipboardSupport.CopyImage(shot))
                _ = _context.LogWarningAsync("截图已生成，但写入剪贴板失败（可能被其他程序占用）。");
        }

        ShowPin(shot, sourceName, sourceRegion);
        _ = _context.SetStatusAsync(BuildStatusText());
    }

    /// <summary>把图片作为置顶贴图显示。<paramref name="sourceRegion"/> 是截图区域（物理像素），贴图优先出现在那里。</summary>
    private void ShowPin(BitmapSource shot, string sourceName,
        System.Drawing.Rectangle? sourceRegion = null)
    {
        PinWindow? pin = null;

        pin = new PinWindow(shot, onClosed: p =>
        {
            _pins.Remove(p);
            _ = _context.SetStatusAsync(BuildStatusText());
        });

        pin.SaveRequested += p => _ = SavePinAsync(p);
        pin.DeleteRequested += p => DeletePin(p);
        pin.OcrRequested += p => _ = RecognizeAsync(p.Source, "贴图识别", p);

        // 划选文字：OCR 只做一次并缓存在贴图窗上，之后拖动选择是纯几何计算。
        // 缓存挂在 PinWindow 自己身上，贴图关闭即随之释放。
        var cachedLayout = (OcrLayout?)null;
        pin.LayoutProvider = async window =>
        {
            if (cachedLayout is not null) return cachedLayout;

            var outcome = await OcrService.RecognizeAsync(
                window.Source, Settings.OcrLanguage, Settings.OcrStripCjkSpaces, Settings.OcrEnhanceImage)
                .ConfigureAwait(true);
            if (!outcome.Success)
            {
                AppLog.Write($"贴图划选识别失败: {outcome.Error}");
                await _context.LogWarningAsync(outcome.Error ?? "识别失败。").ConfigureAwait(true);
                return null;
            }

            cachedLayout = outcome.Layout;
            AppLog.Write($"贴图划选版面：{outcome.Layout?.Lines.Count ?? 0} 行");
            return cachedLayout;
        };
        pin.CopyTextHandler = ClipboardSupport.CopyText;

        _pins.Add(pin);
        pin.Show();
        pin.SetPinned(Settings.PinTopmost);
        if (sourceRegion is { } region) pin.MoveToRegion(region);

        AppLog.Write($"已贴图：{sourceName}（{shot.PixelWidth}×{shot.PixelHeight}，共 {_pins.Count} 张）");
    }

    /// <summary>
    /// 把贴图保存到文件。截图不再自动落盘，保存一律由用户通过「另存为」对话框
    /// 亲自选择位置与文件名，避免悄悄往默认目录堆文件。
    /// </summary>
    private async Task SavePinAsync(PinWindow pin)
    {
        try
        {
            // Win32 保存对话框不能设置 WPF Owner，用活动窗口兜底即可；
            // 对话框自身会置顶，不会落到贴图下面。
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存截图",
                FileName = FileNameBuilder.Build(Settings.FileNameTemplate, DateTime.Now),
                DefaultExt = FileNameBuilder.ExtensionFor(Settings.Format),
                Filter = Settings.Format == ShotImageFormat.Jpeg
                    ? "JPEG 图片|*.jpg;*.jpeg|PNG 图片|*.png|所有文件|*.*"
                    : "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|所有文件|*.*",
                InitialDirectory = Settings.ResolveSaveDirectory(),
            };

            var path = _dispatcher.Invoke(() => dialog.ShowDialog() == true ? dialog.FileName : null);
            if (path is null)
            {
                AppLog.Write("保存已取消");
                return;
            }

            ImageCodec.SaveTo(pin.Source, path, Settings.Format, Settings.JpegQuality);
            pin.SavedFilePath = path;
            AppLog.Write($"已保存：{path}");

            await _context.NotifyAsync("截图已保存", path).ConfigureAwait(false);
            await _context.SetStatusAsync($"已保存 {Path.GetFileName(path)}").ConfigureAwait(false);

            // 保存后直接用系统看图程序打开，方便用户查看
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Write($"打开图片失败: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"保存贴图失败: {ex.Message}");
            await _context.LogErrorAsync($"保存失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>删除贴图：关闭预览；若已保存过文件，询问后一并删除。</summary>
    private void DeletePin(PinWindow pin)
    {
        var saved = pin.SavedFilePath;
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved))
        {
            var delete = _dispatcher.Invoke(() => MessageBox.Show(
                $"要同时删除已保存的文件吗？\n{saved}",
                "删除截图", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            if (delete)
            {
                try
                {
                    File.Delete(saved);
                    AppLog.Write($"已删除文件：{saved}");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"删除文件失败: {ex.Message}");
                    _ = _context.LogErrorAsync($"删除文件失败：{ex.Message}");
                }
            }
        }

        AppLog.Write("贴图已删除（关闭预览）");
        pin.Close();
    }

    // ---- 文本识别 ----

    /// <summary>识别剪贴板里的图片。</summary>
    private async Task RecognizeClipboardAsync()
    {
        AppLog.Write("识别剪贴板图片：收到请求");
        var image = ClipboardSupport.TryGetImage();
        if (image is null)
        {
            AppLog.Write("识别剪贴板图片：剪贴板里没有图片");
            await _context.NotifyAsync("识别失败", "剪贴板里没有图片。请先复制一张图片，或用截图热键截取后再识别。")
                .ConfigureAwait(false);
            return;
        }

        AppLog.Write($"识别剪贴板图片：取到 {image.PixelWidth}×{image.PixelHeight}");
        // 剪贴板位图绑定在当前 UI 线程上，而识别过程会切到线程池，
        // 先拷一份冻结副本，否则 OCR 里访问像素会抛跨线程异常。
        await RecognizeAsync(ImageCodec.Snapshot(image), "剪贴板图片识别").ConfigureAwait(false);
    }

    /// <summary>
    /// 识别位图文字并打开结果窗口。
    /// 这里刻意全程使用 ConfigureAwait(true)：识别结束后要写剪贴板、建窗口，
    /// 两者都必须在 STA 的 UI 线程上执行（剪贴板写 OLE 会直接抛“必须先将当前线程
    /// 设置为单个线程单元(STA)模式”）。识别本身的耗时部分在 WinRT 里异步完成，不会卡住 UI。
    /// </summary>
    private async Task RecognizeAsync(BitmapSource source, string header, PinWindow? origin = null)
    {
        await _context.SetStatusAsync("正在识别文字…").ConfigureAwait(true);

        var outcome = await OcrService.RecognizeAsync(
            source, Settings.OcrLanguage, Settings.OcrStripCjkSpaces, Settings.OcrEnhanceImage).ConfigureAwait(true);

        AppLog.Write($"识别完成：成功={outcome.Success} 字符={outcome.Text.Length} 行={outcome.Layout?.Lines.Count ?? outcome.Lines.Count}" +
            $" 增强={outcome.EnhancementNote ?? "-"} 错误={outcome.Error ?? "-"}" +
            (origin is { IsClosed: true } ? "（来源贴图已关闭，结果窗口独立展示）" : ""));

        // 识别结果窗口是独立的文本展示：来源贴图即使在识别期间被关掉，
        // 结果也照常落地（写剪贴板 + 弹窗）——用户主动发起的识别不该凭空消失。
        // UI 卡顿问题由 OcrService 把重活移到线程池解决，不靠丢弃结果。

        if (!outcome.Success)
        {
            await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(true);
            await _context.NotifyAsync("识别失败", outcome.Error ?? "未知错误").ConfigureAwait(true);
            return;
        }

        if (outcome.Text.Length == 0)
        {
            await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(true);
            await _context.NotifyAsync("识别完成", "没有识别到文字。图片可能过于模糊或没有文字。")
                .ConfigureAwait(true);
            return;
        }

        // 识别结果默认复制到剪贴板，省一次手动操作
        var copied = ClipboardSupport.CopyText(outcome.Text);

        var lineCount = outcome.Layout?.Lines.Count ?? outcome.Lines.Count;
        var enhanceNote = string.IsNullOrEmpty(outcome.EnhancementNote)
            ? ""
            : $" · {outcome.EnhancementNote}";
        var window = new OcrResultWindow(
            outcome.Text,
            header,
            copied
                ? $"已复制到剪贴板 · {outcome.Text.Length} 个字符 · {lineCount} 行{enhanceNote}"
                : $"共 {outcome.Text.Length} 个字符 · {lineCount} 行{enhanceNote}（剪贴板被占用，未自动复制）")
        {
            Owner = null,
        };
        PluginTheme.ApplyToWindow(window);
        window.Show();
        window.Activate();

        await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(true);
        AppLog.Write($"识别完成：{outcome.Text.Length} 个字符{(copied ? "，已复制到剪贴板" : "")}");
    }

    // ---- 设置窗口 ----

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new ScreenshotSettingsWindow(
            Settings,
            onChanged: () => SettingsStore.Save(Settings),
            onHotkeysChanged: async () =>
            {
                await DeclareHotkeysAsync().ConfigureAwait(true);
                await RefreshTrayMenuAsync().ConfigureAwait(false);
                await _context.SetStatusAsync(BuildStatusText()).ConfigureAwait(false);
            });

        _settingsWindow.Closed += (_, _) =>
        {
            SettingsStore.Save(Settings);
            _settingsWindow = null;
        };

        PluginTheme.ApplyToWindow(_settingsWindow);
        _settingsWindow.Show();
        ReportHotkeyFailures();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            foreach (var pin in _pins.ToList()) pin.Close();
            _settingsWindow?.Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }

        SettingsStore.Save(Settings);
        await _context.DisposeAsync().ConfigureAwait(false);
    }
}