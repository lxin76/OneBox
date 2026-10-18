using System.IO;
using System.IO.Compression;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Abstractions.Theming;

namespace OneBox.Host.Services;

/// <summary>一次发现的插件：清单 + 运行时状态。</summary>
public sealed class DiscoveredPlugin
{
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required string Source { get; init; }
    /// <summary>清单损坏时的原因；此时 Manifest 为 null。</summary>
    public string? Error { get; init; }
    public PluginRuntime? Runtime { get; set; }
}

/// <summary>
/// 插件管理中枢：扫描插件目录、启停插件进程、统一注册插件热键、
/// 处理已有插件的热键冲突仲裁，以及安装/卸载插件。
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly Dispatcher _dispatcher;
    private readonly IHotkeyRegistrar _hotkeys;
    private readonly string _hostVersion;
    private readonly List<DiscoveredPlugin> _plugins = [];

    /// <summary>已写入日志的热键失败签名，用于抑制重复日志。</summary>
    private readonly HashSet<string> _loggedHotkeyFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户自定义热键的覆盖层（宿主侧唯一权威来源）。</summary>
    private readonly HotkeyOverrides _hotkeyOverrides;

    /// <summary>改键时用来探测组合键是否被其他软件占用（默认实现是 Win32 RegisterHotKey 试探）。</summary>
    private readonly Func<string, bool> _probeAvailability;

    public PluginManager(PluginRegistry registry, Dispatcher dispatcher, IHotkeyRegistrar hotkeys, string hostVersion)
        : this(registry, dispatcher, hotkeys, hostVersion, null, null)
    {
    }

    /// <param name="hotkeyOverrides">
    /// 用户改键覆盖层；为 null 时从注册表里的宿主设置读取（正常启动路径）。
    /// </param>
    /// <param name="probeAvailability">
    /// 探测组合键是否被外部占用；为 null 时使用 <see cref="HotkeyService.ProbeAvailable"/>。
    /// </param>
    public PluginManager(
        PluginRegistry registry,
        Dispatcher dispatcher,
        IHotkeyRegistrar hotkeys,
        string hostVersion,
        HotkeyOverrides? hotkeyOverrides,
        Func<string, bool>? probeAvailability)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _hotkeys = hotkeys;
        _hostVersion = hostVersion;
        _hotkeyOverrides = hotkeyOverrides
            ?? HotkeyOverrides.FromPersisted(registry.Settings.HotkeyOverrides);
        _probeAvailability = probeAvailability ?? HotkeyService.ProbeAvailable;
    }

    /// <summary>已发现的插件（含禁用与损坏的）。</summary>
    public IReadOnlyList<DiscoveredPlugin> Plugins => _plugins;

    /// <summary>用户自定义热键的覆盖层（只读用途，例如界面判断某项是否被改过）。</summary>
    public HotkeyOverrides HotkeyOverrides => _hotkeyOverrides;

    /// <summary>
    /// 是否暂停全局热键。录制改键期间置为 true：
    /// 此时所有热键从系统注销，用户按下任何组合都只会被录制器读到，
    /// 不会顺带触发插件功能。关闭后才重新注册。
    /// </summary>
    private bool _hotkeysSuspended;

    /// <summary>
    /// 暂停/恢复全局热键注册。
    ///
    /// 为什么必须这么做：全局热键由系统直接派发，**低级键盘钩子拦不住它**
    /// （钩子只能阻止按键继续传给其他窗口，管不了已经注册的 hotkey 消息）。
    /// 只有主动 UnregisterHotKey，录制时按键才真正不会触发插件功能。
    /// </summary>
    public void SuspendHotkeys()
    {
        if (_hotkeysSuspended) return;
        _hotkeysSuspended = true;
        _hotkeys.UnregisterAll();
        HostLog.Write("已暂停全局热键（改键录制中）");
    }

    /// <summary>恢复全局热键注册（录制结束或取消后调用）。</summary>
    public void ResumeHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;
        RebuildHotkeys();
        HostLog.Write("已恢复全局热键");
    }

    /// <summary>当前是否处于热键暂停状态（重建注册时需要跳过）。</summary>
    public bool HotkeysSuspended => _hotkeysSuspended;

    /// <summary>热键注册结果发生变化（改键后界面需要刷新）。</summary>
    public event Action? HotkeysChanged;

    /// <summary>插件集合或状态发生变化。</summary>
    public event Action? Changed;

    /// <summary>插件请求弹出气泡通知。</summary>
    public event Action<PluginRuntime, NotifyPayload>? NotificationRequested;

    /// <summary>插件上报状态文本。</summary>
    public event Action<PluginRuntime, string>? StatusChanged;

    /// <summary>插件注册的菜单项变化。</summary>
    public event Action? MenuChanged;

    /// <summary>某个插件声明的设置项发生变化（设置中心需要重建表单）。</summary>
    public event Action? SettingsChanged;

    // ---- 发现 ----

    /// <summary>扫描内置与用户插件目录，建立插件列表。重复启动安全。</summary>
    public void Discover()
    {
        foreach (var plugin in _plugins) plugin.Runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _plugins.Clear();

        ScanDirectory(HostPaths.BundledPluginsDirectory, PluginSource.Bundled);
        ScanDirectory(HostPaths.PluginsDirectory, PluginSource.Installed);

        // 用户安装的同 id 插件优先于内置插件
        var duplicates = _plugins
            .Where(p => p.Manifest is not null)
            .GroupBy(p => p.Manifest!.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.FirstOrDefault(p => p.Source == PluginSource.Bundled))
            .Where(p => p is not null)
            .ToList();
        foreach (var dup in duplicates) _plugins.Remove(dup!);

        foreach (var plugin in _plugins.Where(p => p.Manifest is not null))
            CreateRuntime(plugin);

        // 重新扫描可能换掉了插件目录，图标缓存里的路径可能已失效
        PluginIcons.Invalidate();

        HostLog.Write($"发现插件 {_plugins.Count} 个");
        Changed?.Invoke();
    }

    private void ScanDirectory(string directory, string source)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            // 每个子目录 = 一个插件；目录名仅用于排障，真实 id 取自清单
            var result = ManifestReader.Load(sub);
            if (result.Success)
            {
                _plugins.Add(new DiscoveredPlugin
                {
                    Manifest = result.Manifest!,
                    Directory = sub,
                    Source = source,
                });
            }
            else
            {
                _plugins.Add(new DiscoveredPlugin
                {
                    Manifest = null!,
                    Directory = sub,
                    Source = source,
                    Error = result.Error,
                });
                HostLog.Write($"插件目录 {sub} 无法加载: {result.Error}");
            }
        }
    }

    private void CreateRuntime(DiscoveredPlugin plugin)
    {
        var entry = _registry.GetOrAdd(plugin.Manifest.Id, plugin.Source);
        var runtime = new PluginRuntime(plugin.Manifest, entry, _registry, _dispatcher, _hostVersion);

        runtime.StateChanged += _ => _dispatcher.BeginInvoke(() =>
        {
            // 插件停止/崩溃时它的热键也应失效，因此状态变化要重建热键
            RebuildHotkeys();
            Changed?.Invoke();
        });
        runtime.StatusChanged += (rt, text) => StatusChanged?.Invoke(rt, text);
        runtime.NotificationRequested += (rt, notify) => NotificationRequested?.Invoke(rt, notify);
        runtime.HotkeysChanged += OnPluginHotkeysChanged;
        runtime.MenuChanged += _ => MenuChanged?.Invoke();
        runtime.SettingsChanged += _ => SettingsChanged?.Invoke();
        plugin.Runtime = runtime;
    }

    // ---- 启用 / 禁用 / 卸载 ----

    /// <summary>启用或禁用插件；禁用会停止进程并释放其热键。</summary>
    public void SetEnabled(DiscoveredPlugin plugin, bool enabled)
    {
        if (plugin.Manifest is null) return;

        var entry = _registry.GetOrAdd(plugin.Manifest.Id, plugin.Source);
        entry.Enabled = enabled;
        entry.CrashCount = 0;
        _registry.Save();

        if (!enabled)
        {
            plugin.Runtime?.Stop(); // 停止后状态变化会触发热键重建，从而释放它的热键
        }
        else
        {
            plugin.Runtime?.Start();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 卸载插件：停止进程 → 释放热键 → 删除插件目录 → 清空注册表与缓存。
    /// </summary>
    /// <param name="plugin">目标插件。</param>
    /// <param name="deleteUserData">是否同时删除插件私有数据目录。</param>
    /// <param name="error">失败原因。</param>
    public bool Uninstall(DiscoveredPlugin plugin, bool deleteUserData, out string? error)
    {
        error = null;
        var id = plugin.Manifest?.Id ?? Path.GetFileName(plugin.Directory);

        try
        {
            if (plugin.Runtime is not null)
            {
                plugin.Runtime.Stop();
                plugin.Runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            RebuildHotkeys(); // 插件已停止，释放它占用的热键

            // 内置插件不允许删除（它们随宿主分发，删了会在下次启动复原）
            if (plugin.Source == PluginSource.Bundled)
            {
                error = "这是随宿主分发的内置插件，无法卸载；可改为禁用它。";
                return false;
            }

            DeleteDirectory(plugin.Directory);

            if (deleteUserData)
            {
                var dataDir = Path.Combine(HostPaths.PluginDataRoot, id);
                DeleteDirectory(dataDir);
                PluginSettingsStore.Evict(id);
            }

            _registry.Remove(id);
            _registry.Save();
            _plugins.Remove(plugin);

            HostLog.Write($"已卸载插件 {id}（删除数据：{deleteUserData}）");
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            HostLog.Write($"卸载插件 {id} 失败: {ex}");
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        // 插件目录里的只读文件会挡住删除，先清掉只读属性
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            catch
            {
                // 单个文件属性失败不阻断整体删除
            }
        }
        Directory.Delete(path, recursive: true);
    }

    // ---- 安装 ----

    /// <summary>
    /// 从目录或 .zip 包安装插件。目录会被复制到插件目录；zip 会被解压。
    /// 若压缩包内包含顶层目录，则以该目录为插件根。
    /// </summary>
    public bool Install(string sourcePath, out string? pluginId, out string? error)
    {
        pluginId = null;
        error = null;

        try
        {
            string stagingRoot = Path.Combine(Path.GetTempPath(), "onebox-install-" + Guid.NewGuid().ToString("N"));

            try
            {
                if (Directory.Exists(sourcePath))
                {
                    CopyDirectory(sourcePath, stagingRoot);
                }
                else if (File.Exists(sourcePath) && Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(stagingRoot);
                    ZipFile.ExtractToDirectory(sourcePath, stagingRoot);
                }
                else
                {
                    error = "请选择插件目录或 .zip 插件包";
                    return false;
                }

                // 允许 zip 里再套一层目录
                var candidate = FindPluginRoot(stagingRoot);
                if (candidate is null)
                {
                    error = $"未找到 {Protocol.ManifestFileName}";
                    return false;
                }

                var result = ManifestReader.Load(candidate);
                if (!result.Success)
                {
                    error = result.Error;
                    return false;
                }

                var manifest = result.Manifest!;
                var target = Path.Combine(HostPaths.PluginsDirectory, manifest.Id);

                // 已安装同 id 插件：先停掉再整体替换，避免半新半旧的文件混在一起
                var existing = _plugins.FirstOrDefault(p =>
                    string.Equals(p.Manifest?.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)
                    && p.Source == PluginSource.Installed);
                if (existing is not null)
                {
                    existing.Runtime?.Stop();
                    existing.Runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }

                if (Directory.Exists(target)) DeleteDirectory(target);
                CopyDirectory(candidate, target);

                pluginId = manifest.Id;
                HostLog.Write($"已安装插件 {manifest.Id} → {target}");
                return true;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
                }
                catch
                {
                    // 临时目录清理失败无碍
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            HostLog.Write($"安装插件失败: {ex}");
            return false;
        }
    }

    private static string? FindPluginRoot(string root)
    {
        if (File.Exists(Path.Combine(root, Protocol.ManifestFileName))) return root;

        foreach (var sub in Directory.EnumerateDirectories(root))
            if (File.Exists(Path.Combine(sub, Protocol.ManifestFileName)))
                return sub;

        return null;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    // ---- 热键托管 ----

    private void OnPluginHotkeysChanged(PluginRuntime runtime)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RebuildHotkeys();
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// 重建全部插件热键注册。插件数量与热键数量都很小，整体重建比单条增删更不容易出错，
    /// 也保证注册顺序稳定；冲突项会被标记为失败并说明原因，不影响其他热键。
    /// </summary>
    private void RebuildHotkeys()
    {
        _hotkeys.UnregisterAll();

        // 录制改键期间不注册任何热键：此时注册回去会让下一次按键又触发插件功能
        if (_hotkeysSuspended) return;

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _plugins)
        {
            var runtime = plugin.Runtime;
            if (runtime is null || plugin.Manifest is null) continue;

            // 只有正在运行的插件才持有热键：停止、崩溃或禁用的插件必须让出组合键。
            // 插件可能刚连上就声明热键（此时宿主还没把状态置为 Running），
            // 这种情况下声明请求保持挂起，等状态变为 Running 后本轮重建会自然应答它。
            if (runtime.State != PluginState.Running) continue;

            // 默认键据插件自己的声明，用来判断"这一项是不是被用户改过"
            var defaults = runtime.DeclaredHotkeys.ToDictionary(
                d => d.ActionId, d => d.Combination, StringComparer.OrdinalIgnoreCase);

            var results = new List<PluginHotkeyBinding>();

            // 同一动作在一次声明里重复出现（插件把同一份列表发了两次、或列表本身写重）
            // 不是冲突，只是同一件事说了两遍：按 actionId 去重，
            // 否则第二份会被判成"被其他插件占用"，在热键页里显示成一条假的冲突行
            var seenActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var declared in EffectiveHotkeys(plugin.Manifest.Id, runtime))
            {
                var normalized = HotkeyParser.Normalize(declared.Combination);
                if (normalized is null)
                {
                    results.Add(Failure(runtime, declared, declared.Combination, "无法解析的热键组合"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(declared.ActionId))
                {
                    results.Add(Failure(runtime, declared, normalized, "缺少 actionId"));
                    continue;
                }

                if (!seenActions.Add(declared.ActionId))
                {
                    HostLog.Write($"插件 {plugin.Manifest.Id} 的声明里动作 {declared.ActionId} 重复出现，已忽略后一条");
                    continue;
                }

                if (taken.Contains(normalized))
                {
                    results.Add(Failure(runtime, declared, normalized, $"热键 {normalized} 已被其他插件占用"));
                    continue;
                }

                var actionId = declared.ActionId;
                var error = _hotkeys.Register(normalized, () => _ = runtime.SendHotkeyAsync(actionId));
                if (error is null) taken.Add(normalized);

                results.Add(new PluginHotkeyBinding
                {
                    PluginId = plugin.Manifest.Id,
                    ActionId = actionId,
                    Combination = normalized,
                    Description = declared.Description,
                    DefaultCombination = defaults.TryGetValue(actionId, out var def) ? def : declared.Combination,
                    Fixed = declared.Fixed,
                    Registered = error is null,
                    Error = error,
                    // 注册器只在"键已被别的进程注册"时给出 1409 这类明确原因
                    OccupiedByOtherApp = error is not null && LooksLikeExternalOccupation(error),
                });
            }

            runtime.UpdateHotkeyRegistration(results);

            // 一次声明可能触发多次重建（状态变化与热键变化各一次），
            // 按"插件 + 组合键 + 原因"去重，避免同一失败被反复写进日志
            foreach (var failure in results.Where(r => !r.Registered))
            {
                var signature = $"{plugin.Manifest.Id}|{failure.Combination}|{failure.Error}";
                if (_loggedHotkeyFailures.Add(signature))
                    HostLog.Write($"插件 {plugin.Manifest.Id} 热键未注册: {failure.Combination} — {failure.Error}");
            }

            // 恢复成功的组合键：允许它将来再次失败时重新记录
            foreach (var ok in results.Where(r => r.Registered))
                _loggedHotkeyFailures.RemoveWhere(s => s.StartsWith($"{plugin.Manifest.Id}|{ok.Combination}|", StringComparison.OrdinalIgnoreCase));
        }

        MenuChanged?.Invoke();
    }

    /// <summary>
    /// 某个插件"实际生效"的热键：插件声明为底，用户改过的键覆盖其上。
    ///
    /// 固定热键（<see cref="HotkeyDeclaration.Fixed"/>）不参与覆盖——这类键是救急用的，
    /// 改了之后用户遇到麻烦时自己也按不回来。
    /// </summary>
    private IEnumerable<HotkeyDeclaration> EffectiveHotkeys(string pluginId, PluginRuntime runtime)
    {
        foreach (var declared in runtime.DeclaredHotkeys)
        {
            if (declared.Fixed) yield return declared;

            var custom = _hotkeyOverrides.Get(pluginId, declared.ActionId);
            if (custom is null)
            {
                yield return declared;
                continue;
            }

            yield return new HotkeyDeclaration
            {
                ActionId = declared.ActionId,
                Combination = custom,
                Description = declared.Description,
                Fixed = declared.Fixed,
            };
        }
    }

    // ---- 用户改键 ----

    /// <summary>
    /// 平台内全部热键的当前占位（改键检查的输入）。
    /// 用插件声明的默认键 + 用户覆盖，而不是注册结果：未运行插件的热键照样要参与冲突判断，
    /// 否则"插件停着时把键改了，等它启动才发现撞车"。
    /// </summary>
    public IReadOnlyList<HotkeyOccupant> Occupants()
    {
        var list = new List<HotkeyOccupant>();

        foreach (var plugin in _plugins)
        {
            var runtime = plugin.Runtime;
            var manifest = plugin.Manifest;
            if (runtime is null || manifest is null) continue;

            foreach (var declared in EffectiveHotkeys(manifest.Id, runtime))
            {
                list.Add(new HotkeyOccupant
                {
                    PluginId = manifest.Id,
                    PluginName = manifest.Name,
                    ActionId = declared.ActionId,
                    Description = string.IsNullOrWhiteSpace(declared.Description) ? declared.ActionId : declared.Description,
                    Combination = declared.Combination,
                    IsCustom = _hotkeyOverrides.Get(manifest.Id, declared.ActionId) is not null,
                    Fixed = declared.Fixed,
                });
            }
        }

        return list;
    }

    /// <summary>
    /// 检查把某个动作改成 <paramref name="combination"/> 是否可行，不落盘也不注册。
    /// 界面在用户按下组合键时调用，以便即时给出冲突提示。
    /// </summary>
    public HotkeyChangeCheck CheckHotkeyChange(string pluginId, string actionId, string? combination)
    {
        var occupants = Occupants();
        var check = HotkeyChangeValidator.Check(combination, pluginId, actionId, occupants);
        if (!check.CanApply || HotkeyParser.Normalize(combination) is not { } normalized) return check;

        // 已经被 OneBox 自己占着的键，探测必然失败——那是误报，不能算"被其他软件占用"。
        //
        // 这里的判断不能用"是否与某个动作相同"：一个键可能被 OneBox 的**其他**动作占着
        // （插件未运行时它不注册、但一旦启动就会抢），那种情况内部冲突检查不一定拦得住，
        // 探测却会返回失败。所以按"OneBox 内是否已有动作声称使用该键"来判断。
        var claimedBySelf = occupants.Any(o =>
            !string.IsNullOrWhiteSpace(o.Combination)
            && HotkeyParser.SameCombination(o.Combination, normalized)
            && !(string.Equals(o.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(o.ActionId, actionId, StringComparison.OrdinalIgnoreCase)));
        if (claimedBySelf) return check;

        bool available;
        try
        {
            available = _probeAvailability(normalized);
        }
        catch (Exception ex)
        {
            HostLog.Write($"热键可用性探测失败（{normalized}）: {ex.Message}");
            return check;
        }

        return HotkeyChangeValidator.WithAvailabilityProbe(check, available, normalized);
    }

    /// <summary>
    /// 把某个动作的热键改成 <paramref name="combination"/> 并立即重新注册。
    /// 校验不通过时不做任何改动，直接把检查结果返回给调用方。
    /// </summary>
    /// <returns>null 表示改键成功；否则为失败原因。</returns>
    public string? SetHotkey(string pluginId, string actionId, string? combination)
    {
        var check = CheckHotkeyChange(pluginId, actionId, combination);
        if (!check.CanApply) return check.Error;

        if (HotkeyParser.Normalize(combination) is not { } normalized) return "无法识别的组合键";

        _hotkeyOverrides.Set(pluginId, actionId, normalized);
        PersistHotkeyOverrides();
        RebuildHotkeys();
        HotkeysChanged?.Invoke();
        Changed?.Invoke();
        HostLog.Write($"用户改键：{pluginId}/{actionId} → {normalized}");
        return null;
    }

    /// <summary>恢复某个动作的默认热键（删掉覆盖项并重新注册）。</summary>
    public void ResetHotkey(string pluginId, string actionId)
    {
        if (!_hotkeyOverrides.Remove(pluginId, actionId)) return;

        PersistHotkeyOverrides();
        RebuildHotkeys();
        HotkeysChanged?.Invoke();
        Changed?.Invoke();
        HostLog.Write($"用户恢复默认热键：{pluginId}/{actionId}");
    }

    /// <summary>
    /// 重新探测已在使用的热键是否可用（"检查冲突"按钮）。
    /// 返回每个组合键 → 是否空闲；探测本身失败时对应项不出现。
    /// </summary>
    public IReadOnlyDictionary<string, bool> ProbeInUseHotkeys()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var occupant in Occupants())
        {
            if (HotkeyParser.Normalize(occupant.Combination) is not { } normalized) continue;
            if (result.ContainsKey(normalized)) continue;

            try
            {
                result[normalized] = _probeAvailability(normalized);
            }
            catch
            {
                // 探测失败不代表冲突，跳过即可
            }
        }

        return result;
    }

    /// <summary>
    /// 批量检查当前全部热键是否被其他软件占用（"检查与其他软件冲突"按钮用）。
    ///
    /// 返回：组合键 → 提示文案。**只包含有冲突的项**，调用方不必再筛一次。
    /// 判定前先排除"OneBox 自己声称使用的键"，否则宿主已注册的热键会全部被判成外部占用。
    /// </summary>
    public IReadOnlyDictionary<string, string> FindExternalConflicts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // OneBox 自己正在用的键：探测时必然失败，不能算外部冲突
        var ownedBySelf = new HashSet<string>(
            Occupants()
                .Select(o => HotkeyParser.Normalize(o.Combination))
                .Where(n => n is not null)
                .Select(n => n!),
            StringComparer.OrdinalIgnoreCase);

        // 已注册成功的键同样属于自己
        foreach (var registered in AllHotkeys.Where(h => h.Registered))
        {
            if (HotkeyParser.Normalize(registered.Combination) is { } normalized)
                ownedBySelf.Add(normalized);
        }

        foreach (var binding in AllHotkeys)
        {
            if (HotkeyParser.Normalize(binding.Combination) is not { } normalized) continue;
            if (result.ContainsKey(normalized)) continue;

            // 注册时就已经因此失败的，直接沿用结论，不必再探
            if (binding.OccupiedByOtherApp)
            {
                result[normalized] = DescribeExternalConflict(normalized);
                continue;
            }

            // 键被 OneBox 自己注册着，探测必然失败，不能据此说"被别的软件占用"
            if (ownedBySelf.Contains(normalized)) continue;

            try
            {
                if (!_probeAvailability(normalized))
                    result[normalized] = DescribeExternalConflict(normalized);
            }
            catch (Exception ex)
            {
                HostLog.Write($"热键冲突检查失败（{normalized}）: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>外部冲突的统一提示文案。</summary>
    private static string DescribeExternalConflict(string combination)
        => $"与{combination}冲突：该组合已被其他软件注册，与它同时运行时此功能无法触发。";

    /// <summary>把覆盖层写回宿主设置文件。</summary>
    private void PersistHotkeyOverrides()
    {
        _registry.Settings.HotkeyOverrides = _hotkeyOverrides.ToDictionary();
        _registry.Save();
    }

    /// <summary>
    /// 注册失败原因看起来是否"被其他软件占用"。
    ///
    /// 走的是错误文本而不是错误码：<see cref="IHotkeyRegistrar"/> 是抽象接口，
    /// 换成别的实现（测试替身、以后的原生注册器）时同样要能判断。
    /// 之所以要单独区分：插件之间撞车要改键才能解决，被别的软件占用则未必——
    /// 两者同时提示会让用户做出错误处置。
    /// </summary>
    private static bool LooksLikeExternalOccupation(string error)
        => error.Contains("其他软件", StringComparison.Ordinal)
           || error.Contains("已被占用", StringComparison.Ordinal)
           || error.Contains("其他程序", StringComparison.Ordinal)
           || error.Contains("1409", StringComparison.Ordinal);

    private static PluginHotkeyBinding Failure(
        PluginRuntime runtime, HotkeyDeclaration declared, string combination, string error)
        => new()
        {
            PluginId = runtime.Manifest.Id,
            ActionId = declared.ActionId,
            Combination = combination,
            Description = declared.Description,
            DefaultCombination = declared.Combination,
            Fixed = declared.Fixed,
            Registered = false,
            Error = error,
        };

    /// <summary>全部插件当前占用的热键（工具箱的"热键总览"用）。</summary>
    public IEnumerable<PluginHotkeyBinding> AllHotkeys
        => _plugins.SelectMany(p => p.Runtime?.Hotkeys ?? []);

    /// <summary>
    /// 把当前主题广播给所有插件（用户切换主题时调用）。
    /// 只发给已建立连接的插件；未运行/未启动的插件在下次握手时从 Hello 里拿到最新主题。
    /// </summary>
    public void BroadcastTheme(ThemePalette theme)
    {
        foreach (var plugin in _plugins)
            _ = plugin.Runtime?.SendThemeAsync(theme);
    }

    /// <summary>启动所有已启用的插件。</summary>
    public void StartEnabled()
    {
        foreach (var plugin in _plugins)
        {
            if (plugin.Manifest is null) continue;
            if (_registry.Find(plugin.Manifest.Id)?.Enabled == true) plugin.Runtime?.Start();
        }
    }

    /// <summary>停止所有插件（宿主退出时调用）。</summary>
    public void StopAll()
    {
        foreach (var plugin in _plugins) plugin.Runtime?.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _plugins)
        {
            if (plugin.Runtime is not null) await plugin.Runtime.DisposeAsync().ConfigureAwait(false);
        }
        _hotkeys.Dispose();
    }
}
