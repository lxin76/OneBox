using System.Text.Json;
using OneBox.Abstractions;
using OneBox.Abstractions.Theming;
using OneBox.Plugin.Sdk.Logging;

namespace OneBox.Plugin.Sdk;

/// <summary>
/// 插件与宿主之间的会话。插件通过它声明热键/托盘菜单、读写私有设置、上报状态与日志，
/// 并接收热键按下、菜单点击等来自宿主的事件。
/// </summary>
public sealed class PluginContext : IAsyncDisposable
{
    private readonly IpcConnection _connection;
    private readonly SynchronizationContext? _syncContext;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    internal PluginContext(IpcConnection connection, HelloPayload hello, SynchronizationContext? syncContext = null)
    {
        _connection = connection;
        _syncContext = syncContext;
        PluginId = hello.PluginId;
        HostVersion = hello.HostVersion;
        DataDirectory = hello.DataDirectory;
        LogDirectory = hello.LogDirectory;
        CurrentTheme = hello.Theme ?? ThemeCatalog.Default;
        _connection.MessageReceived += OnMessage;
    }

    /// <summary>
    /// 把回调派发到插件的主线程（WPF 的 UI 线程）。
    /// 宿主消息在线程池线程上到达，直接回调会让插件在非 STA 线程上碰 UI 控件。
    /// </summary>
    private void Dispatch(Action action)
    {
        if (_syncContext is null || _syncContext == SynchronizationContext.Current) action();
        else _syncContext.Post(_ => action(), null);
    }

    /// <summary>插件 id（来自清单）。</summary>
    public string PluginId { get; }

    /// <summary>宿主版本。</summary>
    public string HostVersion { get; }

    /// <summary>宿主为本插件分配的私有数据目录（已创建），卸载插件时保留或随用户选择删除。</summary>
    public string DataDirectory { get; }

    /// <summary>宿主日志目录。</summary>
    public string LogDirectory { get; }

    /// <summary>
    /// 宿主当前的主题调色板。随握手下发，之后由 <see cref="ThemeChanged"/> 更新。
    /// 插件据此给自己的窗口上色（配合 OneBox.Plugin.Sdk.Wpf 的 PluginTheme 可自动跟随）。
    /// </summary>
    public ThemePalette CurrentTheme { get; private set; }

    /// <summary>宿主通知插件停止工作（准备退出）。</summary>
    public CancellationToken Stopping => _stoppingCts.Token;

    private readonly CancellationTokenSource _stoppingCts = new();

    /// <summary>宿主通知某个已声明的热键被按下。</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>用户点击了插件注册的托盘/工具箱菜单项。</summary>
    public event Action<MenuInvokedPayload>? MenuInvoked;

    /// <summary>
    /// 用户在宿主设置中心改动了本插件的设置项。
    /// 插件应把新值应用到内存并自行持久化（插件是设置值的唯一来源）。
    /// </summary>
    public event Action<SettingsChangedPayload>? SettingsChanged;

    /// <summary>
    /// 宿主切换了主题（用户在设置中心改的）。插件应据此立刻给已打开的窗口换色，
    /// 参数就是新的完整调色板——直接覆盖上一次的即可，不需要自己维护主题目录。
    /// </summary>
    public event Action<ThemePalette>? ThemeChanged;

    /// <summary>
    /// 宿主重新注册了本插件的热键（用户改键、或注册结果发生变化）。
    ///
    /// 插件应当据此更新所有"给用户看的键位文案"（窗口提示、状态栏、托盘菜单说明等），
    /// 否则用户改完键会发现界面里还写着旧键位，与实际行为对不上。
    /// 参数是本插件全部热键的最新结果，按 ActionId 即可取到自己关心的那一项。
    /// </summary>
    public event Action<IReadOnlyList<HotkeyRegistrationResult>>? HotkeysChanged;

    /// <summary>与宿主的连接关闭（宿主退出或崩溃）。</summary>
    public event Action<Exception?>? ConnectionClosed;

    /// <summary>宿主通知插件退出（收到后应尽快收尾并让主体返回）。</summary>
    public event Action<string>? ShutdownRequested;

    internal void RaiseConnectionClosed(Exception? error)
    {
        if (!_stoppingCts.IsCancellationRequested) _stoppingCts.Cancel();
        Dispatch(() => ConnectionClosed?.Invoke(error));
    }

    private void OnMessage(IpcEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case IpcTypes.Start:
                break;

            case IpcTypes.HotkeyPressed:
                if (envelope.GetPayload<HotkeyPressedPayload>() is { } hp)
                    Dispatch(() => HotkeyPressed?.Invoke(hp.ActionId));
                break;

            case IpcTypes.HotkeysChanged:
                if (envelope.GetPayload<HotkeysChangedPayload>() is { } hc)
                    Dispatch(() => HotkeysChanged?.Invoke(hc.Results));
                break;

            case IpcTypes.MenuInvoked:
                if (envelope.GetPayload<MenuInvokedPayload>() is { } mi)
                    Dispatch(() => MenuInvoked?.Invoke(mi));
                break;

            case IpcTypes.SettingsChanged:
                if (envelope.GetPayload<SettingsChangedPayload>() is { } sc)
                    Dispatch(() => SettingsChanged?.Invoke(sc));
                break;

            case IpcTypes.ThemeChanged:
                if (envelope.GetPayload<ThemeChangedPayload>() is { } tc && tc.Theme.Colors.Count > 0)
                {
                    CurrentTheme = tc.Theme;
                    Dispatch(() => ThemeChanged?.Invoke(tc.Theme));
                }
                break;

            case IpcTypes.Shutdown:
                var reason = envelope.GetPayload<ShutdownPayload>()?.Reason ?? "宿主退出";
                _stoppingCts.Cancel();
                Dispatch(() => ShutdownRequested?.Invoke(reason));
                break;

            case IpcTypes.Ping:
                _ = _connection.RespondAsync(envelope.RequestId, true);
                break;
        }
    }

    // ---- 上报 ----

    /// <summary>告诉宿主插件已就绪，并给出展示名与状态。</summary>
    public Task ReadyAsync(string? displayName = null, string? statusText = null)
        => PostAsync(IpcTypes.Ready, new ReadyPayload
        {
            DisplayName = displayName,
            StatusText = statusText,
            ProcessId = Environment.ProcessId,
        });

    /// <summary>更新工具箱里显示的状态文本。</summary>
    public Task SetStatusAsync(string text)
        => PostAsync(IpcTypes.Status, new StatusPayload { Text = text });

    /// <summary>
    /// 上报日志给宿主，同时写入本插件的唯一日志文件（data/logs/plugin-&lt;id&gt;.log）。
    /// 宿主不再重复落盘；保留 IPC 上报是为了协议兼容与将来可能的界面展示。
    /// </summary>
    public Task LogAsync(LogLevel level, string message)
    {
        PluginLog.Write($"[{level}] {message}");
        return PostAsync(IpcTypes.Log, new LogPayload { Level = level, Message = message });
    }

    public Task LogDebugAsync(string message) => LogAsync(LogLevel.Debug, message);
    public Task LogInfoAsync(string message) => LogAsync(LogLevel.Info, message);
    public Task LogWarningAsync(string message) => LogAsync(LogLevel.Warning, message);
    public Task LogErrorAsync(string message) => LogAsync(LogLevel.Error, message);

    /// <summary>内部同步日志（不等待发送完成，用于异常路径）。</summary>
    internal void LogInfo(string message)
    {
        PluginLog.Write($"[{LogLevel.Info}] {message}");
        _ = SafePostAsync(IpcTypes.Log, new LogPayload { Level = LogLevel.Info, Message = message });
    }

    /// <summary>请求宿主弹出气泡通知。</summary>
    public Task NotifyAsync(string title, string message, LogLevel level = LogLevel.Info)
        => PostAsync(IpcTypes.Notify, new NotifyPayload { Title = title, Message = message, Level = level });

    // ---- 能力声明 ----

    /// <summary>声明（或更新）插件需要的全局热键。宿主统一注册并做冲突仲裁。</summary>
    public async Task<IReadOnlyList<HotkeyRegistrationResult>> DeclareHotkeysAsync(IEnumerable<HotkeyDeclaration> hotkeys)
    {
        var response = await RequestAsync(IpcTypes.DeclareHotkeys, new DeclareHotkeysPayload { Hotkeys = [.. hotkeys] })
            .ConfigureAwait(false);
        return response.GetPayload<ResultPayload>()?.Data is { } data
            ? data.Deserialize<HotkeyRegistrationPayload>(Protocol.Json)?.Results ?? []
            : [];
    }

    /// <summary>声明（或更新）插件在托盘右键菜单与工具箱里的操作项。</summary>
    public Task DeclareTrayMenuAsync(IEnumerable<TrayMenuItem> items)
        => RequestAsync(IpcTypes.DeclareTrayMenu, new DeclareTrayMenuPayload { Items = [.. items] });

    /// <summary>
    /// 声明（或更新）插件在宿主「设置中心」里暴露的设置项。
    /// 重复声明会整体替换上一次的结果，因此可在设置变化后重新声明以刷新界面上的值。
    /// </summary>
    public Task DeclareSettingsAsync(IEnumerable<SettingDescriptor> items)
        => RequestAsync(IpcTypes.DeclareSettings, new DeclareSettingsPayload { Items = [.. items] });

    // ---- 插件私有设置 ----

    /// <summary>读取插件私有设置；键不存在或类型不匹配时返回 default。</summary>
    public async Task<T?> GetSettingAsync<T>(string key, T? fallback = default)
    {
        var response = await RequestAsync(IpcTypes.GetSetting, new GetSettingPayload { Key = key }).ConfigureAwait(false);
        var payload = response.GetPayload<ResultPayload>()?.Data is { } data
            ? data.Deserialize<SettingValuePayload>(Protocol.Json)
            : null;

        if (payload is null || !payload.Exists || payload.Value is not { } value) return fallback;
        try
        {
            return value.Deserialize<T>(Protocol.Json) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>写入插件私有设置（宿主落地到插件数据目录）。</summary>
    public Task SetSettingAsync<T>(string key, T value)
        => RequestAsync(IpcTypes.SetSetting, new SetSettingPayload
        {
            Key = key,
            Value = JsonSerializer.SerializeToElement(value, Protocol.Json),
        });

    // ---- 生命周期 ----

    /// <summary>请求宿主退出本插件（不会自动重启）。</summary>
    public Task RequestShutdownAsync() => PostAsync(IpcTypes.RequestShutdown, new EmptyPayload());

    /// <summary>请求宿主重启本插件。</summary>
    public Task RequestRestartAsync() => PostAsync(IpcTypes.RequestRestart, new EmptyPayload());

    private async Task PostAsync<T>(string type, T payload)
    {
        try
        {
            await _connection.SendAsync(IpcEnvelope.Create(type, payload)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 宿主已退出：丢弃消息，由 ConnectionClosed 驱动插件收尾
        }
    }

    private async Task SafePostAsync<T>(string type, T payload)
    {
        try
        {
            await PostAsync(type, payload).ConfigureAwait(false);
        }
        catch
        {
            // 日志发送失败时静默
        }
    }

    private Task<IpcEnvelope> RequestAsync<T>(string type, T payload)
        => _connection.RequestAsync(IpcEnvelope.Create(type, payload), RequestTimeout);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
