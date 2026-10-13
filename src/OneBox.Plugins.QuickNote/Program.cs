using System.Windows;
using OneBox.Plugin.Sdk;

namespace OneBox.Plugins.QuickNote;

/// <summary>示例小工具插件进程入口。新插件可按这个文件照搬，只改业务部分。</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;

        app.Startup += (_, _) => _ = RunAsync(app, args);
        app.Run();
        return exitCode;

        async Task RunAsync(Application application, string[] processArgs)
        {
            exitCode = await OneBoxPlugin.RunAsync(processArgs, async (context, stoppingToken) =>
            {
                var plugin = new QuickNotePlugin(context, application.Dispatcher);
                await plugin.StartAsync().ConfigureAwait(true);

                try
                {
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

            application.Shutdown();
        }
    }
}
