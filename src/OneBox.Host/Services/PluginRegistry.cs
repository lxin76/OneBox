using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
