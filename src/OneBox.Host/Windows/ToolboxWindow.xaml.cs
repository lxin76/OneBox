using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OneBox.Abstractions;
using OneBox.Abstractions.Theming;
using OneBox.Host.Services;
using OneBox.Host.ViewModels;

namespace OneBox.Host.Windows;

/// <summary>工具箱：列出全部小工具，提供启用/禁用、重启、安装与卸载，以及热键总览。</summary>
public partial class ToolboxWindow : Window
{
    private readonly PluginManager _manager;
    private readonly PluginRegistry _registry;

    /// <summary>热键页当前的分组（整体重建，数量很小）。</summary>
    private List<HotkeyGroupViewModel> _hotkeyGroups = [];

    /// <summary>
    /// 窗口句柄就绪后，把系统标题栏切成与当前主题一致的明暗。
    /// 不接管窗口框架：拖动、最大化、贴边分屏仍由系统负责。
    /// </summary>
    private void OnSourceInitialized(object sender, EventArgs e)
        => DarkTitleBar.Apply(this, ThemeManager.Current.IsDark);

    /// <summary>
    /// 换主题后重建列表：窗口本身的颜色走 DynamicResource 会自动更新，
    /// 但热键行的状态色是视图模型算出来的画刷（绑定到属性），只能靠重建换新。
    /// </summary>
    private void OnThemeChanged(ThemePalette theme) => Dispatcher.BeginInvoke(Rebuild);


    public ToolboxWindow(PluginManager manager, PluginRegistry registry)
    {
        InitializeComponent();
        _manager = manager;
        _registry = registry;

        ViewModel = new ToolboxViewModel();
        DataContext = ViewModel;

        PluginsDirText.Text = ViewModel.PluginsDirectory;
        DataDirText.Text = ViewModel.DataDirectory;
        LogDirText.Text = ViewModel.LogDirectory;

        Loaded += (_, _) =>
        {
            _manager.Changed += OnManagerChanged;
            _manager.StatusChanged += OnPluginStatusChanged;
            _manager.MenuChanged += Rebuild;
            ThemeManager.Changed += OnThemeChanged;
            Rebuild();
        };
        Closed += (_, _) =>
        {
            _manager.Changed -= OnManagerChanged;
            _manager.StatusChanged -= OnPluginStatusChanged;
            _manager.MenuChanged -= Rebuild;
            ThemeManager.Changed -= OnThemeChanged;
        };
    }

    private void OnPluginStatusChanged(PluginRuntime runtime, string text) => Rebuild();

    public ToolboxViewModel ViewModel { get; }

    private void OnManagerChanged() => Dispatcher.BeginInvoke(Rebuild);

    /// <summary>从管理器状态重建列表（数量很小，整体重建最不易出错）。</summary>
    public void Rebuild()
    {
        // 保留用户当前选中的插件，重建后尽量还原
        var selectedId = (PluginList.SelectedItem as PluginItemViewModel)?.Id;

        ViewModel.Plugins.Clear();
        foreach (var plugin in _manager.Plugins)
            ViewModel.Plugins.Add(new PluginItemViewModel(plugin, OnTogglePlugin, OnUninstallPlugin, RestartPlugin));

        var enabled = ViewModel.Plugins.Count(p => p.Enabled && !p.IsBroken);
        var running = ViewModel.Plugins.Count(p => p.StateText == "运行中");
        SummaryText.Text = $"共 {ViewModel.Plugins.Count} 个小工具 · 已启用 {enabled} · 运行中 {running}";

        EmptyHint.Visibility = ViewModel.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHotkeyList();

        // 底部状态行只留给「重启中 / 已安装 / 已卸载」这类临时提示。
        // 插件目录已经在「目录与日志」页签里给出路径和打开按钮，这里不再重复显示；
        // 顺带清掉上一条临时提示，免得刷新后残留一句过期的消息。
        FooterHint.Text = string.Empty;

        if (selectedId is not null)
            PluginList.SelectedItem = ViewModel.Plugins.FirstOrDefault(p => p.Id == selectedId);
    }

    /// <summary>
    /// 重建热键页：按小工具分组，而不是把全部热键平铺成一列。
    ///
    /// 平铺时插件一多就退化成一大片看不出归属的行，想找某个小工具的热键只能靠肉眼扫。
    /// 这里以「小工具」为第一层：每组一张卡片，展开才看到它自己的热键，
    /// 组头直接给出数量与冲突概况，一眼能定位到出问题的那一个。
    /// </summary>
    private void UpdateHotkeyList()
    {
        // 保留展开/折叠状态与滚动位置，避免每次刷新（插件状态一有变化就会重建）后
        // 用户刚展开的分组又被合上、看到一半的列表跳回顶部
        var expandedState = _hotkeyGroups.ToDictionary(
            g => g.PluginId, g => g.IsExpanded, StringComparer.OrdinalIgnoreCase);

        var sources = _manager.Plugins
            .Where(p => p.Manifest is not null)
            .Select(p => new HotkeyGroupSource
            {
                PluginId = p.Manifest!.Id,
                PluginName = p.Manifest.Name,
                Running = p.Runtime?.State == PluginState.Running,
                Hotkeys = p.Runtime?.Hotkeys ?? [],
            })
            .ToList();

        var groups = HotkeyGroupBuilder.Build(sources);

        _hotkeyGroups = [.. groups.Select(g => new HotkeyGroupViewModel(g, IconFor(g.PluginId))
        {
            // 默认展开；之前手动折叠过的分组维持折叠
            IsExpanded = !expandedState.TryGetValue(g.PluginId, out var expanded) || expanded,
        })];

        ApplyHotkeyFilter();
        UpdateHotkeyConflictBanner();
    }

    /// <summary>
    /// 刷新冲突横幅：把全部小工具的冲突汇总成一句"哪个小工具的哪个键和谁撞了"。
    /// 不展开分组也能看到问题，这是冲突检测的实际价值所在。
    /// </summary>
    private void UpdateHotkeyConflictBanner()
    {
        var conflicts = _hotkeyGroups
            .SelectMany(g => g.AllItems.Select(i => (Group: g, Row: i)))
            .Where(x => x.Row.HasConflict)
            .ToList();

        if (conflicts.Count == 0)
        {
            HotkeyConflictBanner.Visibility = Visibility.Collapsed;
            HotkeyConflictText.Text = "";
            return;
        }

        var lines = conflicts.Select(x => $"· {x.Group.PluginName}「{x.Row.Combination}」{x.Row.ConflictDetail}");

        HotkeyConflictText.Text =
            $"检测到 {conflicts.Count} 个热键冲突（组合键与其他热键重复，需改键才能生效）：\n"
            + string.Join("\n", lines);

        HotkeyConflictBanner.Visibility = Visibility.Visible;
    }

    /// <summary>分组头用的插件图标；插件已被卸载时退回通用占位图标。</summary>
    private System.Windows.Media.ImageSource IconFor(string pluginId)
    {
        var manifest = _manager.Plugins
            .FirstOrDefault(p => string.Equals(p.Manifest?.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            ?.Manifest;

        return PluginIcons.ForManifest(manifest);
    }

    /// <summary>按筛选框关键字过滤分组；关键字为空则显示全部。</summary>
    private void ApplyHotkeyFilter()
    {
        var keyword = HotkeyFilterBox.Text;
        var searching = !string.IsNullOrWhiteSpace(keyword);

        // 筛选只收窄每组要显示的行，不重建分组对象：
        // 重建会把用户手动折叠的状态一起冲掉，搜索框里每敲一个字都会跳一下
        var filtered = _hotkeyGroups.Where(g => g.ApplyFilter(keyword)).ToList();

        // 搜索时一律展开：用户既然主动搜了，就是想直接看到命中的行
        if (searching)
            foreach (var group in filtered) group.IsExpanded = true;

        HotkeyGroupList.ItemsSource = filtered;

        HotkeyFilterWatermark.Visibility = string.IsNullOrEmpty(HotkeyFilterBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        NoHotkeyHint.Text = _hotkeyGroups.Count == 0
            ? "当前没有小工具声明全局热键。"
            : "没有匹配的热键——换个关键字，或清空筛选框。";
        NoHotkeyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHotkeyFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyHotkeyFilter();

    private void OnExpandAllHotkeys(object sender, RoutedEventArgs e) => SetAllHotkeyGroupsExpanded(true);

    private void OnCollapseAllHotkeys(object sender, RoutedEventArgs e) => SetAllHotkeyGroupsExpanded(false);

    /// <summary>
    /// 全部展开 / 全部折叠。
    /// 只改 _hotkeyGroups 即可：筛选不重建视图模型，界面上显示的始终是它的子集，
    /// 改完这些对象，已过滤掉的分组下次出现时也自然是同一状态。
    /// </summary>
    private void SetAllHotkeyGroupsExpanded(bool expanded)
    {
        foreach (var group in _hotkeyGroups) group.IsExpanded = expanded;
    }

    // ---- 插件操作 ----

    /// <summary>切换启用状态；返回错误文本（成功返回 null）。</summary>
    private string? OnTogglePlugin(PluginItemViewModel vm, bool enabled)
    {
        var plugin = FindPlugin(vm);
        if (plugin is null) return "插件已不存在，请点击刷新。";

        if (plugin.Manifest is null)
            return "插件清单损坏，无法启用。请卸载后重新安装。";

        if (enabled)
        {
            // 用户重新启用时清零重启计数，给一次干净的重试机会
            plugin.Runtime?.Entry.ResetCrashState();
            _registry.Save();
        }

        _manager.SetEnabled(plugin, enabled);
        Dispatcher.BeginInvoke(Rebuild);

        if (!enabled || plugin.Runtime is null) return null;

        // 启动是异步的：此刻若已判定失败，直接把原因反馈给界面
        return plugin.Runtime.State is PluginState.Faulted or PluginState.Crashed
            ? plugin.Runtime.LastError ?? "插件启动失败"
            : null;
    }

    private void RestartPlugin(PluginItemViewModel vm)
    {
        var plugin = FindPlugin(vm);
        if (plugin?.Runtime is null) return;

        plugin.Runtime.Entry.ResetCrashState();
        _registry.Save();
        plugin.Runtime.Restart();
        FooterHint.Text = $"正在重启 {plugin.Manifest?.Name ?? vm.Id}…";
    }

    private (bool Ok, string? Error) OnUninstallPlugin(PluginItemViewModel vm, bool deleteUserData)
    {
        var plugin = FindPlugin(vm);
        if (plugin is null) return (false, "插件已不存在，请点击刷新。");

        if (!_manager.Uninstall(plugin, deleteUserData, out var error))
            return (false, error);

        Dispatcher.BeginInvoke(Rebuild);
        return (true, null);
    }

    private DiscoveredPlugin? FindPlugin(PluginItemViewModel vm)
        => _manager.Plugins.FirstOrDefault(p =>
            string.Equals(p.Manifest?.Id ?? Path.GetFileName(p.Directory.TrimEnd(Path.DirectorySeparatorChar)), vm.Id,
                StringComparison.OrdinalIgnoreCase));

    // ---- 界面事件 ----

    private void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PluginItemViewModel vm) RestartPlugin(vm);
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginItemViewModel vm) return;

        var name = vm.Name;
        var choice = UninstallDialog.Ask(this, name, vm.IsBroken);
        if (choice is null) return;

        var (ok, error) = vm.Uninstall(choice.Value.DeleteUserData);
        if (!ok)
        {
            MessageBox.Show(this, $"卸载失败：{error}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FooterHint.Text = choice.Value.DeleteUserData
            ? $"已卸载 {name}，并删除其数据。"
            : $"已卸载 {name}（保留了它的数据，重新安装后可继续使用）。";
    }

    /// <summary>
    /// 页头的「设置…」按钮：与托盘菜单的「设置…」是同一个入口，统一交给 App 管理，
    /// 已打开时由它把窗口激活，不会点一次多出一个设置窗口。
    /// </summary>
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
        => (Application.Current as App)?.OpenSettingsCenter();

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择小工具插件目录中的 plugin.json，或 .zip 插件包",
            Filter = "插件清单或插件包 (*.json;*.zip)|*.json;*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var source = dialog.FileName;

        // 选中 plugin.json 时安装它所在目录，符合"选目录"的直觉
        if (Path.GetFileName(source).Equals(Protocol.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            source = Path.GetDirectoryName(source) ?? source;

        if (!_manager.Install(source, out var pluginId, out var error))
        {
            MessageBox.Show(this, $"安装失败：{error}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _manager.Discover();
        _manager.StartEnabled();
        Rebuild();
        FooterHint.Text = $"已安装小工具 {pluginId}。";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _manager.Discover();
        _manager.StartEnabled();
        Rebuild();
    }

    private void OnStartAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var plugin in _manager.Plugins.Where(p => p.Manifest is not null))
            if (_registry.Find(plugin.Manifest!.Id)?.Enabled == true)
                plugin.Runtime?.Start();
        Rebuild();
    }

    private void OnStopAllClick(object sender, RoutedEventArgs e)
    {
        _manager.StopAll();
        Rebuild();
    }

    private void OnOpenPluginsDir(object sender, RoutedEventArgs e) => OpenPath(HostPaths.PluginsDirectory);
    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenPath(HostPaths.DataDirectory);
    private void OnOpenLogDir(object sender, RoutedEventArgs e) => OpenPath(HostPaths.LogDirectory);

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HostLog.Write($"打开目录失败（{path}）: {ex.Message}");
        }
    }
}