using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using OneBox.Abstractions;
using OneBox.Host.Services;
using OneBox.Host.Windows;

namespace OneBox.Host;

/// <summary>
/// OneBox 宿主：托盘常驻 + 工具箱窗口 + 插件管理。
/// 宿主本身不含任何具体小工具功能，全部能力来自被加载的插件进程。
/// </summary>
public partial class App : Application
{
    /// <summary>宿主版本（握手时告知插件）。</summary>
    public const string HostVersion = "1.0.0";

    private TaskbarIcon? _tray;
    private PluginManager? _manager;
    private PluginRegistry? _registry;
    private HotkeyService? _hotkeys;
    private ToolboxWindow? _toolbox;
    private SettingsCenterWindow? _settingsCenter;

    /// <summary>正在走退出流程。此时关窗是收尾，不该再弹"关闭窗口时问一次"。</summary>
    private bool _exiting;

    /// <summary>
    /// 还没送达的"执行某小工具菜单项"请求。
    /// 资源管理器右键菜单点下时，小工具可能正在启动、还没声明出那一项，先记下等它声明完再补做。
    /// </summary>
    private (string PluginId, string ItemId)? _pendingMenuInvoke;

    /// <summary>插件管理器（工具箱打开时可用）。</summary>
    public PluginManager? Manager => _manager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            // 常驻宿主：UI 线程瞬时异常记录后拦截，不让宿主退出拖垮所有插件
            WriteCrashLog(args.Exception);
            HostLog.Write($"UI 线程异常（已拦截）: {args.Exception.Message}");
            args.Handled = true;
        };

        try
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 注销/关机时系统会关掉所有窗口，那也是收尾，不弹询问
            SessionEnding += (_, _) => _exiting = true;

            // 老版本把数据放在 %LOCALAPPDATA%\OneBox，改成便携后要先搬过来，
            // 否则用户的剪贴板历史、设置、已装插件会像凭空消失了一样
            LegacyDataMigrator.MigrateIfNeeded();

            // 命令行先落日志：右键菜单点击、快捷方式、单实例转交都靠它区分
            HostLog.Write($"宿主启动，命令行：[{string.Join(" ", e.Args)}]");

            // 单实例：托盘、全局热键、插件进程都只容得下一份。
            // 第二个实例把请求转交给现有实例后自行退出，否则会互相抢热键。
            // 命令行开关：--toolbox / --settings / --menu-invoke（资源管理器右键菜单里的项）
            var startupRequest = HostRequest.ParseCommandLine(e.Args);

            if (!SingleInstance.TryAcquire(startupRequest))
            {
                Shutdown();
                return;
            }

            _registry = PluginRegistry.Load();

            // 主题必须在任何窗口创建之前就位：窗口里的颜色全部走 DynamicResource，
            // 资源还没写进去时窗口会按默认色渲染，之后再补会在屏幕上闪一下。
            ThemeManager.Initialize(_registry.Settings.Theme);
            ThemeManager.Changed += theme =>
            {
                // 系统标题栏不认 WPF 资源，得逐个窗口再刷一次；插件窗口由各插件自己跟着换
                ThemeManager.RefreshTitleBars();
                _manager?.BroadcastTheme(theme);
            };

            // 热键需要一个已创建句柄的窗口承载，因此先建一个不可见的主窗口
            var hotkeyWindow = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Visibility = Visibility.Hidden,
            };
            hotkeyWindow.Show();
            _hotkeys = new HotkeyService(hotkeyWindow);

            _manager = new PluginManager(_registry, Dispatcher, _hotkeys, HostVersion);
            _manager.NotificationRequested += OnPluginNotification;
            _manager.Changed += () => Dispatcher.BeginInvoke(OnPluginsChanged);
            _manager.MenuChanged += () => Dispatcher.BeginInvoke(OnPluginsChanged);

            _tray = CreateTrayIcon();

            _manager.Discover();
            _manager.StartEnabled();

            // 按设置把右键菜单摆上（小工具稍后声明出菜单项时会再同步一次）
            SyncContextMenu();

            if (_registry.Settings.ShowToolboxOnStartup) OpenToolbox();

            // 本次启动自带的请求：方便做快捷方式，也便于自动化验证
            //   OneBox.exe --toolbox                     打开工具箱
            //   OneBox.exe --settings                    打开设置中心
            //   OneBox.exe --menu-invoke <id> <itemId>   执行某小工具的菜单项
            // （已有实例在跑时，这些请求由上面的单实例闸门转交给它执行）
            Dispatcher.BeginInvoke(new Action(() => HandleRequest(startupRequest)));

            // 后续实例转交进来的请求：回调在后台线程，切回 UI 线程再处理
            SingleInstance.StartListening(request => Dispatcher.BeginInvoke(new Action(() => HandleRequest(request))));

            HostLog.Write($"OneBox 宿主已启动（版本 {HostVersion}，插件 {_manager.Plugins.Count} 个）");
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    // ---- 请求处理 ----

    /// <summary>执行一个来自命令行或后续实例的请求。</summary>
    private void HandleRequest(string? request)
    {
        if (string.IsNullOrEmpty(request)) return;

        HostLog.Write($"收到请求：{request}");

        switch (request)
        {
            case null or "":
                return;

            case HostRequest.Toolbox:
                OpenToolbox();
                return;

            case HostRequest.Settings:
                OpenSettingsCenter();
                return;
        }

        if (HostRequest.TryUnpackMenuInvoke(request, out var pluginId, out var itemId))
            InvokePluginMenu(pluginId, itemId);
        else
            HostLog.Write($"无法识别的请求：{request}");
    }

    /// <summary>
    /// 让小工具执行它声明过的某个菜单项。资源管理器右键菜单里的项就是从这里进来的。
    /// 小工具可能还没启动完，那就先记下，等它声明出该项再补做。
    /// </summary>
    private void InvokePluginMenu(string pluginId, string itemId)
    {
        if (TryInvokePluginMenu(pluginId, itemId)) return;

        _pendingMenuInvoke = (pluginId, itemId);
        HostLog.Write($"小工具 {pluginId} 尚未就绪，等它声明出「{itemId}」后再执行");
    }

    /// <summary>尝试把菜单点击转给对应小工具；插件没在跑或没声明该项时返回 false。</summary>
    private bool TryInvokePluginMenu(string pluginId, string itemId)
    {
        var runtime = _manager?.Plugins
            .FirstOrDefault(plugin => string.Equals(plugin.Manifest?.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            ?.Runtime;

        if (runtime is null || runtime.State is not PluginState.Running) return false;
        if (!runtime.MenuItems.Any(item => string.Equals(item.Id, itemId, StringComparison.Ordinal))) return false;

        _ = runtime.SendMenuInvokedAsync(itemId, @checked: false);
        return true;
    }

    /// <summary>补做先前没送达的菜单请求（小工具声明出菜单项后会再触发一次）。</summary>
    private void RetryPendingMenuInvoke()
    {
        if (_pendingMenuInvoke is not { } pending) return;
        if (!TryInvokePluginMenu(pending.PluginId, pending.ItemId)) return;

        _pendingMenuInvoke = null;
    }

    /// <summary>
    /// 插件列表或菜单发生变化的统一处理：重绘托盘，并补做先前没送达的菜单请求。
    /// 右键菜单里只有一个"打开工具箱"，不随插件增删变化，所以不在这里同步。
    /// </summary>
    private void OnPluginsChanged()
    {
        RefreshTrayMenu();
        RetryPendingMenuInvoke();
    }

    /// <summary>按设置同步资源管理器右键菜单（勾选则写入，取消则移除）。</summary>
    private void SyncContextMenu() => ContextMenuRegistrar.Sync(_registry);

    // ---- 工具箱 ----

    /// <summary>打开（或激活）工具箱窗口。</summary>
    public void OpenToolbox()
    {
        if (_toolbox is { IsVisible: true })
        {
            _toolbox.Activate();
            _toolbox.Rebuild();
            return;
        }

        if (_manager is null || _registry is null) return;

        _toolbox = new ToolboxWindow(_manager, _registry);
        _toolbox.Closing += OnHostWindowClosing;
        _toolbox.Closed += (_, _) =>
        {
            _toolbox = null;
            OnAllWindowsClosed();
        };
        _toolbox.Show();
    }

    /// <summary>打开（或激活）设置中心：按插件分层展示宿主与各插件的设置。</summary>
    public void OpenSettingsCenter()
    {
        if (_settingsCenter is { IsVisible: true })
        {
            _settingsCenter.Activate();
            return;
        }
        if (_manager is null || _registry is null) return;

        _settingsCenter = new SettingsCenterWindow(_manager, _registry);
        _settingsCenter.Closing += OnHostWindowClosing;
        _settingsCenter.Closed += (_, _) =>
        {
            _settingsCenter = null;
            OnAllWindowsClosed();
        };
        _settingsCenter.Show();
    }

    /// <summary>
    /// 宿主窗口即将关闭：第一次关窗口时间一次"要不要顺便退出 OneBox"，把选择记进设置。
    /// 之后不再打扰，按设置执行——想改随时去设置中心的「宿主行为」。
    ///
    /// 之所以要问：托盘常驻的程序里，"关掉窗口后进程还在不在"两种习惯的人都有，
    /// 与其替用户猜，不如问一次并记住。
    /// </summary>
    private void OnHostWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_registry is null) return;
        if (_registry.Settings.CloseWindowAction is not null) return;
        if (sender is not Window window) return;

        var choice = CloseBehaviorDialog.Ask(window);
        if (choice is null)
        {
            e.Cancel = true;   // 用户取消：窗口留着，不关
            return;
        }

        _registry.Settings.CloseWindowAction = choice;
        _registry.Save();
    }

    /// <summary>
    /// 窗口全关掉之后的收尾：按设置决定是继续在托盘待命，还是退出 OneBox。
    ///
    /// 只关掉其中一个（例如顺手关掉设置中心、工具箱还开着）一律继续运行——
    /// 否则会出现"想关个小窗，结果整个程序没了"的误退。
    /// </summary>
    private void OnAllWindowsClosed()
    {
        if (_toolbox is not null || _settingsCenter is not null) return;
        if (!CloseWindowActions.ShouldExit(_registry?.Settings.CloseWindowAction)) return;

        _exiting = true;
        Shutdown();
    }

    // ---- 托盘 ----

    private TaskbarIcon CreateTrayIcon()
    {
        var tray = new TaskbarIcon
        {
            ToolTipText = "OneBox 小工具平台",
            Icon = LoadAppIcon(),
        };

        tray.ContextMenu = new ContextMenu();
        RefreshTrayMenu();

        // 双击托盘：打开工具箱（平台的主入口）
        tray.TrayMouseDoubleClick += (_, _) => OpenToolbox();

        // 左键单击也打开工具箱：托盘图标是平台的主入口，双击对随手一点来说太重。
        // OpenToolbox 是幂等的（已打开时只 Activate + Rebuild），双击触发两次单击事件也无副作用。
        tray.TrayLeftMouseUp += (_, _) => OpenToolbox();

        return tray;
    }

    /// <summary>
    /// 重建托盘右键菜单：工具箱 → 每个插件一个子菜单 → 宿主设置/退出。
    ///
    /// 插件功能收进各自子菜单而不是平铺：小工具一多，平铺会把右键菜单撑得
    /// 很长且难以找到目标。禁用或停止的插件不出现在菜单里，因此插件项随状态自动增减。
    /// </summary>
    private void RefreshTrayMenu()
    {
        if (_tray is null) return;

        var menu = new ContextMenu();

        var toolbox = new MenuItem { Header = "打开工具箱…" };
        toolbox.Click += (_, _) => OpenToolbox();
        menu.Items.Add(toolbox);

        var settingsCenter = new MenuItem { Header = "设置…" };
        settingsCenter.Click += (_, _) => OpenSettingsCenter();
        menu.Items.Add(settingsCenter);

        var groups = BuildPluginMenuGroups(out var runtimes);
        if (groups.Count > 0)
        {
            menu.Items.Add(new Separator());

            foreach (var group in groups)
            {
                var submenu = new MenuItem { Header = group.PluginName };

                // 子菜单标题带插件自己的图标，菜单里一眼能认出是哪个小工具
                var pluginRuntime = runtimes[group.PluginId];
                if (PluginIcons.ForManifest(pluginRuntime.Manifest) is { } pluginIcon)
                    submenu.Icon = new System.Windows.Controls.Image { Source = pluginIcon, Width = 16, Height = 16 };

                foreach (var entry in group.Entries)
                {
                    if (entry.IsSeparator)
                    {
                        submenu.Items.Add(new Separator());
                        continue;
                    }

                    submenu.Items.Add(CreateMenuItem(pluginRuntime, entry.Item!));
                }

                menu.Items.Add(submenu);
            }
        }

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出 OneBox" };
        exit.Click += (_, _) =>
        {
            // 明确要退出：先立起标志，免得随后的关窗又被"关闭窗口时问一次"拦一下
            _exiting = true;
            Shutdown();
        };
        menu.Items.Add(exit);

        _tray.ContextMenu = menu;
    }

    /// <summary>
    /// 收集正在运行的插件所声明的菜单项，交给纯逻辑整理成分组，
    /// 并给出 pluginId → runtime 的查表（分组本身保持纯数据，不认识运行时）。
    /// </summary>
    private IReadOnlyList<PluginMenuGroup> BuildPluginMenuGroups(
        out Dictionary<string, PluginRuntime> runtimes)
    {
        runtimes = new Dictionary<string, PluginRuntime>(StringComparer.Ordinal);
        if (_manager is null) return [];

        var sources = new List<(string, string, IReadOnlyList<TrayMenuItem>)>();
        foreach (var plugin in _manager.Plugins)
        {
            var runtime = plugin.Runtime;
            if (runtime is null || plugin.Manifest is null) continue;
            if (runtime.State is not (PluginState.Running or PluginState.Starting)) continue;

            runtimes[runtime.Manifest.Id] = runtime;
            sources.Add((runtime.Manifest.Id, runtime.Manifest.Name, runtime.MenuItems));
        }

        return TrayMenuBuilder.Build(sources);
    }

    /// <summary>把一个插件声明的菜单项转成 WPF 菜单项，点击后回传给该插件。</summary>
    private static MenuItem CreateMenuItem(PluginRuntime runtime, TrayMenuItem declared)
    {
        var item = new MenuItem
        {
            Header = declared.Text,
            IsEnabled = declared.Enabled,
            IsCheckable = declared.Kind == TrayMenuItemKind.Toggle,
            IsChecked = declared.Checked,
        };

        if (!string.IsNullOrWhiteSpace(declared.ToolTip)) item.ToolTip = declared.ToolTip;

        var itemId = declared.Id;
        var isToggle = declared.Kind == TrayMenuItemKind.Toggle;
        item.Click += (_, _) => _ = runtime.SendMenuInvokedAsync(itemId, isToggle && item.IsChecked);

        return item;
    }

    private void OnPluginNotification(PluginRuntime runtime, NotifyPayload notify)
    {
        var title = string.IsNullOrWhiteSpace(notify.Title) ? runtime.Manifest.Name : notify.Title;
        var icon = notify.Level switch
        {
            LogLevel.Error => BalloonIcon.Error,
            LogLevel.Warning => BalloonIcon.Warning,
            _ => BalloonIcon.Info,
        };

        _tray?.ShowBalloonTip(title, notify.Message, icon);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = GetResourceStream(uri)!.Stream;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        catch
        {
            // 图标缺失不应阻止宿主启动
            return SystemIcons.Application;
        }
    }

    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;
        HostLog.Write($"未处理异常: {ex}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.StopAll();
        _manager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tray?.Dispose();
        SingleInstance.Release();
        HostLog.Write("OneBox 宿主已退出");
        base.OnExit(e);
    }
}