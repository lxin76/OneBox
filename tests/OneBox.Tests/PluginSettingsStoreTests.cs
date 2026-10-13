using System.IO;
using System.Text.Json;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>插件私有设置的宿主侧存储：读、写、落盘、缓存与丢弃。</summary>
public sealed class PluginSettingsStoreTests : IDisposable
{
    private readonly string _root;
    private const string PluginId = "settings-demo";

    public PluginSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onebox-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        HostPaths.DataDirectoryOverride = _root;
    }

    public void Dispose()
    {
        HostPaths.DataDirectoryOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void MissingKey_ReportsNotExists()
    {
        PluginSettingsStore.Evict(PluginId);
        var store = PluginSettingsStore.ForPlugin(PluginId);

        var value = store.Get("nothing");

        Assert.False(value.Exists);
    }

    [Fact]
    public void SetThenGet_Roundtrips()
    {
        PluginSettingsStore.Evict(PluginId);
        var store = PluginSettingsStore.ForPlugin(PluginId);

        store.Set("note", Json("\"你好\""));
        var value = store.Get("note");

        Assert.True(value.Exists);
        Assert.Equal("你好", value.Value!.Value.GetString());
    }

    [Fact]
    public void Set_WritesJsonFileToPluginDataDirectory()
    {
        PluginSettingsStore.Evict(PluginId);
        var store = PluginSettingsStore.ForPlugin(PluginId);

        store.Set("count", Json("42"));

        var path = Path.Combine(HostPaths.PluginDataDirectory(PluginId), "settings.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("count", text);
        Assert.Contains("42", text);
    }

    [Fact]
    public void Values_PersistAcrossStoreRecreation()
    {
        PluginSettingsStore.Evict(PluginId);
        PluginSettingsStore.ForPlugin(PluginId).Set("persisted", Json("\"kept\""));

        // 丢弃缓存，模拟宿主重启后重新读取
        PluginSettingsStore.Evict(PluginId);
        var reopened = PluginSettingsStore.ForPlugin(PluginId);

        Assert.Equal("kept", reopened.Get("persisted").Value!.Value.GetString());
    }

    [Fact]
    public void DifferentPlugins_AreIsolated()
    {
        PluginSettingsStore.Evict("plugin-a");
        PluginSettingsStore.Evict("plugin-b");

        PluginSettingsStore.ForPlugin("plugin-a").Set("key", Json("\"a\""));
        PluginSettingsStore.ForPlugin("plugin-b").Set("key", Json("\"b\""));

        Assert.Equal("a", PluginSettingsStore.ForPlugin("plugin-a").Get("key").Value!.Value.GetString());
        Assert.Equal("b", PluginSettingsStore.ForPlugin("plugin-b").Get("key").Value!.Value.GetString());
    }

    [Fact]
    public void Set_OverwritesExistingValue()
    {
        PluginSettingsStore.Evict(PluginId);
        var store = PluginSettingsStore.ForPlugin(PluginId);

        store.Set("k", Json("1"));
        store.Set("k", Json("2"));

        Assert.Equal(2, store.Get("k").Value!.Value.GetInt32());
    }

    [Fact]
    public void Evict_DiscardsCacheSoDiskIsReread()
    {
        PluginSettingsStore.Evict(PluginId);
        PluginSettingsStore.ForPlugin(PluginId).Set("k", Json("\"from-store\""));

        // 绕过 store 直接改盘，验证 Evict 后确实重新读盘
        var path = Path.Combine(HostPaths.PluginDataDirectory(PluginId), "settings.json");
        File.WriteAllText(path, """{"k":"from-disk"}""");
        PluginSettingsStore.Evict(PluginId);

        Assert.Equal("from-disk", PluginSettingsStore.ForPlugin(PluginId).Get("k").Value!.Value.GetString());
    }

    [Fact]
    public void CorruptedSettingsFile_FallsBackToEmpty()
    {
        var dir = HostPaths.PluginDataDirectory(PluginId);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{ 这不是 JSON");
        PluginSettingsStore.Evict(PluginId);

        var store = PluginSettingsStore.ForPlugin(PluginId);

        Assert.False(store.Get("k").Exists);
        // 损坏后写入应恢复正常
        store.Set("k", Json("\"ok\""));
        Assert.Equal("ok", store.Get("k").Value!.Value.GetString());
    }

    [Fact]
    public void Set_SupportsStructuredValues()
    {
        PluginSettingsStore.Evict(PluginId);
        var store = PluginSettingsStore.ForPlugin(PluginId);

        store.Set("obj", Json("""{"a":1,"b":[1,2,3]}"""));

        var value = store.Get("obj").Value!.Value;
        Assert.Equal(1, value.GetProperty("a").GetInt32());
        Assert.Equal(3, value.GetProperty("b").GetArrayLength());
    }
}
