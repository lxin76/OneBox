using System.IO;
using System.IO.Compression;
using System.Windows.Threading;
using OneBox.Abstractions;

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

    public PluginManager(PluginRegistry registry, Dispatcher dispatcher, IHotkeyRegistrar hotkeys, string hostVersion)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _hotkeys = hotkeys;
        _hostVersion = hostVersion;
    }

    /// <summary>已发现的插件（含禁用与损坏的）。</summary>
    public IReadOnlyList<DiscoveredPlugin> Plugins => _plugins;

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

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _plugins)
        {
            var runtime = plugin.Runtime;
            if (runtime is null || plugin.Manifest is null) continue;

            // 只有正在运行的插件才持有热键：停止、崩溃或禁用的插件必须让出组合键。
            // 插件可能刚连上就声明热键（此时宿主还没把状态置为 Running），
            // 这种情况下声明请求保持挂起，等状态变为 Running 后本轮重建会自然应答它。
            if (runtime.State != PluginState.Running) continue;

            var results = new List<PluginHotkeyBinding>();
            foreach (var declared in runtime.DeclaredHotkeys)
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
                    Registered = error is null,
                    Error = error,
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

    private static PluginHotkeyBinding Failure(
        PluginRuntime runtime, HotkeyDeclaration declared, string combination, string error)
        => new()
        {
            PluginId = runtime.Manifest.Id,
            ActionId = declared.ActionId,
            Combination = combination,
            Description = declared.Description,
            Registered = false,
            Error = error,
        };

    /// <summary>全部插件当前占用的热键（工具箱的"热键总览"用）。</summary>
    public IEnumerable<PluginHotkeyBinding> AllHotkeys
        => _plugins.SelectMany(p => p.Runtime?.Hotkeys ?? []);

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
