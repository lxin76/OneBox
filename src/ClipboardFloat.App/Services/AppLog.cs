using System.IO;

namespace ClipboardFloat.App.Services;

/// <summary>轻量诊断日志（写入 %TEMP%\ClipboardFloat.log），开发期排障用。</summary>
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
                    Path.Combine(Path.GetTempPath(), "ClipboardFloat.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
        }
        catch
        {
            // 日志写入失败时忽略，避免影响主流程
        }
    }
}
