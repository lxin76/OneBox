using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipboardFloat.App.Services;
using ClipboardFloat.App.ViewModels;
using ClipboardFloat.App.Windows;
using ClipboardFloat.Core;
using Hardcodet.Wpf.TaskbarNotification;

namespace ClipboardFloat.App;

/// <summary>无主窗口启动：托盘常驻 + 悬浮窗 + 剪贴板监听 + SQLite 持久化。</summary>
public partial class App : Application
{
    /// <summary>当前设置（启动时从磁盘加载，修改即保存）。</summary>
    public static Settings CurrentSettings { get; private set; } = new();

    private TaskbarIcon? _tray;
    private MenuItem? _monitorMenuItem;
    private FloatingWindow? _floatingWindow;
    private FloatingViewModel? _viewModel;
    private ClipboardWatcher? _clipboardWatcher;
    private HistoryStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            // 常驻工具：UI 线程瞬时异常（如剪贴板被占用）记录后拦截，不让应用退出
            WriteCrashLog(args.Exception);
            AppLog.Write($"UI 线程异常（已拦截，应用继续运行）: {args.Exception.Message}");
            args.Handled = true;
        };

        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CurrentSettings = SettingsStore.Load();

            _floatingWindow = new FloatingWindow();

            _store = new HistoryStore(AppPaths.HistoryDbPath);
            _viewModel = new FloatingViewModel(CurrentSettings, _store);
            _floatingWindow.DataContext = _viewModel;

            // 恢复历史：旧→新顺序加入，保证最新在前
            foreach (var item in _store.LoadAll().Reverse())
                _viewModel.AddItem(item);

            _tray = CreateTrayIcon();

            // 先 Show() 创建窗口句柄（触发 SourceInitialized），再挂剪贴板监听
            _floatingWindow.Show();

            _clipboardWatcher = new ClipboardWatcher(_floatingWindow, startListening: CurrentSettings.MonitoringEnabled);
            _clipboardWatcher.ItemCaptured += OnItemCaptured;
            _viewModel.CopyRequested += OnCopyRequested;
            _floatingWindow.MarkSelfWriteAction = () => _clipboardWatcher.MarkSelfWrite();
            _floatingWindow.ViewSelectedRequested += OnViewSelectedRequested;
            _floatingWindow.MonitoringToggleRequested += SetMonitoring;

            // 启动时捕获一次当前剪贴板，让历史面板初始不空（监听关闭时不读取）
            if (CurrentSettings.MonitoringEnabled)
            {
                try
                {
                    var initial = ClipboardSnapshot.Capture();
                    if (initial is not null) _viewModel.AddItem(initial);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"初始捕获失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    private void OnItemCaptured(ClipItem item)
    {
        _viewModel?.AddItem(item);
        AppLog.Write($"捕获剪贴板: {item.Kind} {item.Hash[..Math.Min(8, item.Hash.Length)]}");
    }

    /// <summary>开启/关闭剪贴板监听（悬浮窗开关、托盘菜单、设置页共用入口）。
    /// 关闭时注销监听器，系统不再投递剪贴板更新消息，完全不记录新的复制内容。</summary>
    public void SetMonitoring(bool enabled)
    {
        CurrentSettings.MonitoringEnabled = enabled;
        SettingsStore.Save(CurrentSettings);

        if (enabled) _clipboardWatcher?.Start();
        else _clipboardWatcher?.Stop();

        _floatingWindow?.ApplyMonitoringState(enabled);
        if (_monitorMenuItem is not null) _monitorMenuItem.IsChecked = enabled;
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

        AppLog.Write(req.PlainText ? "复制回剪贴板(纯文本)" : $"复制回剪贴板: {req.Item.Kind}");
    }

    /// <summary>剪贴板写入完成后，在 UI 线程执行自动粘贴。</summary>
    private void MaybePaste(CopyRequest req)
    {
        if (!req.AutoPaste) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var target = _floatingWindow?.TakeForegroundTargetForPaste() ?? IntPtr.Zero;
                PasteService.PasteToForeground(target);
                AppLog.Write("已自动粘贴");
            }
            catch (Exception ex)
            {
                AppLog.Write($"粘贴失败: {ex.Message}");
                WriteCrashLog(ex);
            }
        }));
    }

    /// <summary>悬浮查看选中内容：模拟 Ctrl+C 复制前台窗口的选中文本，
    /// 剪贴板更新后自动打开置顶的完整查看窗口（内容已复制，可直接使用）。
    /// 先等热键修饰键松开再注入，避免 Ctrl+Alt+C 的 Alt 残留导致复制失效。</summary>
    private void OnViewSelectedRequested()
    {
        if (_clipboardWatcher is null || _floatingWindow is null) return;
        PasteService.RunAfterModifiersReleased(() => Dispatcher.BeginInvoke(new Action(StartViewSelectedCopy)));
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
            AppLog.Write("悬浮查看：已打开置顶查看窗口");
        };
        timer.Tick += (_, _) =>
        {
            _clipboardWatcher.ItemCaptured -= handler!;
            timer.Stop();
            if (resumePaused) _clipboardWatcher.Stop();
            AppLog.Write("悬浮查看：复制超时（可能没有选中内容）");
        };

        _clipboardWatcher.ItemCaptured += handler;
        timer.Start();
        PasteService.CopyByKey();
        AppLog.Write("悬浮查看：已模拟 Ctrl+C 复制选中内容");
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var tray = new TaskbarIcon
        {
            ToolTipText = "ClipboardFloat 悬浮剪贴板",
            Icon = LoadAppIcon(),
        };

        var show = new MenuItem { Header = "显示/隐藏悬浮窗" };
        show.Click += (_, _) => ToggleWindow();
        _monitorMenuItem = new MenuItem
        {
            Header = "监听剪贴板（关闭后暂停记录）",
            IsCheckable = true,
            IsChecked = CurrentSettings.MonitoringEnabled,
        };
        _monitorMenuItem.Click += (_, _) => SetMonitoring(_monitorMenuItem.IsChecked);

        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => _floatingWindow?.OpenSettings();
        var clear = new MenuItem { Header = "清空历史" };
        clear.Click += (_, _) => _viewModel?.ClearHistoryCommand.Execute(null);
        var resetKeys = new MenuItem { Header = "修复键盘输入（重置修饰键）" };
        resetKeys.Click += (_, _) => PasteService.ReleaseModifiers();
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(show);
        menu.Items.Add(_monitorMenuItem);
        menu.Items.Add(settings);
        menu.Items.Add(clear);
        menu.Items.Add(resetKeys);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        tray.ContextMenu = menu;
        // 双击托盘图标：唤醒 + 临时置顶展示（与置顶设置无关）
        tray.TrayMouseDoubleClick += (_, _) => _floatingWindow?.WakeToFront();

        return tray;
    }

    private void ToggleWindow()
    {
        if (_floatingWindow is null) return;
        _floatingWindow.ToggleVisibility();
    }

    private static Icon LoadAppIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        return new Icon(ms);
    }

    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ClipboardFloat-crash.log"),
                $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // 日志写入失败时忽略，避免二次崩溃
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clipboardWatcher?.Dispose();
        _store?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
