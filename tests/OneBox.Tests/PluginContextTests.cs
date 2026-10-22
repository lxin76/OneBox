using System.IO;
using System.Text.Json;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;
using OneBox.Plugin.Sdk.Logging;

namespace OneBox.Tests;

/// <summary>
/// 插件侧会话与宿主的通信契约。重点覆盖"热键声明要拿到注册结果"——
/// 宿主若先应答空结果、再异步注册，插件就永远无法提示用户"这个热键被占用了"。
/// </summary>
public class PluginContextTests
{
    /// <summary>构造一对已握手的宿主/插件连接。</summary>
    private static (IpcConnection Host, PluginContext Context) CreateSession()
    {
        var (hostStream, pluginStream) = InMemoryDuplexStream.CreatePair();
        var host = new IpcConnection(hostStream);
        var plugin = new IpcConnection(pluginStream);
        host.Start();
        plugin.Start();

        var hello = new HelloPayload
        {
            PluginId = "demo",
            HostVersion = "1.0.0",
            ProtocolVersion = Protocol.Version,
            DataDirectory = Path.GetTempPath(),
            LogDirectory = Path.GetTempPath(),
        };
        var context = new PluginContext(plugin, hello);
        return (host, context);
    }

    [Fact]
    public async Task LogAsync_WritesToSinglePluginLogFile()
    {
        // 插件只调 _context.LogXxxAsync 时，日志也必须落到自己唯一的文件里，
        // 不能因为宿主不再重复落盘就丢失。
        var directory = Path.Combine(Path.GetTempPath(), $"onebox-plugin-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            PluginLog.Initialize(directory, "demo");

            var (_, context) = CreateSession();
            await using var contextScope = context;

            await context.LogInfoAsync("hello");

            var path = Path.Combine(directory, "plugin-demo.log");
            Assert.True(File.Exists(path));
            Assert.Contains("[Info] hello", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeclareHotkeys_ReturnsResultsFromHostReply()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        // 模拟修好后的宿主：拿到注册结果后再应答
        host.MessageReceived += envelope =>
        {
            if (envelope.Type != IpcTypes.DeclareHotkeys) return;
            _ = host.RespondAsync(envelope.RequestId, true, null, new HotkeyRegistrationPayload
            {
                Results =
                [
                    new HotkeyRegistrationResult
                    {
                        ActionId = "panel",
                        Combination = "Ctrl+Alt+V",
                        Registered = false,
                        Error = "热键 Ctrl+Alt+V 已被其他程序占用",
                    },
                    new HotkeyRegistrationResult
                    {
                        ActionId = "paste",
                        Combination = "Ctrl+Alt+P",
                        Registered = true,
                    },
                ],
            });
        };

        var results = await context.DeclareHotkeysAsync(
        [
            new HotkeyDeclaration { ActionId = "panel", Combination = "Ctrl+Alt+V" },
            new HotkeyDeclaration { ActionId = "paste", Combination = "Ctrl+Alt+P" },
        ]);

        // 插件的设置页与状态文本都依赖这个列表，不能为空
        Assert.Equal(2, results.Count);
        Assert.False(results[0].Registered);
        Assert.Contains("已被其他程序占用", results[0].Error);
        Assert.True(results[1].Registered);
    }

    [Fact]
    public async Task DeclareHotkeys_HostRepliesWithoutData_ReturnsEmpty()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        // 旧行为：只回 ok、不带数据。插件应优雅降级为空列表而不是抛异常
        host.MessageReceived += envelope =>
        {
            if (envelope.Type == IpcTypes.DeclareHotkeys)
                _ = host.RespondAsync(envelope.RequestId, true);
        };

        var results = await context.DeclareHotkeysAsync(
            [new HotkeyDeclaration { ActionId = "a", Combination = "Ctrl+Alt+V" }]);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetSetting_ReturnsValueStoredByHost()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        host.MessageReceived += envelope =>
        {
            if (envelope.Type != IpcTypes.GetSetting) return;
            _ = host.RespondAsync(envelope.RequestId, true, null, new SettingValuePayload
            {
                Exists = true,
                Value = JsonSerializer.SerializeToElement("保存的便签", Protocol.Json),
            });
        };

        var value = await context.GetSettingAsync("note", "");

        Assert.Equal("保存的便签", value);
    }

    [Fact]
    public async Task GetSetting_MissingKey_ReturnsFallback()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        host.MessageReceived += envelope =>
        {
            if (envelope.Type == IpcTypes.GetSetting)
                _ = host.RespondAsync(envelope.RequestId, true, null, new SettingValuePayload { Exists = false });
        };

        var value = await context.GetSettingAsync("note", "默认值");

        Assert.Equal("默认值", value);
    }

    [Fact]
    public async Task GetSetting_TypeMismatch_ReturnsFallback()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        // 盘上是字符串，插件按 int 读：应回退而不是抛 JsonException
        host.MessageReceived += envelope =>
        {
            if (envelope.Type != IpcTypes.GetSetting) return;
            _ = host.RespondAsync(envelope.RequestId, true, null, new SettingValuePayload
            {
                Exists = true,
                Value = JsonSerializer.SerializeToElement("不是数字", Protocol.Json),
            });
        };

        var value = await context.GetSettingAsync("count", 7);

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task HotkeyPressed_IsDeliveredToPlugin()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.HotkeyPressed += actionId => received.TrySetResult(actionId);

        await host.SendAsync(IpcEnvelope.Create(IpcTypes.HotkeyPressed,
            new HotkeyPressedPayload { ActionId = "panel" }));

        Assert.Equal("panel", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MenuInvoked_IsDeliveredToPlugin()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        var received = new TaskCompletionSource<MenuInvokedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.MenuInvoked += payload => received.TrySetResult(payload);

        await host.SendAsync(IpcEnvelope.Create(IpcTypes.MenuInvoked,
            new MenuInvokedPayload { ItemId = "monitor", Checked = true }));

        var invoked = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("monitor", invoked.ItemId);
        Assert.True(invoked.Checked);
    }

    [Fact]
    public async Task Shutdown_CancelsStoppingToken()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        Assert.False(context.Stopping.IsCancellationRequested);

        await host.SendAsync(IpcEnvelope.Create(IpcTypes.Shutdown,
            new ShutdownPayload { Reason = "宿主退出" }));

        // 插件主体用这个令牌结束 Task.Delay(Timeout.Infinite)，必须被触发
        await Task.Delay(50);
        Assert.True(context.Stopping.IsCancellationRequested);
    }

    [Fact]
    public async Task ReadyAndStatusAndLog_ReachHost()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        var seen = new List<IpcEnvelope>();
        var allSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MessageReceived += envelope =>
        {
            lock (seen)
            {
                seen.Add(envelope);
                if (seen.Count >= 3) allSeen.TrySetResult();
            }
        };

        await context.ReadyAsync("示例", "就绪");
        await context.SetStatusAsync("监听中");
        await context.LogInfoAsync("一条日志");

        await allSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (seen)
        {
            Assert.Contains(seen, e => e.Type == IpcTypes.Ready);
            Assert.Contains(seen, e => e.Type == IpcTypes.Status);
            Assert.Contains(seen, e => e.Type == IpcTypes.Log);
            Assert.Equal("监听中",
                seen.First(e => e.Type == IpcTypes.Status).GetPayload<StatusPayload>()!.Text);
        }
    }

    [Fact]
    public async Task DeclareTrayMenu_IsSentToHost()
    {
        var (host, context) = CreateSession();
        await using var contextScope = context;

        var received = new TaskCompletionSource<DeclareTrayMenuPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MessageReceived += envelope =>
        {
            if (envelope.Type != IpcTypes.DeclareTrayMenu) return;
            received.TrySetResult(envelope.GetPayload<DeclareTrayMenuPayload>()!);
            _ = host.RespondAsync(envelope.RequestId, true);
        };

        await context.DeclareTrayMenuAsync(
        [
            new TrayMenuItem { Id = "show", Text = "显示" },
            new TrayMenuItem { Kind = TrayMenuItemKind.Separator },
        ]);

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, payload.Items.Count);
        Assert.Equal(TrayMenuItemKind.Separator, payload.Items[1].Kind);
    }

    [Fact]
    public async Task ConnectionClosed_CancelsStoppingToken()
    {
        var (hostStream, pluginStream) = InMemoryDuplexStream.CreatePair();
        var host = new IpcConnection(hostStream);
        var plugin = new IpcConnection(pluginStream);
        host.Start();
        plugin.Start();

        var context = new PluginContext(plugin, new HelloPayload
        {
            PluginId = "demo",
            HostVersion = "1.0.0",
            ProtocolVersion = Protocol.Version,
        });
        plugin.Closed += error => context.RaiseConnectionClosed(error);
        await using var contextScope = context;

        // 宿主消失：插件必须收到停止信号，否则会作为孤儿进程一直挂着
        await host.DisposeAsync();

        await Task.Delay(100);
        Assert.True(context.Stopping.IsCancellationRequested);
    }
}
