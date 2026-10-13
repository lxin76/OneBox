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
            _registry = PluginRegistry.Load();

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
            _manager.Changed += () => Dispatcher.BeginInvoke(RefreshTrayMenu);
            _manager.MenuChanged += () => Dispatcher.BeginInvoke(RefreshTrayMenu);

            _tray = CreateTrayIcon();

            _manager.Discover();
            _manager.StartEnabled();

            if (_registry.Settings.ShowToolboxOnStartup) OpenToolbox();

            HostLog.Write($"OneBox 宿主已启动（版本 {HostVersion}，插件 {_manager.Plugins.Count} 个）");
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

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
        _toolbox.Closed += (_, _) => _toolbox = null;
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
        _settingsCenter.Closed += (_, _) => _settingsCenter = null;
        _settingsCenter.Show();
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

                foreach (var entry in group.Entries)
                {
                    if (entry.IsSeparator)
                    {
                        submenu.Items.Add(new Separator());
                        continue;
                    }

                    submenu.Items.Add(CreateMenuItem(runtimes[group.PluginId], entry.Item!));
                }

                menu.Items.Add(submenu);
            }
        }

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出 OneBox" };
        exit.Click += (_, _) => Shutdown();
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
        HostLog.Write("OneBox 宿主已退出");
        base.OnExit(e);
    }
}
