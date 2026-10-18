using System.IO;
using System.Windows.Threading;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 改键录制期间的热键暂停/恢复：
/// 录制时按键绝不能触发插件功能，录制结束必须完整恢复。
/// </summary>
public sealed class HotkeySuspendTests : IDisposable
{
    private readonly string _root;

    public HotkeySuspendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onebox-su-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        HostPaths.DataDirectoryOverride = _root;
    }

    public void Dispose()
    {
        HostPaths.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private PluginManager CreateManager(out FakeRegistrar registrar)
    {
        registrar = new FakeRegistrar();
        var registry = PluginRegistry.Load();
        return new PluginManager(
            registry, Dispatcher.CurrentDispatcher, registrar, "test", new HotkeyOverrides(), _ => true);
    }

    [Fact]
    public void Suspend_MarksStateAndUnregistersAll()
    {
        var manager = CreateManager(out var registrar);

        manager.SuspendHotkeys();

        Assert.True(manager.HotkeysSuspended);
        Assert.True(registrar.UnregisterAllCount > 0);
    }

    [Fact]
    public void Suspend_IsIdempotent()
    {
        var manager = CreateManager(out var registrar);

        manager.SuspendHotkeys();
        var count = registrar.UnregisterAllCount;
        manager.SuspendHotkeys();

        Assert.Equal(count, registrar.UnregisterAllCount);
    }

    [Fact]
    public void Resume_ClearsSuspendedFlag()
    {
        var manager = CreateManager(out _);

        manager.SuspendHotkeys();
        manager.ResumeHotkeys();

        Assert.False(manager.HotkeysSuspended);
    }

    [Fact]
    public void Resume_WhenNotSuspended_IsNoOp()
    {
        var manager = CreateManager(out var registrar);

        manager.ResumeHotkeys();

        Assert.Equal(0, registrar.UnregisterAllCount);
        Assert.False(manager.HotkeysSuspended);
    }

    [Fact]
    public void SuspendThenResume_RestoresRegistration()
    {
        var manager = CreateManager(out var registrar);

        manager.SuspendHotkeys();
        Assert.True(manager.HotkeysSuspended);

        manager.ResumeHotkeys();
        Assert.False(manager.HotkeysSuspended);

        // 恢复后应当重新走了一遍"重建注册"（这里没有运行中的插件，注册列表为空，
        // 但至少要确认注销计数增加，说明确实执行了重建）
        Assert.True(registrar.UnregisterAllCount >= 2);
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
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
