using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OneBox.Abstractions;
using OneBox.Host.Services;

namespace OneBox.Host.ViewModels;

/// <summary>工具箱里的一行：包装插件及其运行时状态，供界面绑定与命令操作。</summary>
public partial class PluginItemViewModel : ObservableObject
{
    private readonly DiscoveredPlugin _plugin;
    private readonly Func<PluginItemViewModel, bool, string?> _onToggle;
    private readonly Func<PluginItemViewModel, bool, (bool Ok, string? Error)> _onUninstall;
    private readonly Action<PluginItemViewModel> _onRestart;

    public PluginItemViewModel(
        DiscoveredPlugin plugin,
        Func<PluginItemViewModel, bool, string?> onToggle,
        Func<PluginItemViewModel, bool, (bool Ok, string? Error)> onUninstall,
        Action<PluginItemViewModel> onRestart)
    {
        _plugin = plugin;
        _onToggle = onToggle;
        _onUninstall = onUninstall;
        _onRestart = onRestart;
        Refresh();
    }

    /// <summary>插件 id（损坏插件回退为目录名）。</summary>
    public string Id => _plugin.Manifest?.Id ?? Path.GetFileName(_plugin.Directory.TrimEnd(Path.DirectorySeparatorChar));

    public string Name => _plugin.Manifest?.Name ?? Id;

    public string Description => _plugin.Manifest?.Description
        ?? $"插件清单无法解析：{_plugin.Error}";

    public string VersionText => _plugin.Manifest is null ? "—" : $"v{_plugin.Manifest.Version}";

    /// <summary>是否随宿主分发的内置插件（内置插件不可卸载）。</summary>
    public bool IsBundled => _plugin.Source == PluginSource.Bundled;

    /// <summary>清单损坏：不能启用，只能卸载或忽略。</summary>
    public bool IsBroken => _plugin.Manifest is null;

    public string SourceText => IsBundled ? "内置" : "已安装";

    public string DirectoryPath => _plugin.Directory;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _stateText = "";
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;

            var error = _onToggle(this, value);
            if (error is not null)
            {
                // 启用失败时回滚开关，避免界面与真实状态不一致
                _enabled = !value;
                OnPropertyChanged(nameof(Enabled));
                ErrorText = error;
            }
            else
            {
                ErrorText = null;
            }
        }
    }

    private string? _errorText;
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>本插件占用的热键文本（含冲突标记）。</summary>
    public string HotkeyText
    {
        get
        {
            var bindings = _plugin.Runtime?.Hotkeys;
            if (bindings is null || bindings.Count == 0) return "无";

            return string.Join("    ", bindings.Select(b =>
                b.Registered ? b.Combination : $"{b.Combination}（未生效：{b.Error}）"));
        }
    }

    /// <summary>崩溃历史文本。</summary>
    public string CrashText
    {
        get
        {
            var entry = _plugin.Runtime?.Entry;
            if (entry is null || entry.TotalCrashes == 0) return "";
            return $"崩溃 {entry.TotalCrashes} 次" + (entry.LastCrashReason is null ? "" : $"（最近：{entry.LastCrashReason}）");
        }
    }

    public bool HasCrashInfo => !string.IsNullOrEmpty(CrashText);

    /// <summary>运行时把最新状态同步到界面。</summary>
    public void Refresh()
    {
        var runtime = _plugin.Runtime;

        if (IsBroken)
        {
            StateText = "清单损坏";
            StatusText = _plugin.Error ?? "";
            _enabled = false;
            ErrorText = _plugin.Error;
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(HotkeyText));
            OnPropertyChanged(nameof(CrashText));
            OnPropertyChanged(nameof(HasCrashInfo));
            return;
        }

        _enabled = runtime?.Entry.Enabled ?? true;
        OnPropertyChanged(nameof(Enabled));

        var state = runtime?.State ?? PluginState.Stopped;
        StateText = state switch
        {
            PluginState.Disabled => "已禁用",
            PluginState.Starting => "启动中…",
            PluginState.Running => "运行中",
            PluginState.Stopped => "已停止",
            PluginState.Crashed => "已崩溃，等待重启",
            PluginState.Faulted => "启动失败",
            _ => state.ToString(),
        };

        StatusText = runtime?.StatusText ?? "";
        ErrorText = runtime?.State is PluginState.Crashed or PluginState.Faulted ? runtime.LastError : null;

        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(CrashText));
        OnPropertyChanged(nameof(HasCrashInfo));
    }

    /// <summary>是否显示"重启"按钮（运行中或崩溃后可用）。</summary>
    public bool CanRestart => !IsBroken && Enabled;

    /// <summary>是否显示"卸载"按钮。</summary>
    public bool CanUninstall => !IsBundled;

    public void RequestRestart() => _onRestart(this);

    /// <summary>执行卸载（由界面确认后调用）。</summary>
    public (bool Ok, string? Error) Uninstall(bool deleteData) => _onUninstall(this, deleteData);
}

/// <summary>工具箱窗口的数据模型。</summary>
public sealed class ToolboxViewModel : ObservableObject
{
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = new();

    public string PluginsDirectory => HostPaths.PluginsDirectory;
    public string DataDirectory => HostPaths.DataDirectory;
    public string LogDirectory => HostPaths.LogDirectory;
}
