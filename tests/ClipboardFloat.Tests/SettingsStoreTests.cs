using System.IO;
using ClipboardFloat.Core;

namespace ClipboardFloat.Tests;

public class SettingsStoreTests
{
    private readonly string _dir;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"cf-settings-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var settings = SettingsStore.Load(Path.Combine(_dir, "none.json"));
        Assert.True(settings.AutoPaste);
        Assert.True(settings.Pinned);
        Assert.Equal(500, settings.MaxHistory);
        Assert.Equal("Ctrl+Alt+V", settings.PanelHotkey);
        Assert.Equal("Ctrl+Alt+P", settings.PasteHotkey);
        Assert.Equal("Ctrl+Alt+C", settings.ViewHotkey);
    }

    [Fact]
    public void SaveAndLoad_Roundtrip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new Settings
        {
            AutoPaste = false,
            Pinned = false,
            MaxHistory = 123,
            PanelHotkey = "Ctrl+Alt+Q",
            PasteHotkey = "Ctrl+Shift+Z",
            ViewHotkey = "Ctrl+Alt+X",
        };
        SettingsStore.Save(settings, path);

        var loaded = SettingsStore.Load(path);
        Assert.False(loaded.AutoPaste);
        Assert.False(loaded.Pinned);
        Assert.Equal(123, loaded.MaxHistory);
        Assert.Equal("Ctrl+Alt+Q", loaded.PanelHotkey);
        Assert.Equal("Ctrl+Shift+Z", loaded.PasteHotkey);
        Assert.Equal("Ctrl+Alt+X", loaded.ViewHotkey);
    }

    [Fact]
    public void CorruptedFile_ReturnsDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ 这不是 JSON !!!");
        var loaded = SettingsStore.Load(path);
        Assert.True(loaded.AutoPaste);
    }
}
