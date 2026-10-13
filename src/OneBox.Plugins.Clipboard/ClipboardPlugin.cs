using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;
using OneBox.Plugins.Clipboard.Core;
using OneBox.Plugins.Clipboard.Services;
using OneBox.Plugins.Clipboard.ViewModels;
using OneBox.Plugins.Clipboard.Windows;

namespace OneBox.Plugins.Clipboard;

/// <summary>
/// 剪贴板小工具插件主体：悬浮窗 + 剪贴板监听 + SQLite 持久化。
/// 全局热键、托盘菜单、日志与设置目录全部由宿主提供（见 <see cref="PluginContext"/>）。
/// </summary>
public sealed class ClipboardPlugin : IAsyncDisposable
{
    /// <summary>热键动作标识（与宿主转发回来的 actionId 对应）。</summary>
    private const string HotkeyPanel = "panel";
    private const string HotkeyPaste = "paste";
    private const string HotkeyView = "view";
    private const string HotkeyResetKeys = "resetKeys";

    /// <summary>托盘菜单项标识。</summary>
    private const string MenuShow = "show";
    private const string MenuMonitor = "monitor";
    private const string MenuSettings = "settings";
    private const string MenuClear = "clear";
    private const string MenuResetKeys = "resetKeys";

    private readonly PluginContext _context;
    private readonly Dispatcher _dispatcher;

    private FloatingWindow? _floatingWindow;
    private FloatingViewModel? _viewModel;
    private ClipboardWatcher? _clipboardWatcher;
    private HistoryStore? _store;
    private bool _disposed;

    public ClipboardPlugin(PluginContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    /// <summary>当前设置（改动即保存）。</summary>
    public Settings Settings { get; private set; } = new();

    /// <summary>宿主是否已注册某热键（供设置页提示）。</summary>
    public IReadOnlyList<HotkeyRegistrationResult> HotkeyResults { get; private set; } = [];

    /// <summary>启动插件：恢复历史、创建悬浮窗、开始监听、向宿主声明热键与托盘菜单。</summary>
    public async Task StartAsync()
    {
        // 宿主分配的数据目录：历史库与设置都放在这里，卸载时可一并清理
        AppPaths.Initialize(_context.DataDirectory);
        Settings = SettingsStore.Load();

        _store = new HistoryStore(AppPaths.HistoryDbPath);
        _viewModel = new FloatingViewModel(Settings, _store);
        _viewModel.CopyRequested += OnCopyRequested;

        _floatingWindow = new FloatingWindow(this);
        _floatingWindow.DataContext = _viewModel;
        _floatingWindow.MarkSelfWriteAction = () => _clipboardWatcher?.MarkSelfWrite();
        _floatingWindow.MonitoringToggleRequested += SetMonitoring;

        // 恢复历史：旧→新顺序加入，保证最新在前
        foreach (var item in _store.LoadAll().Reverse())
            _viewModel.AddItem(item);

        // 先 Show() 创建窗口句柄（剪贴板监听需要一个消息窗口）
        _floatingWindow.Show();

        _clipboardWatcher = new ClipboardWatcher(_floatingWindow, startListening: Settings.MonitoringEnabled);
        _clipboardWatcher.ItemCaptured += OnItemCaptured;

        // 启动时捕获一次当前剪贴板，让历史面板初始不空（监听关闭时不读取）
        if (Settings.MonitoringEnabled)
        {
            try
            {
                var initial = ClipboardSnapshot.Capture();
                if (initial is not null) _viewModel.AddItem(initial);
            }
            catch (Exception ex)
            {
                WriteLog($"初始捕获失败: {ex.Message}");
            }
        }

        _context.HotkeyPressed += OnHotkeyPressed;
        _context.MenuInvoked += OnMenuInvoked;

        // 先声明热键与菜单，宿主注册完成后回报结果
        await DeclareHotkeysAsync().ConfigureAwait(false);
        await RefreshTrayMenuAsync().ConfigureAwait(false);
        await _context.ReadyAsync("剪贴板", BuildStatusText()).ConfigureAwait(false);
        await _context.LogInfoAsync($"剪贴板插件已启动（数据目录 {AppPaths.DataDirectory}）").ConfigureAwait(false);
    }

    /// <summary>停止插件：关闭监听与窗口。</summary>
    public void Stop()
    {
        _clipboardWatcher?.Dispose();
        _clipboardWatcher = null;
        _store?.Dispose();
        _store = null;

        try
        {
            _floatingWindow?.Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
        _floatingWindow = null;
    }

    /// <summary>把当前设置里的热键声明给宿主，由宿主统一注册并处理冲突。</summary>
    public async Task DeclareHotkeysAsync()
    {
        var declarations = new List<HotkeyDeclaration>
        {
            new()
            {
                ActionId = HotkeyPanel,
                Combination = Settings.PanelHotkey,
                Description = "剪贴板：呼出/收起历史面板",
            },
            new()
            {
                ActionId = HotkeyPaste,
                Combination = Settings.PasteHotkey,
                Description = "剪贴板：粘贴当前剪贴板内容",
            },
            new()
            {
                ActionId = HotkeyView,
                Combination = Settings.ViewHotkey,
                Description = "剪贴板：复制选中内容并查看",
            },
            new()
            {
                ActionId = HotkeyResetKeys,
                Combination = "Ctrl+Alt+K",
                Description = "剪贴板：修复键盘输入（重置修饰键）",
                Fixed = true,
            },
        };

        HotkeyResults = await _context.DeclareHotkeysAsync(declarations).ConfigureAwait(false);

        var failed = HotkeyResults.Where(r => !r.Registered).ToList();
        foreach (var failure in failed)
            WriteLog($"热键 {failure.Combination} 未生效：{failure.Error}");
    }

    /// <summary>把监听开关、自动粘贴、置顶等状态同步给宿主托盘菜单（勾选状态）。</summary>
    public async Task RefreshTrayMenuAsync()
    {
        await _context.DeclareTrayMenuAsync(
        [
            new TrayMenuItem
            {
                Id = MenuShow,
                Text = "显示/隐藏悬浮窗",
            },
            new TrayMenuItem
            {
                Id = MenuMonitor,
                Text = "监听剪贴板（关闭后暂停记录）",
                Kind = TrayMenuItemKind.Toggle,
                Checked = Settings.MonitoringEnabled,
            },
            new TrayMenuItem
            {
                Id = MenuSettings,
                Text = "剪贴板设置…",
            },
            new TrayMenuItem
            {
                Id = MenuClear,
                Text = "清空剪贴板历史",
            },
            new TrayMenuItem { Kind = TrayMenuItemKind.Separator },
            new TrayMenuItem
            {
                Id = MenuResetKeys,
                Text = "修复键盘输入（重置修饰键）",
            },
        ]).ConfigureAwait(false);
    }

    private string BuildStatusText()
    {
        var monitor = Settings.MonitoringEnabled ? "监听中" : "已暂停";
        var hotkeys = HotkeyResults.Count(r => r.Registered);
        return $"{monitor} · 热键 {hotkeys}/{HotkeyResults.Count}";
    }

    // ---- 热键与菜单回调 ----

    private void OnHotkeyPressed(string actionId)
    {
        switch (actionId)
        {
            case HotkeyPanel:
                _floatingWindow?.ToggleVisibility();
                break;
            case HotkeyPaste:
                _floatingWindow?.PasteLatestFromHotkey();
                break;
            case HotkeyView:
                OnViewSelectedRequested();
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
                _floatingWindow?.ToggleVisibility();
                break;
            case MenuMonitor:
                SetMonitoring(invoked.Checked);
                break;
            case MenuSettings:
                _floatingWindow?.OpenSettings();
                break;
            case MenuClear:
                _viewModel?.ClearHistoryCommand.Execute(null);
                break;
            case MenuResetKeys:
                PasteService.ReleaseModifiers();
                WriteLog("已重置键盘修饰键状态");
                break;
        }
    }

    // ---- 剪贴板逻辑（原 App.xaml.cs 迁移） ----

    private void OnItemCaptured(ClipItem item)
    {
        _viewModel?.AddItem(item);
        WriteLog($"捕获剪贴板: {item.Kind} {item.Hash[..Math.Min(8, item.Hash.Length)]}");
    }

    /// <summary>开启/关闭剪贴板监听（悬浮窗开关、托盘菜单、设置页共用入口）。
    /// 关闭时注销监听器，系统不再投递剪贴板更新消息，完全不记录新的复制内容。</summary>
    public void SetMonitoring(bool enabled)
    {
        Settings.MonitoringEnabled = enabled;
        SettingsStore.Save(Settings);

        if (enabled) _clipboardWatcher?.Start();
        else _clipboardWatcher?.Stop();

        _floatingWindow?.ApplyMonitoringState(enabled);
        _ = _context.SetStatusAsync(BuildStatusText());
        _ = RefreshTrayMenuAsync();
    }

    private void OnCopyRequested(CopyRequest req)
    {
        if (_clipboardWatcher is null) return;

        // 先标记自身写入（若 WM_CLIPBOARDUPDATE 同步到达也能被抑制），再写剪贴板
        _clipboardWatcher.MarkSelfWrite();

        // 剪贴板写入放到后台 STA 线程，UI 线程不因剪贴板争用而卡顿
        if (req.PlainText && req.Item.Text is not null)
            ClipboardWriter.SetClipboardTextAsync(req.Item.Text, () => MaybePaste(req));
        else
            ClipboardWriter.SetClipboardAsync(req.Item, () => MaybePaste(req));

        WriteLog(req.PlainText ? "复制回剪贴板(纯文本)" : $"复制回剪贴板: {req.Item.Kind}");
    }

    /// <summary>剪贴板写入完成后，在 UI 线程执行自动粘贴。</summary>
    private void MaybePaste(CopyRequest req)
    {
        if (!req.AutoPaste) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var target = _floatingWindow?.TakeForegroundTargetForPaste() ?? IntPtr.Zero;
                PasteService.PasteToForeground(target);
                WriteLog("已自动粘贴");
            }
            catch (Exception ex)
            {
                WriteLog($"粘贴失败: {ex.Message}");
            }
        }));
    }

    /// <summary>悬浮查看选中内容：模拟 Ctrl+C 复制前台窗口的选中文本，
    /// 剪贴板更新后自动打开置顶的完整查看窗口。
    /// 先等热键修饰键松开再注入，避免热键里的 Alt 残留导致复制失效。</summary>
    private void OnViewSelectedRequested()
    {
        if (_clipboardWatcher is null || _floatingWindow is null) return;
        PasteService.RunAfterModifiersReleased(() => _dispatcher.BeginInvoke(new Action(StartViewSelectedCopy)));
    }

    private void StartViewSelectedCopy()
    {
        if (_clipboardWatcher is null || _floatingWindow is null) return;

        // 监听暂停时，为完成这次显式操作临时开启监听，取到内容后立即恢复暂停
        var resumePaused = !_clipboardWatcher.IsListening;
        if (resumePaused) _clipboardWatcher.Start();

        Action<ClipItem>? handler = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };

        handler = item =>
        {
            _clipboardWatcher.ItemCaptured -= handler!;
            timer.Stop();
            if (resumePaused) _clipboardWatcher.Stop();
            _floatingWindow!.OpenPinnedViewer(item);
            WriteLog("悬浮查看：已打开置顶查看窗口");
        };
        timer.Tick += (_, _) =>
        {
            _clipboardWatcher.ItemCaptured -= handler!;
            timer.Stop();
            if (resumePaused) _clipboardWatcher.Stop();
            WriteLog("悬浮查看：复制超时（可能没有选中内容）");
        };

        _clipboardWatcher.ItemCaptured += handler;
        timer.Start();
        PasteService.CopyByKey();
        WriteLog("悬浮查看：已模拟 Ctrl+C 复制选中内容");
    }

    /// <summary>设置变更后保存（设置窗口调用）。</summary>
    public void SaveSettings() => SettingsStore.Save(Settings);

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
