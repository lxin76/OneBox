using System.IO;
using System.IO.Pipes;
using System.Text;

namespace OneBox.Host.Services;

/// <summary>
/// 单实例闸门：同一时间只允许一个宿主在跑。
///
/// 为什么必须要有：宿主的资源都是"独占式"的——托盘图标、全局热键、插件进程，
/// 全部只容得下一份。第二个实例起来后，插件热键会被前一个实例占着，
/// 于是界面上每个热键都显示"已被其他程序占用"，用户看到的是一个功能残缺的 OneBox，
/// 而且两份实例还会往同一个 plugins.json 与同一个 SQLite 里写。
///
/// 做法：以"数据目录"为作用域开一个命名互斥体（换安装目录就能同时跑两份，
/// 这正是便携版该有的行为），并通过命名管道把手上的命令行请求转交给现有实例。
/// </summary>
public static class SingleInstance
{
    /// <summary>互斥体与管道名都基于数据目录，因此多份便携安装互不影响。</summary>
    private static string ScopeId
    {
        get
        {
            // 用数据目录路径算一个稳定的短标识
            var path = Path.GetFullPath(HostPaths.DataDirectory).ToLowerInvariant();
            var hash = 17;
            foreach (var c in path) hash = unchecked(hash * 31 + c);
            return (hash & 0x7FFFFFFF).ToString("X8");
        }
    }

    private static string MutexName => $"OneBox.Host.{ScopeId}";
    private static string PipeName => $"onebox-host-{ScopeId}";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCts;

    /// <summary>
    /// 尝试取得单实例所有权。
    /// 成功返回 true；已有实例在跑时把 <paramref name="request"/> 转交给它并返回 false。
    /// </summary>
    /// <param name="request">要转交给现有实例的请求（如 "toolbox" / "settings"）。</param>
    public static bool TryAcquire(string? request)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (createdNew) return true;

        // 已有实例：把请求递过去，然后本进程直接退出
        Forward(request);
        return false;
    }

    /// <summary>把请求写给现有实例；对方会打开对应窗口。</summary>
    private static void Forward(string? request)
    {
        if (string.IsNullOrEmpty(request)) return;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // 给一个短超时：对方可能正在退出，转交失败也不该让用户干等
            client.Connect(1500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.Write(request);
        }
        catch (Exception ex)
        {
            HostLog.Write($"向现有实例转交请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 开始监听来自后续实例的请求。回调在后台线程触发，调用方自行切回 UI 线程。
    /// </summary>
    public static void StartListening(Action<string> onRequest)
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    var request = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(request)) onRequest(request.Trim());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 单次监听失败不该终止整个循环，否则后续实例就再也转交不进来了
                    HostLog.Write($"单实例监听异常（继续监听）: {ex.Message}");
                }
            }
        }, token);
    }

    /// <summary>退出时释放互斥体，让下一个实例能正常启动。</summary>
    public static void Release()
    {
        try
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _listenerCts = null;

            if (_mutex is not null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        }
        catch
        {
            // 释放失败不影响进程退出
        }
    }
}
