using System.IO;

namespace OneBox.Host.Services;

/// <summary>带轮转的轻量日志（宿主与插件共用），写入 %LOCALAPPDATA%\OneBox\logs。</summary>
public static class HostLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();

    /// <summary>写宿主日志。</summary>
    public static void Write(string message) => WriteTo(HostPaths.HostLogPath, "HOST", message);

    /// <summary>写指定插件的日志。</summary>
    public static void WritePlugin(string pluginId, string level, string message)
        => WriteTo(HostPaths.PluginLogPath(pluginId), pluginId, $"[{level}] {message}");

    /// <summary>写入指定文件，超过上限时轮转为 .1 备份。</summary>
    public static void WriteTo(string path, string tag, string message)
    {
        try
        {
            lock (Sync)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var backup = path + ".1";
                    File.Delete(backup);
                    File.Move(path, backup);
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
