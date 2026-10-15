using System.IO;
using System.Windows;
using OneBox.Plugin.Sdk;

namespace OneBox.Plugins.Screenshot;

/// <summary>
/// 截图小工具插件进程入口。宿主以 "--pipe &lt;name&gt; --plugin-id screenshot" 启动本进程，
/// 本入口建立与宿主的连接后，把所有界面与逻辑交给 <see cref="ScreenshotPlugin"/>。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 截图坐标要与屏幕物理像素一一对应，必须在创建任何窗口前声明 DPI 感知
        Services.ScreenCapture.EnableDpiAwareness();

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        var exitCode = 0;

        app.Startup += (_, _) =>
        {
            _ = RunAsync(app, args);
        };

        app.Run();
        return exitCode;

        async Task RunAsync(Application application, string[] processArgs)
        {
            try
            {
                exitCode = await OneBoxPlugin.RunAsync(processArgs, async (context, stoppingToken) =>
                {
                    var plugin = new ScreenshotPlugin(context, application.Dispatcher);
                    await plugin.StartAsync().ConfigureAwait(true);

                    try
                    {
                        // 一直运行到宿主通知退出（或宿主进程关闭连接）
                        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // 宿主要求退出
                    }
                    finally
                    {
                        await plugin.DisposeAsync().ConfigureAwait(true);
                    }
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                exitCode = 1;
            }
            finally
            {
                application.Shutdown();
            }
        }
    }

    /// <summary>连接尚未建立时的兜底日志：写到插件 exe 旁边，不落系统用户目录。</summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "OneBox-screenshot-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响退出
        }
    }
}
