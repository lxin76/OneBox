using System.Collections.Concurrent;
using OneBox.Abstractions.Logging;

namespace OneBox.Host.Services;

/// <summary>
/// 宿主日志：单文件、10 MB 上限，写满后覆盖最旧内容（见 <see cref="LogFile"/>）。
/// 插件自身的日志由插件进程写 data/logs/plugin-&lt;id&gt;.log；
/// 这里只记录宿主事件，以及宿主观察到的插件生命周期事件（带 plugin:&lt;id&gt; 标签）。
/// </summary>
public static class HostLog
{
    private static readonly ConcurrentDictionary<string, LogFile> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>写宿主日志（host.log）。</summary>
    public static void Write(string message) => For(HostPaths.HostLogPath).Write("HOST", message);

    /// <summary>把宿主观察到的插件事件写进 host.log，标记插件 id 与级别。</summary>
    public static void WritePlugin(string pluginId, string level, string message)
        => Write($"[plugin:{pluginId}] [{level}] {message}");

    private static LogFile For(string path) => Files.GetOrAdd(path, static p => new LogFile(p));
}
