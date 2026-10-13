using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OneBox.Abstractions;
using OneBox.Host.Services;
using OneBox.Host.ViewModels;

namespace OneBox.Host.Windows;

/// <summary>工具箱：列出全部小工具，提供启用/禁用、重启、安装与卸载，以及热键总览。</summary>
public partial class ToolboxWindow : Window
{
    private readonly PluginManager _manager;
    private readonly PluginRegistry _registry;

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
            Rebuild();
        };
        Closed += (_, _) =>
        {
            _manager.Changed -= OnManagerChanged;
            _manager.StatusChanged -= OnPluginStatusChanged;
            _manager.MenuChanged -= Rebuild;
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
        FooterHint.Text = $"插件目录：{HostPaths.PluginsDirectory}";

        if (selectedId is not null)
            PluginList.SelectedItem = ViewModel.Plugins.FirstOrDefault(p => p.Id == selectedId);
    }

    private void UpdateHotkeyList()
    {
        var rows = _manager.AllHotkeys
            .Select(h => new
            {
                h.Combination,
                Description = string.IsNullOrWhiteSpace(h.Description) ? h.ActionId : h.Description,
                Status = h.Registered ? "已生效" : $"未生效：{h.Error}",
            })
            .OrderBy(r => r.Combination, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HotkeyList.ItemsSource = rows;
        NoHotkeyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
