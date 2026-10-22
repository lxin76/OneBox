using System.IO;

namespace OneBox.Abstractions.Logging;

/// <summary>
/// 日志文件名约定。宿主与插件进程必须用同一套命名，否则会出现"插件写 A、用户找 B"。
/// </summary>
public static class LogPaths
{
    /// <summary>宿主日志文件名。</summary>
    public const string HostLogFileName = "host.log";

    /// <summary>插件日志文件名前缀。</summary>
    public const string PluginLogFilePrefix = "plugin-";

    /// <summary>某个插件的唯一日志文件路径。</summary>
    public static string PluginLogFile(string logDirectory, string pluginId)
        => Path.Combine(logDirectory, $"{PluginLogFilePrefix}{pluginId}.log");
}
