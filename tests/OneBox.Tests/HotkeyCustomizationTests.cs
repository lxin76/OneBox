using System.IO;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 用户自定义热键的端到端行为：改键后是否真的改了注册、是否落盘、冲突时是否拒绝。
///
/// 这里不启动插件进程，而是直接构造"插件已声明热键"的状态，让测试只关心改键这条链路。
/// </summary>
public sealed class HotkeyCustomizationTests : IDisposable
{
    private readonly string _root;

    public HotkeyCustomizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onebox-hk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        HostPaths.DataDirectoryOverride = _root;
    }

    public void Dispose()
    {
        HostPaths.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private PluginManager CreateManager(
        out FakeHotkeyRegistrar hotkeys, out PluginRegistry registry, bool available = true)
    {
        hotkeys = new FakeHotkeyRegistrar();
        registry = PluginRegistry.Load();
        return new PluginManager(
            registry, Dispatcher.CurrentDispatcher, hotkeys, "test", new HotkeyOverrides(), _ => available);
    }

    [Fact]
    public void Overrides_PersistToHostSettings()
    {
        // 改键结果必须落到服务器设置里，否则重启后用户的修改就丢了
        var manager = CreateManager(out _, out var registry, available: true);
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "Ctrl+Alt+Q");

        registry.Settings.HotkeyOverrides = overrides.ToDictionary();
        registry.Save();

        var reloaded = PluginRegistry.Load();
        var persisted = HotkeyOverrides.FromPersisted(reloaded.Settings.HotkeyOverrides);

        Assert.Equal("Ctrl+Alt+Q", persisted.Get("textpin", "toggle"));
    }

    [Fact]
    public void DefaultSettings_HaveNoOverrides()
    {
        var registry = PluginRegistry.Load();

        Assert.Null(registry.Settings.HotkeyOverrides);
        Assert.Equal(0, HotkeyOverrides.FromPersisted(registry.Settings.HotkeyOverrides).Count);
    }

    [Fact]
    public void CheckHotkeyChange_DetectsConflictAgainstOccupants()
    {
        var manager = CreateManager(out _, out _, available: true);
        var occupant = new HotkeyOccupant
        {
            PluginId = "screenshot",
            PluginName = "截图",
            ActionId = "region",
            Description = "区域截图",
            Combination = "Ctrl+Alt+A",
        };

        // 直接走校验器（管理器侧的占位来自真实插件，这里用等价数据验证判定规则）
        var check = HotkeyChangeValidator.Check("Ctrl+Alt+A", "textpin", "toggle", new[] { occupant });

        Assert.False(check.CanApply);
        Assert.Contains("截图", check.Error);
    }

    [Fact]
    public void ProbeUnavailable_DoesNotBlockButWarns()
    {
        var manager = CreateManager(out _, out _, available: false);

        // 没有插件时占位为空，只剩外部探测这一层：应放行但给警告
        var check = manager.CheckHotkeyChange("textpin", "toggle", "Ctrl+Alt+Q");

        Assert.True(check.CanApply);
        Assert.NotNull(check.Warning);
    }

    [Fact]
    public void ProbeAvailable_NoWarning()
    {
        var manager = CreateManager(out _, out _, available: true);

        var check = manager.CheckHotkeyChange("textpin", "toggle", "Ctrl+Alt+Q");

        Assert.True(check.CanApply);
        Assert.Null(check.Warning);
    }

    [Fact]
    public void InvalidCombination_IsRejectedWithoutRegistration()
    {
        var manager = CreateManager(out var hotkeys, out _, available: true);

        var error = manager.SetHotkey("textpin", "toggle", "V");

        Assert.NotNull(error);
        Assert.Empty(hotkeys.Registered);
    }

    [Fact]
    public void ResetWhenNotCustomized_IsNoOp()
    {
        var manager = CreateManager(out var hotkeys, out _, available: true);

        manager.ResetHotkey("textpin", "toggle");

        Assert.Empty(hotkeys.Registered);
    }

    /// <summary>假的热键注册器：记录调用，不碰 Win32。</summary>
    private sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
    {
        public List<string> Registered { get; } = new();

        public string? Register(string combination, Action callback)
        {
            Registered.Add(combination);
            return null;
        }

        public void UnregisterAll() => Registered.Clear();

        public void Dispose() => UnregisterAll();
    }
}
