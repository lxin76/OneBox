using OneBox.Abstractions.Logging;

namespace OneBox.Plugin.Sdk.Logging;

/// <summary>
/// 插件日志入口：每个插件只有一个 data/logs/plugin-&lt;id&gt;.log，10 MB 上限，
/// 写满后覆盖最旧内容。由插件在拿到宿主下发的 <see cref="PluginContext.LogDirectory"/>
/// 后调用 <see cref="Initialize"/>；未初始化前的写入直接丢弃，不会另建第二个文件。
/// </summary>
public static class PluginLog
{
    private static readonly object Sync = new();
    private static LogFile? _file;
    private static string _tag = "plugin";

    /// <summary>绑定宿主下发的日志目录；重复调用以最后一次为准。</summary>
    public static void Initialize(string logDirectory, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || string.IsNullOrWhiteSpace(pluginId)) return;
        lock (Sync)
        {
            _tag = pluginId;
            _file = new LogFile(LogPaths.PluginLogFile(logDirectory, pluginId));
        }
    }

    /// <summary>写一条插件日志；写入失败时静默。</summary>
    public static void Write(string message)
    {
        LogFile? file;
        string tag;
        lock (Sync)
        {
            file = _file;
            tag = _tag;
        }
        file?.Write(tag, message);
    }
}
