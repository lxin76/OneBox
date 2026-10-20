using System.IO;
using OneBox.Plugins.TextPin.Core;

namespace OneBox.Tests;

public class SettingsStoreTests
{
    private readonly string _dir;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"textpin-settings-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var settings = SettingsStore.Load(Path.Combine(_dir, "none.json"));
        Assert.True(settings.Pinned);
        Assert.True(settings.NoActivate);
        Assert.True(settings.WordWrap);
        Assert.Equal(13, settings.FontSize);
        Assert.Equal(200_000, settings.MaxTextLength);
        Assert.Equal("Ctrl+Alt+V", settings.ToggleHotkey);
        Assert.Equal("Ctrl+Alt+C", settings.CaptureHotkey);
    }

    [Fact]
    public void SaveAndLoad_Roundtrip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new Settings
        {
            Pinned = false,
            NoActivate = false,
            WordWrap = false,
            FontSize = 18.5,
            MaxTextLength = 1234,
            ToggleHotkey = "Ctrl+Alt+Q",
            CaptureHotkey = "Ctrl+Alt+W",
        };
        SettingsStore.Save(settings, path);

        var loaded = SettingsStore.Load(path);
        Assert.False(loaded.Pinned);
        Assert.False(loaded.NoActivate);
        Assert.False(loaded.WordWrap);
        Assert.Equal(18.5, loaded.FontSize);
        Assert.Equal(1234, loaded.MaxTextLength);
        Assert.Equal("Ctrl+Alt+Q", loaded.ToggleHotkey);
        Assert.Equal("Ctrl+Alt+W", loaded.CaptureHotkey);
    }

    [Fact]
    public void 旧版设置里的已删除键被忽略()
    {
        // 升级前的 settings.json 里还有 AutoPaste / PasteHotkey / CopyHotkey，
        // 这些键现在已取消，读回来只应保留仍然存在的字段。
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {
              "Pinned": false,
              "AutoPaste": false,
              "PasteHotkey": "Ctrl+Alt+P",
              "CopyHotkey": "Ctrl+Alt+X",
              "CaptureHotkey": "Ctrl+Alt+C"
            }
            """);

        var loaded = SettingsStore.Load(path);

        Assert.False(loaded.Pinned);
        Assert.Equal("Ctrl+Alt+C", loaded.CaptureHotkey);
        Assert.True(loaded.NoActivate);
    }

    [Fact]
    public void CorruptedFile_ReturnsDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ 这不是 JSON !!!");
        var loaded = SettingsStore.Load(path);
        Assert.True(loaded.Pinned);
    }
}
