using System.IO;
using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>
/// 宿主级路径。默认是**便携模式**：全部数据放在宿主 exe 同级的 data 目录下，
/// 不经手系统用户目录，整个安装目录拷走即可，卸载直接删目录。
///
/// 目录布局（相对宿主 exe）：
/// <code>
/// OneBox.exe
/// plugins/                  随宿主分发的内置小工具
/// data/plugins/             用户安装的小工具
/// data/plugin-state/&lt;id&gt;/   每个小工具的私有数据
/// data/logs/                宿主与各小工具日志
/// data/plugins.json         启用状态、安装来源、崩溃统计
/// data/settings.json        宿主设置
/// </code>
///
/// 之所以不去 %LOCALAPPDATA%：那会让"程序在哪"和"数据在哪"分离，
/// 卸载时留下残留，多份安装之间还会互相串数据。
/// </summary>
public static class HostPaths
{
    /// <summary>数据目录名（位于宿主 exe 同级）。</summary>
    public const string DataFolderName = "data";

    /// <summary>
    /// 数据目录覆盖。设置后所有宿主路径都改到该目录下，用于单元测试
    /// （否则测试会真的往 exe 旁边写数据）。
    /// </summary>
    public static string? DataDirectoryOverride { get; set; }

    /// <summary>宿主 exe 所在目录：一切相对路径的基准。</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>数据根目录（便携模式：宿主 exe 同级的 data）。</summary>
    public static string DataDirectory
    {
        get
        {
            var dir = DataDirectoryOverride is { Length: > 0 } custom
                ? custom
                : Path.Combine(InstallDirectory, DataFolderName);
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

    /// <summary>插件私有数据根目录：宿主为每个插件分配 plugin-state/&lt;id&gt;。</summary>
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
        => Path.Combine(InstallDirectory, "plugins");

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
