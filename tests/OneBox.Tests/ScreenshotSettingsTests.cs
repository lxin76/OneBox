using System.IO;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class ScreenshotSettingsTests
{
    [Fact]
    public void 保存目录为空时回退到默认目录()
    {
        var settings = new ScreenshotSettings { SaveDirectory = "" };
        Assert.Equal(AppPaths.DefaultSaveDirectory, settings.ResolveSaveDirectory());

        settings.SaveDirectory = "   ";
        Assert.Equal(AppPaths.DefaultSaveDirectory, settings.ResolveSaveDirectory());
    }

    [Fact]
    public void 指定目录时按原样使用()
    {
        var settings = new ScreenshotSettings { SaveDirectory = @"D:\Shots" };
        Assert.Equal(@"D:\Shots", settings.ResolveSaveDirectory());
    }

    [Fact]
    public void 读写往返保持一致()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OneBoxTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new ScreenshotSettings
            {
                SaveDirectory = @"D:\Shots",
                FileNameTemplate = "Shot_{yyyyMMdd}",
                Format = ShotImageFormat.Jpeg,
                JpegQuality = 80,
                CopyToClipboard = false,
                PinTopmost = false,
                ScrollWheelNotches = 5,
                ScrollMaxHeight = 12345,
                OcrLanguage = "zh-Hans-CN",
                OcrStripCjkSpaces = false,
                OcrEnhanceImage = false,
                RegionHotkey = "Ctrl+Shift+A",
            };

            SettingsStore.Save(settings, path);
            var loaded = SettingsStore.Load(path);

            Assert.Equal(settings.SaveDirectory, loaded.SaveDirectory);
            Assert.Equal(settings.FileNameTemplate, loaded.FileNameTemplate);
            Assert.Equal(ShotImageFormat.Jpeg, loaded.Format);
            Assert.Equal(80, loaded.JpegQuality);
            Assert.False(loaded.CopyToClipboard);
            Assert.False(loaded.PinTopmost);
            Assert.Equal(5, loaded.ScrollWheelNotches);
            Assert.Equal(12345, loaded.ScrollMaxHeight);
            Assert.Equal("zh-Hans-CN", loaded.OcrLanguage);
            Assert.False(loaded.OcrStripCjkSpaces);
            Assert.False(loaded.OcrEnhanceImage);
            Assert.Equal("Ctrl+Shift+A", loaded.RegionHotkey);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 设置文件损坏时回退默认值()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OneBoxTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ 这不是合法 JSON");

            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.CopyToClipboard);
            Assert.Equal("Ctrl+Alt+A", loaded.RegionHotkey);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 文件不存在时使用默认值()
    {
        var path = Path.Combine(Path.GetTempPath(), "OneBoxTests", Guid.NewGuid().ToString("N"), "none.json");
        var loaded = SettingsStore.Load(path);

        Assert.Equal("Shot_{yyyyMMdd_HHmmss}", loaded.FileNameTemplate);
        Assert.Equal(ShotImageFormat.Png, loaded.Format);
    }

    [Fact]
    public void 默认热键不与置顶文本插件冲突()
    {
        var settings = new ScreenshotSettings();

        // 置顶文本插件占用 Ctrl+Alt+V / Ctrl+Alt+P / Ctrl+Alt+C / Ctrl+Alt+X，截图不能用同样的组合
        var clipboardKeys = new[] { "Ctrl+Alt+V", "Ctrl+Alt+P", "Ctrl+Alt+C", "Ctrl+Alt+X" };
        var shotKeys = new[]
        {
            settings.RegionHotkey, settings.FullScreenHotkey,
            settings.ScrollHotkey, settings.OcrClipboardHotkey,
        };

        Assert.Empty(shotKeys.Intersect(clipboardKeys));
        // 截图插件内部也不能重复
        Assert.Equal(shotKeys.Length, shotKeys.Distinct().Count());
    }
}
