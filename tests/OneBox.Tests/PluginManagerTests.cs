using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 插件安装与卸载：这些操作会真实读写磁盘，测试把宿主数据目录指向临时目录，
/// 避免污染 %LOCALAPPDATA%\OneBox。
/// </summary>
public sealed class PluginManagerTests : IDisposable
{
    private readonly string _root;
    private readonly Dispatcher _dispatcher;

    public PluginManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onebox-pm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));

        // 宿主数据目录改到临时目录；HostPaths 是静态的，测试类内串行执行
        HostPaths.DataDirectoryOverride = _root;

        // 热键注册用假实现，避免依赖真实窗口
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Dispose()
    {
        HostPaths.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private PluginManager CreateManager(out FakeHotkeyRegistrar hotkeys)
    {
        hotkeys = new FakeHotkeyRegistrar();
        var registry = PluginRegistry.Load();
        return new PluginManager(registry, _dispatcher, hotkeys, "test");
    }

    /// <summary>造一个可安装的插件源目录（含 plugin.json 与入口文件）。</summary>
    private string CreatePluginSource(string id, string name = "测试插件", string entry = "test.exe")
    {
        var dir = Path.Combine(_root, "sources", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Protocol.ManifestFileName), JsonSerializer.Serialize(new
        {
            id,
            name,
            version = "1.0.0",
            description = "测试",
            entry,
            protocolVersion = Protocol.Version,
        }));
        File.WriteAllText(Path.Combine(dir, entry), "stub");
        return dir;
    }

    [Fact]
    public void Install_FromDirectory_CopiesIntoPluginsDirectory()
    {
        var manager = CreateManager(out _);
        var source = CreatePluginSource("demo");

        var ok = manager.Install(source, out var pluginId, out var error);

        Assert.True(ok, error);
        Assert.Equal("demo", pluginId);
        var installed = Path.Combine(HostPaths.PluginsDirectory, "demo");
        Assert.True(File.Exists(Path.Combine(installed, Protocol.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(installed, "test.exe")));
    }

    [Fact]
    public void Install_FromZip_ExtractsAndInstalls()
    {
        var manager = CreateManager(out _);
        var source = CreatePluginSource("zipped");
        var zip = Path.Combine(_root, "zipped.zip");
        ZipFile.CreateFromDirectory(source, zip);

        var ok = manager.Install(zip, out var pluginId, out var error);

        Assert.True(ok, error);
        Assert.Equal("zipped", pluginId);
        Assert.True(File.Exists(Path.Combine(HostPaths.PluginsDirectory, "zipped", "test.exe")));
    }

    [Fact]
    public void Install_ZipWithNestedFolder_FindsPluginRoot()
    {
        var manager = CreateManager(out _);
        // 压缩包里多套一层目录（很多打包工具会这样）
        var wrapper = Path.Combine(_root, "wrapper");
        var inner = Path.Combine(wrapper, "nested");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, Protocol.ManifestFileName), JsonSerializer.Serialize(new
        {
            id = "nested",
            name = "嵌套",
            entry = "test.exe",
            protocolVersion = Protocol.Version,
        }));
        File.WriteAllText(Path.Combine(inner, "test.exe"), "stub");
        var zip = Path.Combine(_root, "nested.zip");
        ZipFile.CreateFromDirectory(wrapper, zip);

        var ok = manager.Install(zip, out var pluginId, out var error);

        Assert.True(ok, error);
        Assert.Equal("nested", pluginId);
    }

    [Fact]
    public void Install_InvalidSource_Fails()
    {
        var manager = CreateManager(out _);
        var bogus = Path.Combine(_root, "no-such-thing.txt");
        File.WriteAllText(bogus, "x");

        var ok = manager.Install(bogus, out var pluginId, out var error);

        Assert.False(ok);
        Assert.Null(pluginId);
        Assert.Contains("插件目录或 .zip", error);
    }

    [Fact]
    public void Install_MissingManifest_Fails()
    {
        var manager = CreateManager(out _);
        var empty = Path.Combine(_root, "sources", "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "x");

        var ok = manager.Install(empty, out _, out var error);

        Assert.False(ok);
        Assert.Contains(Protocol.ManifestFileName, error);
    }

    [Fact]
    public void Install_SameIdTwice_ReplacesWithoutDuplicate()
    {
        var manager = CreateManager(out _);
        var source = CreatePluginSource("dup");
        Assert.True(manager.Install(source, out _, out _));

        manager.Discover();
        Assert.Single(manager.Plugins, p => p.Manifest?.Id == "dup");

        // 再次安装同 id：应整体替换，而不是新增一份
        File.WriteAllText(Path.Combine(source, "extra.txt"), "new");
        Assert.True(manager.Install(source, out _, out _));

        manager.Discover();
        Assert.Single(manager.Plugins, p => p.Manifest?.Id == "dup");
        Assert.True(File.Exists(Path.Combine(HostPaths.PluginsDirectory, "dup", "extra.txt")));
    }

    [Fact]
    public void Discover_FindsInstalledPluginAsInstalledSource()
    {
        var manager = CreateManager(out _);
        Assert.True(manager.Install(CreatePluginSource("installed-demo"), out _, out _));

        manager.Discover();

        var plugin = Assert.Single(manager.Plugins);
        Assert.Equal("installed-demo", plugin.Manifest.Id);
        Assert.Equal(PluginSource.Installed, plugin.Source);
    }

    [Fact]
    public void Discover_BrokenManifest_MarksWithError()
    {
        var manager = CreateManager(out _);
        var broken = Path.Combine(HostPaths.PluginsDirectory, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, Protocol.ManifestFileName), "{ 这不是 JSON");

        manager.Discover();

        var plugin = Assert.Single(manager.Plugins);
        Assert.Null(plugin.Manifest);
        Assert.NotNull(plugin.Error);
    }

    [Fact]
    public void Uninstall_RemovesPluginDirectoryAndRegistryEntry()
    {
        var manager = CreateManager(out _);
        Assert.True(manager.Install(CreatePluginSource("gone"), out _, out _));
        manager.Discover();

        var plugin = Assert.Single(manager.Plugins);
        var ok = manager.Uninstall(plugin, deleteUserData: false, out var error);

        Assert.True(ok, error);
        Assert.False(Directory.Exists(Path.Combine(HostPaths.PluginsDirectory, "gone")));
        Assert.Empty(manager.Plugins);

        // 注册表条目也要清掉，否则重装后仍带着旧状态
        manager.Discover();
        Assert.Empty(manager.Plugins);
    }

    [Fact]
    public void Uninstall_WithDeleteUserData_RemovesPluginState()
    {
        var manager = CreateManager(out _);
        Assert.True(manager.Install(CreatePluginSource("withdata"), out _, out _));
        manager.Discover();

        // 模拟插件已写过私有数据
        var dataDir = HostPaths.PluginDataDirectory("withdata");
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), "{}");
        Assert.True(Directory.Exists(dataDir));

        var plugin = Assert.Single(manager.Plugins);
        Assert.True(manager.Uninstall(plugin, deleteUserData: true, out var error), error);

        Assert.False(Directory.Exists(dataDir));
    }

    [Fact]
    public void Uninstall_WithoutDeleteUserData_KeepsPluginState()
    {
        var manager = CreateManager(out _);
        Assert.True(manager.Install(CreatePluginSource("keepdata"), out _, out _));
        manager.Discover();

        var dataDir = HostPaths.PluginDataDirectory("keepdata");
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), "{}");

        var plugin = Assert.Single(manager.Plugins);
        Assert.True(manager.Uninstall(plugin, deleteUserData: false, out var error), error);

        Assert.True(Directory.Exists(dataDir));
        Assert.True(File.Exists(Path.Combine(dataDir, "settings.json")));
    }

    [Fact]
    public void Uninstall_BundledPlugin_IsRefused()
    {
        var manager = CreateManager(out _);

        // 内置插件目录 = 宿主 exe 旁的 plugins；用当前测试程序集目录模拟
        var bundledRoot = HostPaths.BundledPluginsDirectory;
        var bundledDir = Path.Combine(bundledRoot, "bundled-demo");
        Directory.CreateDirectory(bundledDir);
        File.WriteAllText(Path.Combine(bundledDir, Protocol.ManifestFileName), JsonSerializer.Serialize(new
        {
            id = "bundled-demo",
            name = "内置",
            entry = "test.exe",
            protocolVersion = Protocol.Version,
        }));
        File.WriteAllText(Path.Combine(bundledDir, "test.exe"), "stub");

        try
        {
            manager.Discover();
            var plugin = Assert.Single(manager.Plugins, p => p.Manifest?.Id == "bundled-demo");

            var ok = manager.Uninstall(plugin, deleteUserData: true, out var error);

            Assert.False(ok);
            Assert.Contains("内置插件", error);
            Assert.True(Directory.Exists(bundledDir));
        }
        finally
        {
            try { Directory.Delete(bundledRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InstalledPlugin_OverridesBundledWithSameId()
    {
        var manager = CreateManager(out _);

        var bundledRoot = HostPaths.BundledPluginsDirectory;
        var bundledDir = Path.Combine(bundledRoot, "shadowed");
        Directory.CreateDirectory(bundledDir);
        File.WriteAllText(Path.Combine(bundledDir, Protocol.ManifestFileName), JsonSerializer.Serialize(new
        {
            id = "shadowed",
            name = "内置版本",
            entry = "test.exe",
            protocolVersion = Protocol.Version,
        }));
        File.WriteAllText(Path.Combine(bundledDir, "test.exe"), "stub");

        try
        {
            Assert.True(manager.Install(CreatePluginSource("shadowed", name: "用户版本"), out _, out _));
            manager.Discover();

            var plugin = Assert.Single(manager.Plugins);
            Assert.Equal("用户版本", plugin.Manifest.Name);
            Assert.Equal(PluginSource.Installed, plugin.Source);
        }
        finally
        {
            try { Directory.Delete(bundledRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Discover_IsIdempotent()
    {
        var manager = CreateManager(out _);
        Assert.True(manager.Install(CreatePluginSource("stable"), out _, out _));

        manager.Discover();
        manager.Discover();

        Assert.Single(manager.Plugins);
    }

    /// <summary>假的热键注册器：记录调用，不碰 Win32。</summary>
    private sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
    {
        public List<string> Registered { get; } = new();
        public int UnregisterAllCount { get; private set; }

        public string? Register(string combination, Action callback)
        {
            Registered.Add(combination);
            return null;
        }

        public void UnregisterAll()
        {
            UnregisterAllCount++;
            Registered.Clear();
        }

        public void Dispose() => UnregisterAll();
    }
}
