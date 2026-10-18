using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneBox.Abstractions.Theming;

namespace OneBox.Host.Services;

/// <summary>单个插件的宿主侧状态。</summary>
public sealed class PluginRegistryEntry
{
    /// <summary>插件 id（与清单一致）。</summary>
    public string Id { get; set; } = "";

    /// <summary>用户是否启用该插件。默认启用，禁用后宿主不启动其进程。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>插件目录来源："bundled"（随宿主分发）或 "installed"（用户安装）。</summary>
    public string Source { get; set; } = PluginSource.Installed;

    /// <summary>首次发现时间。</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.Now;

    /// <summary>最近一次启动时间。</summary>
    public DateTime? LastStartedAt { get; set; }

    /// <summary>连续崩溃次数；成功运行一段时间后清零。</summary>
    public int CrashCount { get; set; }

    /// <summary>累计崩溃次数（仅展示）。</summary>
    public int TotalCrashes { get; set; }

    /// <summary>上次崩溃原因（展示用）。</summary>
    public string? LastCrashReason { get; set; }

    /// <summary>用户重新启用或手动重启时清零崩溃状态，给一次干净的重试机会。</summary>
    public void ResetCrashState()
    {
        CrashCount = 0;
        TotalCrashes = 0;
        LastCrashReason = null;
    }
}

/// <summary>插件来源取值。</summary>
public static class PluginSource
{
    public const string Bundled = "bundled";
    public const string Installed = "installed";
}

/// <summary>宿主设置。</summary>
public sealed class HostSettings
{
    /// <summary>插件异常退出后是否自动重启。</summary>
    public bool AutoRestartPlugins { get; set; } = true;

    /// <summary>同一插件的最大连续自动重启次数（超过后停止重启并提示）。</summary>
    public int MaxRestartAttempts { get; set; } = 3;

    /// <summary>插件成功运行超过该秒数后清零崩溃计数。</summary>
    public int CrashResetAfterSeconds { get; set; } = 60;

    /// <summary>是否开机自启动宿主。</summary>
    public bool AutoStart { get; set; }

    /// <summary>工具箱窗口是否在启动时显示。</summary>
    public bool ShowToolboxOnStartup { get; set; }

    /// <summary>
    /// 是否把「OneBox 小工具」写进资源管理器的右键菜单（桌面与文件夹空白处）。
    /// 打开后右键就能直接开工具箱、设置或某个小工具的窗口，不必先去找托盘图标。
    /// </summary>
    public bool AddToContextMenu { get; set; }

    /// <summary>界面主题 id（见 <see cref="ThemeCatalog.All"/>）；未知值回退默认主题。</summary>
    public string Theme { get; set; } = ThemeCatalog.DefaultId;

    /// <summary>
    /// 关闭窗口时的行为（取值见 <see cref="CloseWindowActions"/>）。
    /// 为 null 表示还没问过用户：首次关闭窗口会弹一次快捷确认，结果记在这里，之后不再打扰。
    /// </summary>
    public string? CloseWindowAction { get; set; }

    /// <summary>
    /// 用户自定义的全局热键覆盖：键为 "&lt;插件id&gt;|&lt;动作id&gt;"，值为组合键文本。
    /// 没改过的动作不在此表中，插件声明的默认键照常生效。
    /// </summary>
    public Dictionary<string, string>? HotkeyOverrides { get; set; }
}

/// <summary>关闭窗口时的行为取值。</summary>
public static class CloseWindowActions
{
    /// <summary>只关窗口，OneBox 继续留在托盘（默认，也是没做过选择时的行为）。</summary>
    public const string CloseWindowOnly = "closeWindowOnly";

    /// <summary>关闭窗口的同时退出 OneBox。</summary>
    public const string ExitApp = "exitApp";

    /// <summary>
    /// 把设置里的值解释成确定的取值：null（还没问过）或不认识的值一律按默认的"只关窗口"。
    /// 用户手改配置文件、或旧版本残留了别的取值，都不该让窗口关不掉或意外退出。
    /// </summary>
    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), ExitApp, StringComparison.OrdinalIgnoreCase) ? ExitApp : CloseWindowOnly;

    /// <summary>按设置，关闭窗口时是否要顺带退出宿主。</summary>
    public static bool ShouldExit(string? value)
        => string.Equals(Normalize(value), ExitApp, StringComparison.Ordinal);
}

/// <summary>插件注册表持久化（plugins.json + settings.json）。</summary>
public sealed class PluginRegistry
{
    private readonly List<PluginRegistryEntry> _entries;
    private readonly string _path;

    public HostSettings Settings { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private PluginRegistry(List<PluginRegistryEntry> entries, HostSettings settings, string path)
    {
        _entries = entries;
        Settings = settings;
        _path = path;
    }

    /// <summary>插件状态快照。</summary>
    public IReadOnlyList<PluginRegistryEntry> Entries => _entries;

    public static PluginRegistry Load(string? path = null)
    {
        path ??= HostPaths.RegistryPath;
        List<PluginRegistryEntry> entries = [];
        HostSettings settings = new();

        try
        {
            if (File.Exists(path))
            {
                var payload = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(path), JsonOptions);
                if (payload is not null)
                {
                    entries = payload.Plugins ?? [];
                    settings = payload.Settings ?? new HostSettings();
                }
            }
        }
        catch (Exception ex)
        {
            HostLog.Write($"插件注册表读取失败（使用默认值）: {ex.Message}");
        }

        return new PluginRegistry(entries, settings, path);
    }

    /// <summary>按键取条目；不存在时按来源创建并加入。</summary>
    public PluginRegistryEntry GetOrAdd(string id, string source)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new PluginRegistryEntry { Id = id, Source = source };
            _entries.Add(entry);
        }
        else
        {
            // 插件随宿主分发状态可能变化（如内置插件被安装版覆盖），以本次发现为准
            entry.Source = source;
        }
        return entry;
    }

    public PluginRegistryEntry? Find(string id)
        => _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>移除条目（卸载插件时调用）。</summary>
    public bool Remove(string id)
    {
        var entry = Find(id);
        return entry is not null && _entries.Remove(entry);
    }

    public void Save()
    {
        try
        {
            var payload = new RegistryFile { Plugins = _entries, Settings = Settings };
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            HostLog.Write($"插件注册表保存失败: {ex.Message}");
        }
    }

    private sealed class RegistryFile
    {
        public List<PluginRegistryEntry>? Plugins { get; set; }
        public HostSettings? Settings { get; set; }
    }
}