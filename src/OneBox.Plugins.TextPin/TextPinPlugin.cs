using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;
using OneBox.Plugin.Sdk.Wpf;
using OneBox.Plugins.TextPin.Core;
using OneBox.Plugins.TextPin.Services;
using OneBox.Plugins.TextPin.Windows;

namespace OneBox.Plugins.TextPin;

/// <summary>
/// 置顶文本小工具插件主体：一个常驻置顶的纯文本框 + 显式复制/粘贴 + 自动保存。
/// 全局热键、托盘菜单、日志与设置目录全部由宿主提供（见 <see cref="PluginContext"/>）。
///
/// 与旧版剪贴板插件的关键区别：**不注册任何剪贴板监听器**。
/// 只有用户点按钮或按热键时，才会读写一次剪贴板。
/// </summary>
public sealed class TextPinPlugin : IAsyncDisposable
{
    /// <summary>热键动作标识（与宿主转发回来的 actionId 对应）。</summary>
    /// <summary>呼出热键的动作标识（窗口刷新提示文本时也要用）。</summary>
    public const string HotkeyToggle = "toggle";
    private const string HotkeyPaste = "paste";
    private const string HotkeyCopy = "copy";
    private const string HotkeyCapture = "capture";
    private const string HotkeyResetKeys = "resetKeys";

    /// <summary>托盘菜单项标识。</summary>
    private const string MenuShow = "show";
    private const string MenuPaste = "paste";
    private const string MenuCopy = "copy";
    private const string MenuCapture = "capture";
    private const string MenuClear = "clear";
    private const string MenuSettings = "settings";
    private const string MenuResetKeys = "resetKeys";

    private readonly PluginContext _context;
    private readonly Dispatcher _dispatcher;

    private PinnedWindow? _window;
    private bool _disposed;

    /// <summary>最近一次已知的正文。缓存它而不是去读窗口的 TextBox：
    /// 状态构建可能在线程池线程上被调用（声明热键后用了 ConfigureAwait(false)），
    /// 那时直接读 WPF 控件属性会抛"调用线程无法访问此对象"。</summary>
    private string _currentText = "";

    public TextPinPlugin(PluginContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    /// <summary>当前设置（改动即保存）。</summary>
    public Settings Settings { get; private set; } = new();

    /// <summary>宿主是否已注册某热键（供设置页提示）。</summary>
    public IReadOnlyList<HotkeyRegistrationResult> HotkeyResults { get; private set; } = [];

    /// <summary>
    /// 某个动作当前**实际生效**的组合键。
    ///
    /// 优先取宿主回传的注册结果：用户可能在工具箱里改键，那时插件自己的
    /// <see cref="Settings"/> 仍是声明时的值，拿它做界面提示会显示旧键位。
    /// 只有宿主还没回报过结果时，才退回插件自己声明的默认键。
    /// </summary>
    public string EffectiveHotkey(string actionId, string fallback)
    {
        var result = HotkeyResults.FirstOrDefault(r =>
            string.Equals(r.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

        return result is { Combination.Length: > 0 } ? result.Combination : fallback;
    }

    /// <summary>
    /// 启动插件：恢复上次的文本、创建（但不显示）置顶窗，向宿主声明热键、菜单与设置项。
    /// 窗口只在用户按热键或点托盘菜单时才出现，启动时不打扰用户。
    /// </summary>
    public async Task StartAsync()
    {
        // 跟随宿主主题：置顶窗与设置窗里的颜色都引用主题键，这里把调色板刷进应用资源
        PluginTheme.Attach(_context);

        // 宿主分配的数据目录：文本与设置都放在这里，卸载时可一并清理
        AppPaths.Initialize(_context.DataDirectory);
        Settings = SettingsStore.Load();

        _currentText = PinnedTextStore.Load();
        _window = new PinnedWindow(this);
        PluginTheme.ApplyToWindow(_window);
        _window.SetTextSilently(_currentText);

        _context.HotkeyPressed += OnHotkeyPressed;
        _context.MenuInvoked += OnMenuInvoked;
        _context.SettingsChanged += OnSettingsChanged;
        // 用户在工具箱改键后，宿主会推最新注册结果；状态栏与设置页要跟着刷新
        _context.HotkeysChanged += OnHotkeysChanged;

        // 启动时**不**显示窗口：插件只在后台待命，用户按呼出热键或点托盘菜单才唤出。
        // 但窗口对象仍必须在这里（任何 await 之前）建好：DeclareHotkeysAsync 等用
        // ConfigureAwait(false)，之后的代码会在线程池线程上继续，那时再碰 WPF 窗口
        // 就会抛"调用线程无法访问此对象"。
        //
        // 首次 Show() 由 WindowStartupLocation=CenterScreen 定在主屏中央，
        // 不会叠在屏幕左上角；用户拖动过之后位置由 WPF 自行保持。

        // 先声明热键与菜单，宿主注册完成后回报结果
        await DeclareHotkeysAsync().ConfigureAwait(false);
        await RefreshTrayMenuAsync().ConfigureAwait(false);
        await DeclareSettingsAsync().ConfigureAwait(false);

        await _context.ReadyAsync("置顶文本", BuildStatusText()).ConfigureAwait(false);
        await _context.LogInfoAsync($"置顶文本插件已启动（数据目录 {AppPaths.DataDirectory}）").ConfigureAwait(false);
    }

    /// <summary>停止插件：保存内容并关闭窗口。</summary>
    public void Stop()
    {
        try
        {
            _window?.SaveNow();
            _window?.Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
        _window = null;
    }

    /// <summary>
    /// 宿主重新注册了热键（用户改键或注册状态变化）。
    ///
    /// 这里做两件事：更新 <see cref="HotkeyResults"/> 让设置页的"未生效"提示保持准确，
    /// 并刷新状态栏——否则用户在工具箱改完键，插件的状态文本还停在旧结果上。
    /// </summary>
    private void OnHotkeysChanged(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        HotkeyResults = results;
        _ = _context.SetStatusAsync(BuildStatusText());

        // 已打开的窗口要立刻显示新键位，否则用户改完键看到的还是旧提示
        _window?.UpdateHintText();
        _window?.RefreshSettingsHotkeys();

        foreach (var failure in results.Where(r => !r.Registered))
            WriteLog($"热键 {failure.Combination} 未生效：{failure.Error}");
    }

    /// <summary>把当前设置里的热键声明给宿主，由宿主统一注册并处理冲突。</summary>
    public async Task DeclareHotkeysAsync()
    {
        var declarations = new List<HotkeyDeclaration>
        {
            new()
            {
                ActionId = HotkeyToggle,
                Combination = Settings.ToggleHotkey,
                Description = "置顶文本：显示/隐藏文本框",
            },
            new()
            {
                ActionId = HotkeyPaste,
                Combination = Settings.PasteHotkey,
                Description = "置顶文本：把剪贴板文本读入文本框",
            },
            new()
            {
                ActionId = HotkeyCopy,
                Combination = Settings.CopyHotkey,
                Description = "置顶文本：复制文本到剪贴板",
            },
            new()
            {
                ActionId = HotkeyCapture,
                Combination = Settings.CaptureHotkey,
                Description = "置顶文本：抓取选中文字并置顶",
            },
            new()
            {
                ActionId = HotkeyResetKeys,
                Combination = "Ctrl+Alt+K",
                Description = "置顶文本：修复键盘输入（重置修饰键）",
                Fixed = true,
            },
        };

        HotkeyResults = await _context.DeclareHotkeysAsync(declarations).ConfigureAwait(false);

        foreach (var failure in HotkeyResults.Where(r => !r.Registered))
            WriteLog($"热键 {failure.Combination} 未生效：{failure.Error}");
    }

    /// <summary>把置顶、自动粘贴等状态同步给宿主托盘菜单（勾选状态）。</summary>
    public async Task RefreshTrayMenuAsync()
    {
        await _context.DeclareTrayMenuAsync(
        [
            new TrayMenuItem
            {
                Id = MenuShow,
                Text = "显示/隐藏文本框",
            },
            new TrayMenuItem
            {
                Id = MenuPaste,
                Text = "把剪贴板文本读入",
            },
            new TrayMenuItem
            {
                Id = MenuCopy,
                Text = "复制文本到剪贴板",
            },
            new TrayMenuItem
            {
                Id = MenuCapture,
                Text = "抓取选中文字并置顶",
            },
            new TrayMenuItem
            {
                Id = MenuClear,
                Text = "清空文本",
            },
            new TrayMenuItem
            {
                Id = MenuSettings,
                Text = "置顶文本设置…",
            },
            new TrayMenuItem { Kind = TrayMenuItemKind.Separator },
            new TrayMenuItem
            {
                Id = MenuResetKeys,
                Text = "修复键盘输入（重置修饰键）",
            },
        ]).ConfigureAwait(false);
    }

    /// <summary>把设置项声明给宿主的设置中心（宿主只渲染，值仍由插件自己保存）。</summary>
    public Task DeclareSettingsAsync()
        => _context.DeclareSettingsAsync(SettingsSchema.Describe(Settings));

    /// <summary>设置变化后重新声明，让设置中心回显夹取后的真实值。不等待结果。</summary>
    public void RefreshSettingsAsync() => _ = DeclareSettingsAsync();

    /// <summary>宿主设置中心改动了本插件的设置项：应用、保存、刷新界面。</summary>
    private void OnSettingsChanged(SettingsChangedPayload payload)
    {
        if (!SettingsSchema.ApplyAll(Settings, payload.Changes)) return;

        SettingsStore.Save(Settings);
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _window?.OnSettingsChanged();
            _ = DeclareSettingsAsync();
            _ = _context.SetStatusAsync(BuildStatusText());
        }));
    }

    private string BuildStatusText()
    {
        var hotkeys = HotkeyResults.Count(r => r.Registered);
        return $"{TextStats.Measure(_currentText).Describe()} · 热键 {hotkeys}/{HotkeyResults.Count}";
    }

    /// <summary>刷新宿主里显示的状态（由窗口在保存节流后调用，不是每次按键）。</summary>
    public void NotifyStatus(string text)
    {
        _currentText = text ?? "";
        _ = _context.SetStatusAsync($"{TextStats.Measure(_currentText).Describe()} · 已自动保存");
    }

    // ---- 热键与菜单回调 ----

    private void OnHotkeyPressed(string actionId)
    {
        switch (actionId)
        {
            case HotkeyToggle:
                _window?.ToggleVisibility();
                break;
            case HotkeyPaste:
                _window?.PasteFromClipboard(alsoPaste: false);
                break;
            case HotkeyCopy:
                _window?.CopyToClipboard(autoPaste: false);
                break;
            case HotkeyCapture:
                CaptureSelection();
                break;
            case HotkeyResetKeys:
                PasteService.ReleaseModifiers();
                WriteLog("已重置键盘修饰键状态");
                break;
        }
    }

    private void OnMenuInvoked(MenuInvokedPayload invoked)
    {
        switch (invoked.ItemId)
        {
            case MenuShow:
                _window?.ToggleVisibility();
                break;
            case MenuPaste:
                _window?.PasteFromClipboard(alsoPaste: false);
                break;
            case MenuCopy:
                _window?.CopyToClipboard(autoPaste: false);
                break;
            case MenuCapture:
                CaptureSelection();
                break;
            case MenuClear:
                _window?.ClearText();
                break;
            case MenuSettings:
                _window?.OpenSettings();
                break;
            case MenuResetKeys:
                PasteService.ReleaseModifiers();
                WriteLog("已重置键盘修饰键状态");
                break;
        }
    }

    /// <summary>
    /// 抓取当前选中文字并置顶：先等热键修饰键松开，再模拟 Ctrl+C，
    /// 等剪贴板真的变化后把文本读进置顶窗。
    /// 整个过程在后台线程完成（含等待与按键注入），只在落地结果时回到 UI 线程。
    /// </summary>
    private void CaptureSelection()
    {
        _window?.ShowBusy("正在抓取选中文字…");

        // 在 UI 线程就把"该归还给谁"问清楚：后台线程不能碰 WPF 窗口属性
        var restoreTarget = _window?.TakeForegroundTargetForPaste() ?? IntPtr.Zero;

        // 热键里若还按着 Alt/Ctrl，注入的 Ctrl+C 会变成别的组合，必须先等修饰键松开
        PasteService.RunAfterModifiersReleased(() =>
        {
            // 这是插件里唯一跑在裸后台线程上的流程，任何未捕获异常都会直接终止进程
            // （宿主表现为"插件崩溃并重启"）。抓取失败只需要提示用户，不该赔上整个插件。
            CaptureResult result;
            try
            {
                result = SelectionCapture.CaptureSelectedText(restoreTarget);
            }
            catch (Exception ex)
            {
                result = CaptureResult.Fail($"抓取过程异常：{ex.Message}");
                WriteLog($"抓取选中文字异常：{ex}");
            }

            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (result.Success)
                {
                    _window?.AdoptCapturedText(result.Text!);
                    WriteLog($"已抓取选中文字（{result.Text!.Length} 字）");
                }
                else
                {
                    _window?.ShowCaptureFailed(result.Error ?? "抓取失败");
                    WriteLog($"抓取选中文字失败：{result.Error}");
                }
            }));
        });
    }

    /// <summary>复制文本到剪贴板；<paramref name="autoPaste"/> 为真时接着粘贴到前台窗口。</summary>
    public void CopyText(string text, bool autoPaste)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 剪贴板写入放到后台 STA 线程，UI 线程不因剪贴板争用而卡顿
        ClipboardText.WriteAsync(text, autoPaste
            ? () => _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    PasteService.PasteToForeground(_window?.TakeForegroundTargetForPaste() ?? IntPtr.Zero);
                    WriteLog("已复制并自动粘贴");
                }
                catch (Exception ex)
                {
                    WriteLog($"粘贴失败: {ex.Message}");
                }
            }))
            : null);

        WriteLog(autoPaste ? "已复制到剪贴板并粘贴" : "已复制到剪贴板");
    }

    /// <summary>设置变更后保存（设置窗口调用）。</summary>
    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>保存当前文本（文本框失焦/退出时调用）。</summary>
    public void SaveText(string text)
    {
        _currentText = text ?? "";
        PinnedTextStore.Save(_currentText);
    }

    /// <summary>写插件日志（由宿主统一落盘）。</summary>
    public void WriteLog(string message) => _ = _context.LogInfoAsync(message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        await _context.DisposeAsync().ConfigureAwait(false);
    }
}