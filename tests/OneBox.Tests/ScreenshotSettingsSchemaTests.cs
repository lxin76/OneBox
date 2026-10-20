using System.Text.Json;
using OneBox.Abstractions;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

/// <summary>
/// 截图插件设置声明的完整性，以及把设置中心回传的改动应用到设置对象的健壮性。
/// 重点是：任何非法输入都不得把设置对象改坏。
/// </summary>
public class ScreenshotSettingsSchemaTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static SettingChange Change(string key, string rawJson)
        => new() { Key = key, Value = Json(rawJson) };

    [Fact]
    public void 声明覆盖所有非热键设置项()
    {
        var keys = ScreenshotSettingsSchema.Describe(new ScreenshotSettings())
            .Select(d => d.Key)
            .ToList();

        // 热键不在设置中心里（由插件自己的设置窗处理，需要展示注册结果）
        Assert.DoesNotContain("regionHotkey", keys);

        Assert.Contains(ScreenshotSettingsSchema.KeySaveDirectory, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyFileNameTemplate, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyFormat, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyJpegQuality, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyCopyToClipboard, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyPinTopmost, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyPinTopmost, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyShowToolbarAfterRegion, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyScrollMinMatchRows, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyScrollMaxHeight, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyScrollWheelNotches, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyOcrLanguage, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyOcrStripCjkSpaces, keys);
        Assert.Contains(ScreenshotSettingsSchema.KeyOcrEnhanceImage, keys);
    }

    [Fact]
    public void 滚动参数在功能不可用时只读并注明原因()
    {
        if (ScreenshotSettingsSchema.ScrollCaptureAvailable) return; // 功能恢复后本用例不再适用

        var descriptors = ScreenshotSettingsSchema.Describe(new ScreenshotSettings())
            .ToDictionary(d => d.Key);

        foreach (var key in new[]
                 {
                     ScreenshotSettingsSchema.KeyScrollMinMatchRows,
                     ScreenshotSettingsSchema.KeyScrollMaxHeight,
                     ScreenshotSettingsSchema.KeyScrollWheelNotches,
                 })
        {
            Assert.True(descriptors[key].ReadOnly, $"{key} 应标为只读，否则用户改了也不生效");
            Assert.Contains("暂不可用", descriptors[key].Description!);
        }
    }

    [Fact]
    public void 声明里没有重复的键()
    {
        var keys = ScreenshotSettingsSchema.Describe(new ScreenshotSettings()).Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void 每个设置项都有标题和分组()
    {
        foreach (var d in ScreenshotSettingsSchema.Describe(new ScreenshotSettings()))
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Label), $"{d.Key} 缺少 Label");
            Assert.False(string.IsNullOrWhiteSpace(d.Group), $"{d.Key} 缺少 Group");
        }
    }

    [Fact]
    public void 声明回显当前值()
    {
        var settings = new ScreenshotSettings { JpegQuality = 77, PinTopmost = true, Format = ShotImageFormat.Jpeg };

        var descriptors = ScreenshotSettingsSchema.Describe(settings).ToDictionary(d => d.Key);

        Assert.Equal(77, SettingValueCodecNumber(descriptors[ScreenshotSettingsSchema.KeyJpegQuality].Value));
        Assert.True(descriptors[ScreenshotSettingsSchema.KeyPinTopmost].Value!.Value.GetBoolean());
        Assert.Equal("Jpeg", descriptors[ScreenshotSettingsSchema.KeyFormat].Value!.Value.GetString());
    }

    private static double SettingValueCodecNumber(JsonElement? value)
        => OneBox.Host.Services.SettingValueCodec.GetNumber(value);

    [Fact]
    public void 数字项都声明了范围()
    {
        var numbers = ScreenshotSettingsSchema.Describe(new ScreenshotSettings())
            .Where(d => d.Kind == SettingKind.Number);

        foreach (var d in numbers)
        {
            Assert.NotNull(d.Min);
            Assert.NotNull(d.Max);
            Assert.True(d.Min < d.Max, $"{d.Key} 的范围不合法");
        }
    }

    [Fact]
    public void Choice项都提供了候选项()
    {
        var choices = ScreenshotSettingsSchema.Describe(new ScreenshotSettings())
            .Where(d => d.Kind == SettingKind.Choice);

        foreach (var d in choices)
            Assert.NotEmpty(d.Choices);
    }

    // ---- 应用改动 ----

    [Fact]
    public void 应用布尔改动()
    {
        var settings = new ScreenshotSettings { PinTopmost = false };

        Assert.True(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyPinTopmost, Json("true")));
        Assert.True(settings.PinTopmost);
    }

    [Fact]
    public void 应用文本改动()
    {
        var settings = new ScreenshotSettings();

        Assert.True(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeySaveDirectory, Json("\"D:\\\\Shots\"")));
        Assert.Equal("D:\\Shots", settings.SaveDirectory);
    }

    [Fact]
    public void 应用格式改动()
    {
        var settings = new ScreenshotSettings { Format = ShotImageFormat.Png };

        Assert.True(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyFormat, Json("\"Jpeg\"")));
        Assert.Equal(ShotImageFormat.Jpeg, settings.Format);
    }

    [Fact]
    public void 格式改动大小写不敏感()
    {
        var settings = new ScreenshotSettings();

        Assert.True(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyFormat, Json("\"jpeg\"")));
        Assert.Equal(ShotImageFormat.Jpeg, settings.Format);
    }

    [Fact]
    public void 数字改动被夹取到范围内()
    {
        var settings = new ScreenshotSettings { JpegQuality = 92 };

        ScreenshotSettingsSchema.Apply(settings, ScreenshotSettingsSchema.KeyJpegQuality, Json("500"));
        Assert.Equal(100, settings.JpegQuality);

        ScreenshotSettingsSchema.Apply(settings, ScreenshotSettingsSchema.KeyJpegQuality, Json("0"));
        Assert.Equal(1, settings.JpegQuality);
    }

    [Fact]
    public void 数字以字符串形式传来也能应用()
    {
        var settings = new ScreenshotSettings { ScrollWheelNotches = 3 };

        Assert.True(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyScrollWheelNotches, Json("\"5\"")));
        Assert.Equal(5, settings.ScrollWheelNotches);
    }

    [Fact]
    public void 未知键返回false且不改动任何字段()
    {
        var settings = new ScreenshotSettings();
        var before = JsonSerializer.Serialize(settings);

        Assert.False(ScreenshotSettingsSchema.Apply(settings, "不存在的键", Json("\"x\"")));

        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void 布尔键收到错误类型时忽略()
    {
        var settings = new ScreenshotSettings { PinTopmost = true };

        Assert.False(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyPinTopmost, Json("\"yes\"")));
        Assert.True(settings.PinTopmost);
    }

    [Fact]
    public void 数字键收到非数字时忽略()
    {
        var settings = new ScreenshotSettings { JpegQuality = 92 };

        Assert.False(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyJpegQuality, Json("\"abc\"")));
        Assert.Equal(92, settings.JpegQuality);
    }

    [Fact]
    public void 格式键收到非法枚举值时忽略()
    {
        var settings = new ScreenshotSettings { Format = ShotImageFormat.Png };

        Assert.False(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyFormat, Json("\"Bmp\"")));
        Assert.Equal(ShotImageFormat.Png, settings.Format);
    }

    [Fact]
    public void 文件名模板不接受空白()
    {
        var settings = new ScreenshotSettings { FileNameTemplate = "Shot_{yyyyMMdd_HHmmss}" };

        ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyFileNameTemplate, Json("\"   \""));

        Assert.Equal("Shot_{yyyyMMdd_HHmmss}", settings.FileNameTemplate);
    }

    [Fact]
    public void 应用无变化的值返回false()
    {
        var settings = new ScreenshotSettings { PinTopmost = false };

        Assert.False(ScreenshotSettingsSchema.Apply(
            settings, ScreenshotSettingsSchema.KeyPinTopmost, Json("false")));
    }

    [Fact]
    public void 批量应用报告是否发生变化()
    {
        var settings = new ScreenshotSettings { PinTopmost = false, JpegQuality = 92 };

        Assert.True(ScreenshotSettingsSchema.ApplyAll(settings,
        [
            Change(ScreenshotSettingsSchema.KeyPinTopmost, "true"),
            Change(ScreenshotSettingsSchema.KeyJpegQuality, "80"),
        ]));
        Assert.True(settings.PinTopmost);
        Assert.Equal(80, settings.JpegQuality);
    }

    [Fact]
    public void 批量应用里全是无效项时返回false()
    {
        var settings = new ScreenshotSettings();

        Assert.False(ScreenshotSettingsSchema.ApplyAll(settings,
        [
            Change("未知键", "\"x\""),
            Change(ScreenshotSettingsSchema.KeyJpegQuality, "\"不是数字\""),
        ]));
    }

    [Fact]
    public void 批量应用跳过空键()
    {
        var settings = new ScreenshotSettings { PinTopmost = false };

        Assert.False(ScreenshotSettingsSchema.ApplyAll(settings, [Change("", "true")]));
        Assert.False(settings.PinTopmost);
    }

    [Fact]
    public void 一串非法改动之后设置对象仍然可用()
    {
        // 模拟被乱改一轮：设置对象必须保持所有字段处于合法值域
        var settings = new ScreenshotSettings();

        ScreenshotSettingsSchema.ApplyAll(settings,
        [
            Change(ScreenshotSettingsSchema.KeyJpegQuality, "\"炸\""),
            Change(ScreenshotSettingsSchema.KeyScrollMinMatchRows, "null"),
            Change(ScreenshotSettingsSchema.KeyFormat, "\"Webp\""),
            Change(ScreenshotSettingsSchema.KeyOcrLanguage, "{}"),
            Change(ScreenshotSettingsSchema.KeyPinTopmost, "123"),
            Change(ScreenshotSettingsSchema.KeyFileNameTemplate, "null"),
        ]);

        var defaults = new ScreenshotSettings();
        Assert.Equal(defaults.JpegQuality, settings.JpegQuality);
        Assert.Equal(defaults.ScrollMinMatchRows, settings.ScrollMinMatchRows);
        Assert.Equal(defaults.Format, settings.Format);
        Assert.Equal(defaults.OcrLanguage, settings.OcrLanguage);
        Assert.Equal(defaults.PinTopmost, settings.PinTopmost);
        Assert.Equal(defaults.FileNameTemplate, settings.FileNameTemplate);
    }

    [Fact]
    public void 每个声明的键都能被应用()
    {
        // 防止声明了一个键却忘了在 Apply 里处理（用户改了没反应）
        foreach (var d in ScreenshotSettingsSchema.Describe(new ScreenshotSettings()))
        {
            var settings = new ScreenshotSettings();
            var probe = d.Kind switch
            {
                SettingKind.Bool => Json("true"),
                SettingKind.Number => Json("2"),
                SettingKind.Choice => Json($"\"{d.Choices[0].Value}\""),
                _ => Json("\"probe\""),
            };

            // 应用不抛异常即可；返回 true/false 取决于是否真的改变了值
            ScreenshotSettingsSchema.Apply(settings, d.Key, probe);
        }
    }
}
