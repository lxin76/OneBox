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

    /// <summary>改键录制器：同一时刻只应有一行在录制。</summary>
    private readonly HotkeyRecorderService _hotkeyRecorder = new();

    /// <summary>正在录制的那一行（切换行或取消时用来复位）。</summary>
    private HotkeyRowViewModel? _recordingRow;

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

            // 窗口关掉时必须卸掉键盘钩子，否则钩子会一直挂着、占住输入
            CancelRecording();
            _hotkeyRecorder.Dispose();
        };
    }

    private void OnPluginStatusChanged(PluginRuntime runtime, string text) => Rebuild();

    public ToolboxViewModel ViewModel { get; }

    private void OnManagerChanged() => Dispatcher.BeginInvoke(Rebuild);

    /// <summary>从管理器状态重建列表（数量很小，整体重建最不易出错）。</summary>
    public void Rebuild()
    {
        // 重建会换掉全部热键行；正在录制的行随之消失，必须先停掉钩子
        CancelRecording();

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

    // ---- 自定义热键 ----

    /// <summary>
    /// 点「改键」：开始录制这一行。再点一次同一行则取消。
    ///
    /// 录制用低级键盘钩子而不是窗口按键事件：已被其他软件占用的组合键
    /// 会被系统直接吞掉、送不到窗口，而那恰恰是用户最该看到提示的情形。
    /// </summary>
    private void OnRecordHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HotkeyRowViewModel row }) return;

        // 同一行再点一次（此时按钮显示为「取消」）= 取消录制
        if (ReferenceEquals(_recordingRow, row))
        {
            CancelRecording();
            return;
        }

        CancelRecording();

        // 先把全局热键停掉，再开始录制：顺序反过来会有一个窗口期，
        // 用户在这期间按下的键仍可能触发插件功能
        _manager.SuspendHotkeys();

        _recordingRow = row;
        row.EditMessage = null;
        row.IsRecording = true;

        _hotkeyRecorder.Captured -= OnHotkeyCaptured;
        _hotkeyRecorder.Cancelled -= OnRecordingCancelled;
        _hotkeyRecorder.Captured += OnHotkeyCaptured;
        _hotkeyRecorder.Cancelled += OnRecordingCancelled;

        if (!_hotkeyRecorder.Start())
        {
            CancelRecording();
            row.EditMessageIsError = true;
            row.EditMessage = "无法启动按键录制，请重试";
        }
    }

    private void OnRecordingCancelled() => CancelRecording();

    /// <summary>复位录制状态（取消、捕获完成或窗口关闭时调用）。</summary>
    private void CancelRecording()
    {
        _hotkeyRecorder.Stop();
        _hotkeyRecorder.Captured -= OnHotkeyCaptured;
        _hotkeyRecorder.Cancelled -= OnRecordingCancelled;

        if (_recordingRow is not null)
        {
            _recordingRow.IsRecording = false;
            _recordingRow = null;
        }

        // 无论成功、取消还是异常退出，都必须把热键恢复回去，
        // 否则用户会发现"改了一次键之后所有快捷键都失灵了"
        _manager.ResumeHotkeys();
    }

    /// <summary>
    /// 捕获到组合键：校验后保存并重新注册。
    ///
    /// 两类冲突的处置刻意不同：
    /// · 平台内部重复 —— 必定保存不了，先改掉才能用，直接拒绝并说明和谁撞了；
    /// · 被其他软件占用 —— **只强提示、不阻止**。两个软件未必同时运行，
    ///   错开使用就没事，用户比探测更清楚自己的用法。
    /// </summary>
    private void OnHotkeyCaptured(string combination)
    {
        var row = _recordingRow;
        CancelRecording();
        if (row is null) return;

        var check = _manager.CheckHotkeyChange(row.PluginId, row.ActionId, combination);

        if (!check.CanApply)
        {
            // 拒绝原因既写在行内，也落到窗口底部：行内提示在长列表里容易被滚出视野，
            // 用户按下键后看到的只有"录制结束、什么也没发生"，就会以为功能失灵
            row.EditMessageIsError = true;
            row.EditMessage = check.Error;
            FooterHint.Text = $"未保存：{check.Error}";
            return;
        }

        var error = _manager.SetHotkey(row.PluginId, row.ActionId, combination);
        if (error is not null)
        {
            row.EditMessageIsError = true;
            row.EditMessage = error;
            FooterHint.Text = $"未保存：{error}";
            return;
        }

        FooterHint.Text = $"已设置 {combination}";

        Dispatcher.BeginInvoke(() =>
        {
            Rebuild();

            // 外部冲突时，重建后仍要把醒目提示留在这一行上：
            // 否则用户只看到"已保存"，下次与那个软件同时运行时会一头雾水
            if (check.HasExternalConflict)
                ShowExternalNotice(row.PluginId, row.ActionId, $"已保存，但{check.ExternalConflictMessage}");
            else
                ShowSavedHint(row.PluginId, row.ActionId, $"已保存：{combination}");
        });
    }

    /// <summary>
    /// 「检查与其他软件冲突」：逐个试探当前热键是否已被别的进程注册。
    ///
    /// 只提示、不改任何设置：被占用往往只是"同时运行才冲突"，
    /// 用户需要的是知情，而不是被程序替他改键。
    /// </summary>
    private void OnCheckExternalConflicts(object sender, RoutedEventArgs e)
    {
        CancelRecording();

        var occupied = _manager.FindExternalConflicts();
        var rows = _hotkeyGroups.SelectMany(g => g.AllItems).ToList();

        foreach (var row in rows)
            row.ExternalNotice = occupied.TryGetValue(row.Combination, out var message) ? message : null;

        var count = rows.Count(r => r.HasExternalNotice);
        FooterHint.Text = count == 0
            ? "检查完成：当前热键没有与其他软件冲突。"
            : $"检查完成：{count} 个热键与其他软件冲突（仅提示，不影响使用；若与该软件不同时运行则无妨）。";
    }

    /// <summary>在指定行上写外部冲突的醒目提示。</summary>
    private void ShowExternalNotice(string pluginId, string actionId, string message)
    {
        var target = FindRow(pluginId, actionId);
        if (target is not null) target.ExternalNotice = message;
    }

    /// <summary>按插件与动作定位当前界面上的热键行（重建会换掉视图模型）。</summary>
    private HotkeyRowViewModel? FindRow(string pluginId, string actionId)
        => _hotkeyGroups
            .SelectMany(g => g.AllItems)
            .FirstOrDefault(i =>
                string.Equals(i.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.ActionId, actionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>点「恢复默认」：删掉覆盖项并重新注册。</summary>
    private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HotkeyRowViewModel row }) return;

        CancelRecording();
        _manager.ResetHotkey(row.PluginId, row.ActionId);

        Dispatcher.BeginInvoke(() =>
        {
            Rebuild();
            ShowSavedHint(row.PluginId, row.ActionId, $"已恢复默认：{row.DefaultCombination}");
        });
    }

    /// <summary>重建后在对应行上写一条成功提示（重建会换掉视图模型，所以按 id 重新定位）。</summary>
    private void ShowSavedHint(string pluginId, string actionId, string message)
    {
        var row = FindRow(pluginId, actionId);
        if (row is null) return;

        row.EditMessageIsError = false;
        row.EditMessage = message;
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