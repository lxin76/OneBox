using OneBox.Plugin.Sdk.Logging;

namespace OneBox.Plugin.Sdk;

/// <summary>
/// 小工具插件入口。实现这个基类并由宿主启动后调用 <see cref="RunAsync"/>，
/// 即可获得与宿主的完整连接（热键、托盘菜单、设置、日志、生命周期通知）。
/// </summary>
/// <remarks>
/// 最小插件示例：
/// <code>
/// internal static class Program
/// {
///     private static async Task&lt;int&gt; Main(string[] args)
///         => await OneBoxPlugin.RunAsync(args, (ctx, ct) =&gt;
///         {
///             ctx.LogInfoAsync("示例工具已启动");
///             return Task.CompletedTask;
///         });
/// }
/// </code>
/// 宿主负责启动进程、注入管道名，并在退出或卸载时通知插件收尾。
/// </remarks>
public static class OneBoxPlugin
{
    /// <summary>等待宿主握手的时限；超时说明宿主未按协议启动插件。</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 连接宿主并开始运行插件主体。
    /// </summary>
    /// <param name="args">进程参数（由宿主注入 --pipe 与 --plugin-id）。</param>
    /// <param name="run">插件主体；连接建立后调用，返回即代表插件结束工作。</param>
    /// <returns>进程退出码：0 正常，非 0 表示启动失败。</returns>
    public static async Task<int> RunAsync(
        string[] args,
        Func<PluginContext, CancellationToken, Task> run)
    {
        var pipeName = ReadArgument(args, Abstractions.Protocol.PipeArgument);
        if (string.IsNullOrEmpty(pipeName))
        {
            Console.Error.WriteLine($"缺少参数 {Abstractions.Protocol.PipeArgument}：请由 OneBox 宿主启动本插件。");
            return 2;
        }

        try
        {
            // 捕获调用方（通常是插件的 UI 线程）的同步上下文：
            // SDK 内部一律 ConfigureAwait(false)，但交回插件主体的回调必须回到该上下文，
            // 否则 WPF 控件会在线程池线程上创建并抛 "调用线程必须为 STA"。
            var syncContext = SynchronizationContext.Current;

            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);

            using var connectCts = new CancellationTokenSource(HelloTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            await using var connection = new Abstractions.IpcConnection(pipe);
            connection.Start();

            // 等待宿主 Hello，拿到数据目录等运行环境
            var hello = await WaitForHelloAsync(connection, connectCts.Token).ConfigureAwait(false);
            if (hello is null)
            {
                Console.Error.WriteLine("等待宿主握手超时。");
                return 3;
            }

            await using var context = new PluginContext(connection, hello, syncContext);

            // 每个插件只有一个日志：宿主下发的 LogDirectory 下的 plugin-<id>.log。
            // 在这里绑定，保证插件主体、IPC 上报日志都落到同一个文件。
            PluginLog.Initialize(hello.LogDirectory, hello.PluginId);

            connection.Closed += error => context.RaiseConnectionClosed(error);

            await context.LogInfoAsync($"已连接宿主 {hello.HostVersion}（协议 {hello.ProtocolVersion}）")
                .ConfigureAwait(false);

            await RunOnContextAsync(syncContext, () => run(context, context.Stopping)).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("连接宿主超时。");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"插件运行失败: {ex}");
            return 1;
        }
    }

    private static async Task<Abstractions.HelloPayload?> WaitForHelloAsync(
        Abstractions.IpcConnection connection, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Abstractions.HelloPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMessage(Abstractions.IpcEnvelope envelope)
        {
            if (envelope.Type != Abstractions.IpcTypes.Hello) return;
            if (envelope.GetPayload<Abstractions.HelloPayload>() is { } payload)
                tcs.TrySetResult(payload);
        }

        connection.MessageReceived += OnMessage;
        try
        {
            // Hello 由宿主在连接建立后立即下发；这里等待它，同时响应关闭事件
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            connection.MessageReceived -= OnMessage;
        }
    }

    /// <summary>把插件主体调度回它自己的同步上下文（WPF 的 UI 线程）执行。</summary>
    private static Task RunOnContextAsync(SynchronizationContext? syncContext, Func<Task> action)
    {
        if (syncContext is null || syncContext == SynchronizationContext.Current)
            return action();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        syncContext.Post(async _ =>
        {
            try
            {
                await action().ConfigureAwait(true);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
