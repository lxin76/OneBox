using System.IO;
using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>宿主级路径（%LOCALAPPDATA%\OneBox）：插件目录、插件私有数据、日志、宿主设置。</summary>
public static class HostPaths
{
    /// <summary>
    /// 数据目录覆盖。设置后所有宿主路径都改到该目录下，用于便携模式与单元测试
    /// （否则测试会污染真实的 %LOCALAPPDATA%\OneBox）。
    /// </summary>
    public static string? DataDirectoryOverride { get; set; }

    public static string DataDirectory
    {
        get
        {
            var dir = DataDirectoryOverride is { Length: > 0 } custom
                ? custom
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OneBox");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>插件安装目录：每个插件一个子目录（内含 plugin.json 与入口文件）。</summary>
    public static string PluginsDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "plugins");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>插件私有数据根目录：宿主为每个插件分配 plugins-data/&lt;id&gt;。</summary>
    public static string PluginDataRoot
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "plugin-state");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>宿主提供的应用内置插件目录（随宿主一起分发的插件，位于宿主 exe 旁的 plugins）。</summary>
    public static string BundledPluginsDirectory
        => Path.Combine(AppContext.BaseDirectory, "plugins");

    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>宿主自身日志文件。</summary>
    public static string HostLogPath => Path.Combine(LogDirectory, "host.log");

    /// <summary>插件日志文件（每个插件一个）。</summary>
    public static string PluginLogPath(string pluginId)
        => Path.Combine(LogDirectory, $"plugin-{pluginId}.log");

    /// <summary>插件私有数据目录（首次访问时创建）。</summary>
    public static string PluginDataDirectory(string pluginId)
    {
        var dir = Path.Combine(PluginDataRoot, pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>插件注册表（启用状态、安装来源、用户自定义热键等）。</summary>
    public static string RegistryPath => Path.Combine(DataDirectory, "plugins.json");

    /// <summary>宿主设置文件。</summary>
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
