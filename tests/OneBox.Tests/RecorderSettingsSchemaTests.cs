using System.Text.Json;
using OneBox.Abstractions;
using OneBox.Plugins.Recorder.Core;

namespace OneBox.Tests;

/// <summary>
/// 录屏插件设置声明的完整性，以及把设置中心回传的改动应用到设置对象的健壮性。
/// 重点是：任何非法输入都不得把设置对象改坏。
/// </summary>
public class RecorderSettingsSchemaTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static SettingChange Change(string key, string rawJson)
        => new() { Key = key, Value = Json(rawJson) };

    [Fact]
    public void 声明覆盖所有设置项()
    {
        var keys = RecorderSettingsSchema.Describe(new RecorderSettings())
            .Select(d => d.Key)
            .ToList();

        Assert.Contains(RecorderSettingsSchema.KeySaveDirectory, keys);
        Assert.Contains(RecorderSettingsSchema.KeyOpenFolderAfterStop, keys);
        Assert.Contains(RecorderSettingsSchema.KeyAskOnSave, keys);
        Assert.Contains(RecorderSettingsSchema.KeyQuality, keys);
        Assert.Contains(RecorderSettingsSchema.KeyFps, keys);
        Assert.Contains(RecorderSettingsSchema.KeyRecordSystemAudio, keys);
        Assert.Contains(RecorderSettingsSchema.KeyRecordMicrophone, keys);
        Assert.Contains(RecorderSettingsSchema.KeyRecordCursor, keys);
        Assert.Contains(RecorderSettingsSchema.KeyFullScreenTarget, keys);
        Assert.Contains(RecorderSettingsSchema.KeyShowControlBar, keys);
    }

    [Fact]
    public void 声明里没有重复的键()
    {
        var keys = RecorderSettingsSchema.Describe(new RecorderSettings()).Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void 每个设置项都有标题和分组()
    {
        foreach (var descriptor in RecorderSettingsSchema.Describe(new RecorderSettings()))
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Label), $"{descriptor.Key} 缺少 Label");
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Group), $"{descriptor.Key} 缺少 Group");
        }
    }

    [Fact]
    public void Choice项都提供了候选项()
    {
        foreach (var descriptor in RecorderSettingsSchema.Describe(new RecorderSettings())
                     .Where(d => d.Kind == SettingKind.Choice))
        {
            Assert.NotEmpty(descriptor.Choices);
            Assert.All(descriptor.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Value)));
        }
    }

    [Fact]
    public void 声明回显当前值()
    {
        var settings = new RecorderSettings
        {
            Fps = 15,
            Quality = RecordingQuality.Sharp,
            RecordSystemAudio = false,
            RecordMicrophone = true,
            FullScreenTarget = MonitorTarget.Primary,
        };

        var descriptors = RecorderSettingsSchema.Describe(settings).ToDictionary(d => d.Key);

        Assert.Equal("15", descriptors[RecorderSettingsSchema.KeyFps].Value!.Value.GetString());
        Assert.Equal("Sharp", descriptors[RecorderSettingsSchema.KeyQuality].Value!.Value.GetString());
        Assert.False(descriptors[RecorderSettingsSchema.KeyRecordSystemAudio].Value!.Value.GetBoolean());
        Assert.True(descriptors[RecorderSettingsSchema.KeyRecordMicrophone].Value!.Value.GetBoolean());
        Assert.Equal("Primary", descriptors[RecorderSettingsSchema.KeyFullScreenTarget].Value!.Value.GetString());
    }

    [Fact]
    public void 画质档位包含原始分辨率并说明不缩放()
    {
        var settings = new RecorderSettings { Quality = RecordingQuality.Original };
        var descriptors = RecorderSettingsSchema.Describe(settings).ToDictionary(d => d.Key);

        var quality = descriptors[RecorderSettingsSchema.KeyQuality];
        Assert.Contains(quality.Choices!, c => c.Value == nameof(RecordingQuality.Original));
        Assert.Contains(quality.Choices!, c => c.Label.Contains("不缩放"));
        Assert.Contains("停止后可选择是否压缩保存", quality.Description);
    }

    [Fact]
    public void 画质档位说明带体积估算并且随音频开关变化()
    {
        var withAudio = RecorderSettingsSchema.Describe(new RecorderSettings { RecordSystemAudio = true })
            .Single(d => d.Key == RecorderSettingsSchema.KeyQuality);
        var videoOnly = RecorderSettingsSchema.Describe(new RecorderSettings { RecordSystemAudio = false, RecordMicrophone = false })
            .Single(d => d.Key == RecorderSettingsSchema.KeyQuality);

        Assert.Contains("MB/分钟", withAudio.Description);
        Assert.Contains("含声音", withAudio.Description);
        Assert.Contains("仅画面", videoOnly.Description);
        Assert.NotEqual(withAudio.Description, videoOnly.Description);
    }

    [Fact]
    public void 应用画质与声音设置()
    {
        var settings = new RecorderSettings();

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyQuality, Json("\"Standard\"")));
        Assert.Equal(RecordingQuality.Standard, settings.Quality);

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyRecordMicrophone, Json("true")));
        Assert.True(settings.RecordMicrophone);

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyFullScreenTarget, Json("\"Primary\"")));
        Assert.Equal(MonitorTarget.Primary, settings.FullScreenTarget);

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyAskOnSave, Json("false")));
        Assert.False(settings.AskOnSave);
    }

    [Fact]
    public void 帧率超范围时夹取到边界()
    {
        var settings = new RecorderSettings();

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyFps, Json("100")));
        Assert.Equal(RecorderSettingsSchema.MaxFps, settings.Fps);

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyFps, Json("1")));
        Assert.Equal(RecorderSettingsSchema.MinFps, settings.Fps);

        Assert.True(RecorderSettingsSchema.Apply(settings, RecorderSettingsSchema.KeyFps, Json("\"12\"")));
        Assert.Equal(12, settings.Fps);
    }

    [Theory]
    [InlineData("quality", "\"NotAQuality\"")]
    [InlineData("quality", "42")]
    [InlineData("fps", "\"abc\"")]
    [InlineData("fps", "null")]
    [InlineData("recordSystemAudio", "\"yes\"")]
    [InlineData("fullScreenTarget", "\"Nowhere\"")]
    public void 非法输入不修改设置(string key, string rawJson)
    {
        var settings = new RecorderSettings
        {
            Quality = RecordingQuality.Compact,
            Fps = 10,
            RecordSystemAudio = true,
            FullScreenTarget = MonitorTarget.Cursor,
        };

        Assert.False(RecorderSettingsSchema.Apply(settings, key, Json(rawJson)));

        Assert.Equal(RecordingQuality.Compact, settings.Quality);
        Assert.Equal(10, settings.Fps);
        Assert.True(settings.RecordSystemAudio);
        Assert.Equal(MonitorTarget.Cursor, settings.FullScreenTarget);
    }

    [Fact]
    public void 未知键被忽略且批量应用只报告真实改动()
    {
        var settings = new RecorderSettings();

        Assert.False(RecorderSettingsSchema.Apply(settings, "unknown", Json("true")));
        Assert.False(RecorderSettingsSchema.ApplyAll(settings, [Change("unknown", "true")]));
        Assert.True(RecorderSettingsSchema.ApplyAll(settings, [Change("recordCursor", "false")]));
    }

    [Fact]
    public void 空目录表示使用默认录制目录()
    {
        var settings = new RecorderSettings { SaveDirectory = "" };

        Assert.Equal(AppPaths.DefaultRecordingsDirectory, settings.ResolveSaveDirectory());

        settings.SaveDirectory = @"D:\Recordings";
        Assert.Equal(@"D:\Recordings", settings.ResolveSaveDirectory());
    }
}