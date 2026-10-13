using System.IO;
using OneBox.Abstractions;

namespace OneBox.Tests;

/// <summary>IPC 帧协议：宿主与插件共用这一层，收发与请求应答必须可靠。</summary>
public class IpcConnectionTests
{
    /// <summary>在内存里对接两端的双向流（各自读对方写入的数据）。</summary>
    private static (IpcConnection A, IpcConnection B) CreatePair()
    {
        var (ab, ba) = InMemoryDuplexStream.CreatePair();

        var a = new IpcConnection(ab);
        var b = new IpcConnection(ba);
        a.Start();
        b.Start();
        return (a, b);
    }

    [Fact]
    public async Task Send_IsReceivedOnOtherSide()
    {
        var (a, b) = CreatePair();
        await using var connA = a;
        await using var connB = b;

        var received = new TaskCompletionSource<IpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageReceived += envelope => received.TrySetResult(envelope);

        await a.SendAsync(IpcEnvelope.Create(IpcTypes.Status, new StatusPayload { Text = "运行中" }));

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IpcTypes.Status, envelope.Type);
        Assert.Equal("运行中", envelope.GetPayload<StatusPayload>()!.Text);
    }

    [Fact]
    public async Task Request_GetsMatchingResponse()
    {
        var (plugin, host) = CreatePair();
        await using var connPlugin = plugin;
        await using var connHost = host;

        // 模拟宿主：收到请求后应答
        host.MessageReceived += envelope => _ = host.RespondAsync(
            envelope.RequestId, true, null, new StatusPayload { Text = "pong" });

        var response = await plugin.RequestAsync(
            IpcEnvelope.Create(IpcTypes.Status, new StatusPayload { Text = "ping" }),
            TimeSpan.FromSeconds(5));

        Assert.Equal(IpcTypes.Ok, response.Type);
        Assert.True(response.GetPayload<ResultPayload>()!.Ok);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsWithMessage()
    {
        var (plugin, host) = CreatePair();
        await using var connPlugin = plugin;
        await using var connHost = host;

        host.MessageReceived += envelope => _ = host.RespondAsync(envelope.RequestId, false, "热键被占用");

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.RequestAsync(
            IpcEnvelope.Create(IpcTypes.Status, new StatusPayload { Text = "ping" }),
            TimeSpan.FromSeconds(5)));

        Assert.Contains("热键被占用", ex.Message);
    }

    [Fact]
    public async Task Request_WithoutResponse_TimesOut()
    {
        var (plugin, host) = CreatePair();
        await using var connPlugin = plugin;
        await using var connHost = host;

        // 宿主故意不应答
        await Assert.ThrowsAsync<TimeoutException>(() => plugin.RequestAsync(
            IpcEnvelope.Create(IpcTypes.Status, new StatusPayload { Text = "ping" }),
            TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task MultipleConcurrentRequests_AreMatchedIndependently()
    {
        var (plugin, host) = CreatePair();
        await using var connPlugin = plugin;
        await using var connHost = host;

        // 逆序应答，验证按 requestId 配对而非按到达顺序
        var pending = new List<IpcEnvelope>();
        host.MessageReceived += envelope =>
        {
            pending.Add(envelope);
            if (pending.Count == 3)
            {
                foreach (var e in pending.AsEnumerable().Reverse())
                    _ = host.RespondAsync(e.RequestId, true);
            }
        };

        var tasks = Enumerable.Range(0, 3)
            .Select(_ => plugin.RequestAsync(
                IpcEnvelope.Empty(IpcTypes.Ping), TimeSpan.FromSeconds(5)))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(IpcTypes.Ok, r.Type));
    }

    [Fact]
    public async Task DisposingPeer_WakesPendingRequests()
    {
        var (plugin, host) = CreatePair();
        await using var connPlugin = plugin;
        var request = plugin.RequestAsync(IpcEnvelope.Empty(IpcTypes.Ping), TimeSpan.FromSeconds(30));

        // 宿主进程消失：等待中的请求必须被唤醒，而不是永久挂起
        await host.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
