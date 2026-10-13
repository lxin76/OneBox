using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>插件运行状态。</summary>
public enum PluginState
{
    /// <summary>已发现但被用户禁用。</summary>
    Disabled,
    /// <summary>正在启动进程。</summary>
    Starting,
    /// <summary>已连接并正常工作。</summary>
    Running,
    /// <summary>已停止（用户主动停止或插件自行退出）。</summary>
    Stopped,
    /// <summary>异常退出，宿主正在等待重启。</summary>
    Crashed,
    /// <summary>连续崩溃次数超限，宿主不再重启。</summary>
    Faulted,
}

/// <summary>插件热键的宿主侧记录。</summary>
public sealed class PluginHotkeyBinding
{
    public string PluginId { get; init; } = "";
    public string ActionId { get; init; } = "";
    public string Combination { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Registered { get; init; }
    public string? Error { get; init; }
}

/// <summary>插件托盘/工具箱菜单项（宿主持有的副本）。</summary>
public sealed class PluginMenuItem
{
    public required string PluginId { get; init; }
    public required TrayMenuItem Item { get; init; }
}

/// <summary>
/// 一个已发现插件的运行时：负责启动子进程、建立命名管道 IPC、转发宿主能力，
/// 并在插件崩溃时按策略自动重启。所有状态变化通过事件通知界面。
/// </summary>
public sealed class PluginRuntime : IAsyncDisposable
{
    private readonly PluginManifest _manifest;
    private readonly PluginRegistryEntry _entry;
    private readonly PluginRegistry _registry;
    private readonly Dispatcher _dispatcher;
    private readonly string _hostVersion;

    private Process? _process;
    private IpcConnection? _connection;
    private CancellationTokenSource? _lifetimeCts;
    /// <summary>插件声明的热键（含用户覆盖后的组合串），由热键管理器读取。</summary>
    private readonly List<HotkeyDeclaration> _declaredHotkeys = [];
    /// <summary>热键管理器回填的实际注册结果。</summary>
    private readonly List<PluginHotkeyBinding> _hotkeys = [];
    private readonly List<TrayMenuItem> _menuItems = [];
    private readonly List<SettingDescriptor> _settings = [];
    private int _restartAttempts;
    private bool _userRequestedStop;
    private bool _disposed;

    /// <summary>本次运行是否已处理过进程退出（防止 Exited 与连接关闭重复调度重启）。</summary>
    private bool _exitHandled;

    /// <summary>
    /// 等待注册结果的 declareHotkeys 请求 id。
    /// 用列表而非单个字段：插件可能连续两次声明（例如快速改键），
    /// 若只保留最后一个，前一个请求会一直等到 15 秒超时。
    /// </summary>
    private readonly List<string> _pendingHotkeyRequests = [];
    private readonly object _pendingHotkeySync = new();

    internal PluginRuntime(
        PluginManifest manifest,
        PluginRegistryEntry entry,
        PluginRegistry registry,
        Dispatcher dispatcher,
        string hostVersion)
    {
        _manifest = manifest;
        _entry = entry;
        _registry = registry;
        _dispatcher = dispatcher;
        _hostVersion = hostVersion;
    }

    public PluginManifest Manifest => _manifest;
    public PluginRegistryEntry Entry => _entry;
    public PluginState State { get; private set; } = PluginState.Stopped;

    /// <summary>插件上报的状态文本（显示在工具箱列表）。</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>最近一次退出/失败原因。</summary>
    public string? LastError { get; private set; }

    /// <summary>插件进程 id（运行中有效）。</summary>
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    /// <summary>本插件声明的热键（含用户覆盖），热键管理器据此注册。</summary>
    public IReadOnlyList<HotkeyDeclaration> DeclaredHotkeys => _declaredHotkeys;

    /// <summary>本插件热键的实际注册结果（由热键管理器回填）。</summary>
    public IReadOnlyList<PluginHotkeyBinding> Hotkeys => _hotkeys;

    /// <summary>本插件注册的托盘菜单项。</summary>
    public IReadOnlyList<TrayMenuItem> MenuItems => _menuItems;

    /// <summary>本插件声明的设置项（供设置中心渲染）。</summary>
    public IReadOnlyList<SettingDescriptor> Settings => _settings;

    // ---- 事件（均在 UI 线程触发） ----

    public event Action<PluginRuntime>? StateChanged;
    /// <summary>插件声明了新的热键集合，宿主需要在热键服务上重新注册。</summary>
    public event Action<PluginRuntime>? HotkeysChanged;
    /// <summary>插件注册的托盘菜单变化。</summary>
    public event Action<PluginRuntime>? MenuChanged;
    /// <summary>插件声明的设置项变化（设置中心需要重建表单）。</summary>
    public event Action<PluginRuntime>? SettingsChanged;
    /// <summary>插件请求弹气泡通知。</summary>
    public event Action<PluginRuntime, NotifyPayload>? NotificationRequested;
    /// <summary>插件上报的动态状态文本。</summary>
    public event Action<PluginRuntime, string>? StatusChanged;

    private void SetState(PluginState state, string? error = null)
    {
        State = state;
        if (error is not null) LastError = error;
        _dispatcher.BeginInvoke(() => StateChanged?.Invoke(this));
    }

    /// <summary>启动插件进程（若已禁用则忽略）。</summary>
    public void Start()
    {
        if (_disposed || !_entry.Enabled || State is PluginState.Running or PluginState.Starting) return;
        _userRequestedStop = false;
        _ = StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        SetState(PluginState.Starting);
        _exitHandled = false;
        _lifetimeCts?.Dispose();
        _lifetimeCts = new CancellationTokenSource();
        var ct = _lifetimeCts.Token;

        var pipeName = Protocol.CreatePipeName(_manifest.Id);
        NamedPipeServerStream? server = null;
        try
        {
            // 不能用 await using：管道流要交给 IpcConnection 活到插件结束，
            // 方法返回即释放会让刚建立的连接立刻断开（Pipe is broken）。
            server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var entryPath = Path.GetFullPath(Path.Combine(_manifest.Directory, _manifest.Entry));
            var startInfo = new ProcessStartInfo(entryPath)
            {
                WorkingDirectory = _manifest.Directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 捕获插件控制台输出：插件启动期崩溃时，这里是唯一的线索
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add(Protocol.PipeArgument);
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(Protocol.PluginIdArgument);
            startInfo.ArgumentList.Add(_manifest.Id);
            foreach (var arg in _manifest.Arguments) startInfo.ArgumentList.Add(arg);

            _process = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("无法启动插件进程");

            // 异步转写插件的 stdout/stderr 到插件日志，避免管道写满导致插件阻塞
            CaptureProcessOutput(_process, _manifest.Id);

            _entry.LastStartedAt = DateTime.Now;
            _registry.Save();
            HostLog.Write($"启动插件 {_manifest.Id}（PID {_process.Id}，管道 {pipeName}）");

            // 等插件连上来；超时说明插件没按协议启动
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    KillProcess("插件未在 30 秒内连接宿主");
                    SetState(PluginState.Crashed, "插件未在 30 秒内连接宿主");
                    ScheduleRestart();
                    return;
                }
            }

            var connection = new IpcConnection(server);
            server = null; // 所有权已交给连接
            connection.Start();
            _connection = connection;

            // 先握手：下发协议版本与插件数据目录
            await connection.SendAsync(IpcEnvelope.Create(IpcTypes.Hello, new HelloPayload
            {
                ProtocolVersion = Protocol.Version,
                PluginId = _manifest.Id,
                HostVersion = _hostVersion,
                DataDirectory = HostPaths.PluginDataDirectory(_manifest.Id),
                LogDirectory = HostPaths.LogDirectory,
            })).ConfigureAwait(false);

            connection.MessageReceived += envelope => OnMessage(envelope);
            connection.Closed += error => OnConnectionClosed(error);
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => OnProcessExited();

            await connection.SendAsync(IpcEnvelope.Empty(IpcTypes.Start)).ConfigureAwait(false);

            SetState(PluginState.Running);
            _entry.CrashCount = 0;
            _restartAttempts = 0;
            _registry.Save();

            _ = WatchForStableRunAsync(ct);
        }
        catch (Exception ex)
        {
            HostLog.Write($"启动插件 {_manifest.Id} 失败: {ex}");
            KillProcess(null);
            SetState(PluginState.Crashed, ex.Message);
            ScheduleRestart();
        }
        finally
        {
            // 只有还没交给连接时才需要释放（正常路径已置空）
            if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>插件稳定运行足够久后清零崩溃计数，避免历史崩溃影响后续重启额度。</summary>
    private async Task WatchForStableRunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_registry.Settings.CrashResetAfterSeconds), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (State == PluginState.Running)
        {
            _restartAttempts = 0;
            _entry.CrashCount = 0;
            _entry.LastCrashReason = null;
            _registry.Save();
        }
    }

    private void OnMessage(IpcEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case IpcTypes.Ready:
                if (envelope.GetPayload<ReadyPayload>() is { } ready)
                {
                    StatusText = ready.StatusText ?? "";
                    _dispatcher.BeginInvoke(() =>
                    {
                        StatusChanged?.Invoke(this, StatusText);
                        StateChanged?.Invoke(this);
                    });
                }
                break;

            case IpcTypes.Status:
                if (envelope.GetPayload<StatusPayload>() is { } status)
                {
                    StatusText = status.Text;
                    _dispatcher.BeginInvoke(() =>
                    {
                        StatusChanged?.Invoke(this, status.Text);
                        StateChanged?.Invoke(this);
                    });
                }
                break;

            case IpcTypes.Log:
                if (envelope.GetPayload<LogPayload>() is { } log)
                    HostLog.WritePlugin(_manifest.Id, log.Level.ToString(), log.Message);
                break;

            case IpcTypes.Notify:
                if (envelope.GetPayload<NotifyPayload>() is { } notify)
                    _dispatcher.BeginInvoke(() => NotificationRequested?.Invoke(this, notify));
                break;

            case IpcTypes.DeclareHotkeys:
                HandleDeclareHotkeys(envelope);
                break;

            case IpcTypes.DeclareTrayMenu:
                HandleDeclareTrayMenu(envelope);
                break;

            case IpcTypes.DeclareSettings:
                HandleDeclareSettings(envelope);
                break;

            case IpcTypes.GetSetting:
                HandleGetSetting(envelope);
                break;

            case IpcTypes.SetSetting:
                HandleSetSetting(envelope);
                break;

            case IpcTypes.RequestShutdown:
                _ = _connection!.RespondAsync(envelope.RequestId, true);
                HostLog.Write($"插件 {_manifest.Id} 请求自行停止");
                _userRequestedStop = true; // 插件主动退出：视为正常结束，不自动重启
                Stop();
                break;

            case IpcTypes.RequestRestart:
                _ = _connection!.RespondAsync(envelope.RequestId, true);
                HostLog.Write($"插件 {_manifest.Id} 请求重启");
                Restart();
                break;

            case IpcTypes.Ping:
                _ = _connection!.RespondAsync(envelope.RequestId, true);
                break;

            default:
                // 未知消息不应答，避免把协议外内容当成请求
                HostLog.Write($"插件 {_manifest.Id} 发送了未知消息: {envelope.Type}");
                break;
        }
    }

    private void HandleDeclareHotkeys(IpcEnvelope envelope)
    {
        var declaration = envelope.GetPayload<DeclareHotkeysPayload>();
        if (declaration is null)
        {
            _ = _connection!.RespondAsync(envelope.RequestId, false, "热键声明格式错误");
            return;
        }

        _declaredHotkeys.Clear();
        foreach (var hk in declaration.Hotkeys)
        {
            // 组合键由插件决定（插件在自己的设置页改键后重新声明）。
            // 宿主只负责注册与仲裁，不保存一份会与插件设置失同步的覆盖副本。
            _declaredHotkeys.Add(new HotkeyDeclaration
            {
                ActionId = hk.ActionId,
                Combination = hk.Combination,
                Description = hk.Description,
                Fixed = hk.Fixed,
            });
        }

        // 注册是异步的（要回 UI 线程调 RegisterHotKey），因此这里只记下待应答的请求：
        // 真正的应答由 UpdateHotkeyRegistration 在拿到注册结果后发出，
        // 否则插件永远收到空结果，也就无法提示"这个热键被占用了"。
        if (envelope.RequestId is { Length: > 0 } requestId)
            lock (_pendingHotkeySync) _pendingHotkeyRequests.Add(requestId);

        // 通知热键管理器重新注册（结果经 UpdateHotkeyRegistration 回填并应答）
        _dispatcher.BeginInvoke(() => HotkeysChanged?.Invoke(this));
    }

    /// <summary>由热键管理器回填注册结果（不触发 HotkeysChanged，避免重入）。</summary>
    internal void UpdateHotkeyRegistration(IReadOnlyList<PluginHotkeyBinding> bindings)
    {
        _hotkeys.Clear();
        _hotkeys.AddRange(bindings);

        // 回填后才有结果可答，此时才回应插件先前发出的全部声明请求
        List<string> waiting;
        lock (_pendingHotkeySync)
        {
            if (_pendingHotkeyRequests.Count == 0) return;
            waiting = [.. _pendingHotkeyRequests];
            _pendingHotkeyRequests.Clear();
        }

        var payload = new HotkeyRegistrationPayload
        {
            Results = [.. _hotkeys.Select(b => new HotkeyRegistrationResult
            {
                ActionId = b.ActionId,
                Combination = b.Combination,
                Registered = b.Registered,
                Error = b.Error,
            })],
        };

        foreach (var requestId in waiting)
            _ = _connection?.RespondAsync(requestId, true, null, payload);
    }

    private void HandleDeclareTrayMenu(IpcEnvelope envelope)
    {
        var declaration = envelope.GetPayload<DeclareTrayMenuPayload>();
        if (declaration is null)
        {
            _ = _connection!.RespondAsync(envelope.RequestId, false, "菜单声明格式错误");
            return;
        }

        _menuItems.Clear();
        _menuItems.AddRange(declaration.Items);
        _ = _connection!.RespondAsync(envelope.RequestId, true);
        _dispatcher.BeginInvoke(() => MenuChanged?.Invoke(this));
    }

    private void HandleDeclareSettings(IpcEnvelope envelope)
    {
        var declaration = envelope.GetPayload<DeclareSettingsPayload>();
        if (declaration is null)
        {
            _ = _connection!.RespondAsync(envelope.RequestId, false, "设置声明格式错误");
            return;
        }

        _settings.Clear();
        _settings.AddRange(declaration.Items.Where(s => !string.IsNullOrWhiteSpace(s.Key)));
        _ = _connection!.RespondAsync(envelope.RequestId, true);
        _dispatcher.BeginInvoke(() => SettingsChanged?.Invoke(this));
    }

    /// <summary>
    /// 把用户在设置中心做出的改动回传给插件。
    /// 插件是设置值的唯一来源，宿主不代为持久化，避免出现两份真相。
    /// </summary>
    public Task SendSettingsChangedAsync(IReadOnlyList<SettingChange> changes)
    {
        if (_connection is null || changes.Count == 0) return Task.CompletedTask;
        return _connection.SendAsync(
            IpcEnvelope.Create(IpcTypes.SettingsChanged, new SettingsChangedPayload { Changes = [.. changes] }));
    }

    private void HandleGetSetting(IpcEnvelope envelope)
    {
        var request = envelope.GetPayload<GetSettingPayload>();
        if (request is null || string.IsNullOrWhiteSpace(request.Key))
        {
            _ = _connection!.RespondAsync(envelope.RequestId, false, "缺少 key");
            return;
        }

        var store = PluginSettingsStore.ForPlugin(_manifest.Id);
        var value = store.Get(request.Key);
        _ = _connection!.RespondAsync(envelope.RequestId, true, null, value);
    }

    private void HandleSetSetting(IpcEnvelope envelope)
    {
        var request = envelope.GetPayload<SetSettingPayload>();
        if (request is null || string.IsNullOrWhiteSpace(request.Key))
        {
            _ = _connection!.RespondAsync(envelope.RequestId, false, "缺少 key");
            return;
        }

        PluginSettingsStore.ForPlugin(_manifest.Id).Set(request.Key, request.Value);
        _ = _connection!.RespondAsync(envelope.RequestId, true);
    }

    /// <summary>把插件进程的 stdout/stderr 逐行转写到插件日志，便于诊断启动失败。</summary>
    private void CaptureProcessOutput(Process process, string pluginId)
    {
        TryCapture(process.StandardOutput, "stdout");
        TryCapture(process.StandardError, "stderr");

        void TryCapture(StreamReader reader, string tag)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            HostLog.WritePlugin(pluginId, tag, line);
                    }
                }
                catch
                {
                    // 进程退出时读取会中断，属正常情况
                }
                finally
                {
                    reader.Dispose();
                }
            });
        }
    }

    private void OnProcessExited()
    {
        if (_disposed || _userRequestedStop) return;

        // 进程退出事件与连接关闭可能各触发一次，用标志位保证只处理一次，
        // 否则会重复调度重启，退避次数被凭空翻倍
        if (_exitHandled) return;
        _exitHandled = true;

        var exitCode = _process?.ExitCode ?? -1;
        var reason = exitCode == 0
            ? "插件进程已退出"
            : $"插件进程异常退出（退出码 {exitCode}）";

        HostLog.Write($"插件 {_manifest.Id} {reason}");
        CloseConnection();

        if (exitCode == 0)
        {
            SetState(PluginState.Stopped, reason);
            return;
        }

        SetState(PluginState.Crashed, reason);
        ScheduleRestart();
    }

    private void OnConnectionClosed(Exception? error)
    {
        if (_disposed || _userRequestedStop) return;
        if (State != PluginState.Running) return;

        // 连接断了但进程还活着：让它继续，进程退出事件会接管后续处理
        HostLog.Write($"插件 {_manifest.Id} 连接关闭: {error?.Message ?? "对端主动关闭"}");
        if (_process is { HasExited: true })
            OnProcessExited();
    }

    /// <summary>崩溃后按策略重启；超过次数上限则进入 Faulted 并停止重试。</summary>
    private void ScheduleRestart()
    {
        if (_disposed) return;

        _entry.CrashCount++;
        _entry.TotalCrashes++;
        _entry.LastCrashReason = LastError;
        _registry.Save();

        if (!_registry.Settings.AutoRestartPlugins || !_entry.Enabled)
        {
            SetState(PluginState.Faulted, LastError + "（自动重启已关闭）");
            return;
        }

        _restartAttempts++;
        if (_restartAttempts > _registry.Settings.MaxRestartAttempts)
        {
            var message = $"{LastError}（已连续重启 {_restartAttempts - 1} 次仍未成功，已停止自动重启）";
            HostLog.Write($"插件 {_manifest.Id} {message}");
            SetState(PluginState.Faulted, message);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(2 * _restartAttempts, 10));
        HostLog.Write($"插件 {_manifest.Id} 将在 {delay.TotalSeconds:0} 秒后自动重启（第 {_restartAttempts} 次）");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
            }
            catch
            {
                return;
            }

            if (_disposed || _userRequestedStop || !_entry.Enabled) return;
            _ = _dispatcher.BeginInvoke(new Action(Start));
        });
    }

    /// <summary>停止插件：先发退出通知，宽限期内未退出则结束进程。</summary>
    public void Stop(int gracefulTimeoutMs = 5000)
    {
        _userRequestedStop = true;
        _ = StopCoreAsync(gracefulTimeoutMs);
    }

    private async Task StopCoreAsync(int gracefulTimeoutMs)
    {
        var connection = _connection;
        var process = _process;

        _lifetimeCts?.Cancel();

        if (connection is not null)
        {
            try
            {
                await connection.SendAsync(IpcEnvelope.Create(IpcTypes.Shutdown, new ShutdownPayload
                {
                    Reason = "宿主请求停止插件",
                    GracefulTimeoutMs = gracefulTimeoutMs,
                })).ConfigureAwait(false);
            }
            catch
            {
                // 连接已断，直接走强杀
            }
        }

        if (process is { HasExited: false })
        {
            try
            {
                using var waitCts = new CancellationTokenSource(gracefulTimeoutMs);
                await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcess("插件未在宽限期内退出，已强制结束");
            }
        }

        CloseConnection();
        SetState(PluginState.Stopped);
    }

    /// <summary>
    /// 清空插件声明的菜单与设置项。插件停止后这些声明已失效，
    /// 若不清掉，设置中心会残留一个无法编辑的旧表单。
    /// </summary>
    private void ClearDeclarations()
    {
        _menuItems.Clear();
        _settings.Clear();
        _dispatcher.BeginInvoke(() =>
        {
            MenuChanged?.Invoke(this);
            SettingsChanged?.Invoke(this);
        });
    }

    /// <summary>重启插件。</summary>
    public void Restart()
    {
        _ = Task.Run(async () =>
        {
            _userRequestedStop = true;
            await StopCoreAsync(3000).ConfigureAwait(false);
            _userRequestedStop = false;
            _restartAttempts = 0;
            _ = _dispatcher.BeginInvoke(new Action(Start));
        });
    }

    /// <summary>把宿主收到的热键按下事件转发给插件。</summary>
    public async Task SendHotkeyAsync(string actionId)
    {
        if (_connection is null) return;
        try
        {
            await _connection.SendAsync(IpcEnvelope.Create(IpcTypes.HotkeyPressed, new HotkeyPressedPayload
            {
                ActionId = actionId,
            }));
        }
        catch (Exception ex)
        {
            HostLog.Write($"转发热键到 {_manifest.Id} 失败: {ex.Message}");
        }
    }

    /// <summary>把用户点击的菜单项转发给插件。</summary>
    public async Task SendMenuInvokedAsync(string itemId, bool @checked)
    {
        if (_connection is null) return;
        try
        {
            await _connection.SendAsync(IpcEnvelope.Create(IpcTypes.MenuInvoked, new MenuInvokedPayload
            {
                ItemId = itemId,
                Checked = @checked,
            }));
        }
        catch (Exception ex)
        {
            HostLog.Write($"转发菜单点击到 {_manifest.Id} 失败: {ex.Message}");
        }
    }

    private void KillProcess(string? reason)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                if (reason is not null) HostLog.Write($"插件 {_manifest.Id}: {reason}");
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            HostLog.Write($"结束插件 {_manifest.Id} 进程失败: {ex.Message}");
        }
    }

    private void CloseConnection()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null) _ = connection.DisposeAsync();

        // 连接断开意味着插件的所有声明都失效了（正常停止、崩溃、宿主退出都会走到这里）
        ClearDeclarations();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _userRequestedStop = true;
        _lifetimeCts?.Cancel();
        await StopCoreAsync(2000).ConfigureAwait(false);
        _lifetimeCts?.Dispose();
        _process?.Dispose();
    }
}

/// <summary>插件私有设置的宿主侧存储（每个插件一个 JSON 文件）。</summary>
public sealed class PluginSettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _values;
    private static readonly object Sync = new();

    private PluginSettingsStore(string path, Dictionary<string, JsonElement> values)
    {
        _path = path;
        _values = values;
    }

    private static readonly Dictionary<string, PluginSettingsStore> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取得（并缓存）指定插件的设置存储。</summary>
    public static PluginSettingsStore ForPlugin(string pluginId)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(pluginId, out var cached)) return cached;

            var path = Path.Combine(HostPaths.PluginDataDirectory(pluginId), "settings.json");
            Dictionary<string, JsonElement> values = [];
            try
            {
                if (File.Exists(path))
                {
                    values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                                 File.ReadAllText(path), HostPaths.JsonOptions)
                             ?? [];
                }
            }
            catch (Exception ex)
            {
                HostLog.WritePlugin(pluginId, "Warning", $"设置读取失败: {ex.Message}");
            }

            var store = new PluginSettingsStore(path, values);
            Cache[pluginId] = store;
            return store;
        }
    }

    /// <summary>卸载插件时丢弃缓存（数据文件由卸载流程处理）。</summary>
    public static void Evict(string pluginId)
    {
        lock (Sync) Cache.Remove(pluginId);
    }

    public SettingValuePayload Get(string key)
    {
        lock (Sync)
        {
            return _values.TryGetValue(key, out var value)
                ? new SettingValuePayload { Exists = true, Value = value }
                : new SettingValuePayload { Exists = false };
        }
    }

    public void Set(string key, JsonElement value)
    {
        lock (Sync)
        {
            _values[key] = value;
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_values, HostPaths.JsonOptions));
            }
            catch (Exception ex)
            {
                HostLog.Write($"插件设置保存失败（{_path}）: {ex.Message}");
            }
        }
    }
}
