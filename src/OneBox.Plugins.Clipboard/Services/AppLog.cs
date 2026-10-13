using System.IO;
using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Plugins.Clipboard.Services;

/// <summary>
/// 插件本地诊断日志。正常运行时插件日志通过宿主统一落盘（PluginContext.LogAsync），
/// 这里额外写一份本地文件，便于宿主未连接时排障。
/// </summary>
public static class AppLog
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(
                    Path.Combine(AppPaths.DataDirectory, "clipboard.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写入失败时忽略，避免影响主流程
        }
    }
}
