using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Plugins.Recorder.Core;

/// <summary>
/// 录屏插件在宿主「设置中心」里暴露的设置项声明，以及把改动应用到
/// <see cref="RecorderSettings"/> 的逻辑。
///
/// 声明与应用都是纯数据变换，不碰 WPF，因此可以脱离界面单元测试。
/// 值域约定与截图插件一致：宿主传来的 JSON 可能是任何东西（手输、旧版本残留），
/// 非法值一律忽略并保留原值，绝不把设置对象改坏。
/// </summary>
public static class RecorderSettingsSchema
{
    public const string KeySaveDirectory = "saveDirectory";
    public const string KeyQuality = "quality";
    public const string KeyFps = "fps";
    public const string KeyRecordSystemAudio = "recordSystemAudio";
    public const string KeyRecordMicrophone = "recordMicrophone";
    public const string KeyRecordCursor = "recordCursor";
    public const string KeyFullScreenTarget = "fullScreenTarget";
    public const string KeyShowControlBar = "showControlBar";
    public const string KeyOpenFolderAfterStop = "openFolderAfterStop";
    public const string KeyAskOnSave = "askOnSave";

    private const string GroupSave = "保存";
    private const string GroupQuality = "画质与体积";
    private const string GroupAudio = "声音";
    private const string GroupCapture = "录制";

    /// <summary>帧率允许的范围。界面只给 8/10/15 三档，但旧配置里的其他值只要在范围内也接受。</summary>
    public const int MinFps = 5;
    public const int MaxFps = 20;

    public static IReadOnlyList<SettingDescriptor> Describe(RecorderSettings settings)
    {
        var withAudio = settings.RecordSystemAudio || settings.RecordMicrophone;

        return
        [
            new SettingDescriptor
            {
                Key = KeySaveDirectory,
                Label = "保存目录",
                Kind = SettingKind.DirectoryPath,
                Group = GroupSave,
                Description = "留空则保存在插件数据目录的 recordings 下。",
                Value = JsonSerializer.SerializeToElement(settings.SaveDirectory),
                DefaultValue = JsonSerializer.SerializeToElement(""),
                Placeholder = AppPaths.DefaultRecordingsDirectory,
            },
            new SettingDescriptor
            {
                Key = KeyAskOnSave,
                Label = "录制完成后询问是否压缩",
                Kind = SettingKind.Bool,
                Group = GroupSave,
                Description = "关闭则停止后按录制画质直接保存，不再询问是否压缩。",
                Value = JsonSerializer.SerializeToElement(settings.AskOnSave),
                DefaultValue = JsonSerializer.SerializeToElement(true),
            },
            new SettingDescriptor
            {
                Key = KeyOpenFolderAfterStop,
                Label = "录制完成后定位到文件",
                Kind = SettingKind.Bool,
                Group = GroupSave,
                Description = "保存完成后打开资源管理器并选中该文件。",
                Value = JsonSerializer.SerializeToElement(settings.OpenFolderAfterStop),
                DefaultValue = JsonSerializer.SerializeToElement(false),
            },

            new SettingDescriptor
            {
                Key = KeyQuality,
                Label = "画质档位",
                Kind = SettingKind.Choice,
                Group = GroupQuality,
                Description = settings.Quality == RecordingQuality.Original
                    ? "按屏幕原始像素录制，不缩放；停止后可选择是否压缩保存。"
                    : "档位决定码率与最大宽度，不放大源画面。" +
                      RecordingQualityProfile.DescribeEstimate(settings.Quality, withAudio) + "。",
                Value = JsonSerializer.SerializeToElement(settings.Quality.ToString()),
                DefaultValue = JsonSerializer.SerializeToElement(nameof(RecordingQuality.Compact)),
                Choices =
                [
                    new SettingChoice
                    {
                        Value = nameof(RecordingQuality.Compact),
                        Label = FormatChoiceLabel(RecordingQuality.Compact, withAudio),
                    },
                    new SettingChoice
                    {
                        Value = nameof(RecordingQuality.Standard),
                        Label = FormatChoiceLabel(RecordingQuality.Standard, withAudio),
                    },
                    new SettingChoice
                    {
                        Value = nameof(RecordingQuality.Sharp),
                        Label = FormatChoiceLabel(RecordingQuality.Sharp, withAudio),
                    },
                    new SettingChoice
                    {
                        Value = nameof(RecordingQuality.Original),
                        Label = "原始分辨率（不缩放）",
                    },
                ],
            },
            new SettingDescriptor
            {
                Key = KeyFps,
                Label = "帧率",
                Kind = SettingKind.Choice,
                Group = GroupQuality,
                Description = "静态演示 8 帧足够；需要看清鼠标移动时用 15 帧，文件也会更大。",
                Value = JsonSerializer.SerializeToElement(settings.Fps.ToString()),
                DefaultValue = JsonSerializer.SerializeToElement("10"),
                Choices =
                [
                    new SettingChoice { Value = "8", Label = "8 帧/秒（更省）" },
                    new SettingChoice { Value = "10", Label = "10 帧/秒（推荐）" },
                    new SettingChoice { Value = "15", Label = "15 帧/秒（更流畅）" },
                ],
            },

            new SettingDescriptor
            {
                Key = KeyRecordSystemAudio,
                Label = "录制系统声音",
                Kind = SettingKind.Bool,
                Group = GroupAudio,
                Description = "录制扬声器正在播放的声音（回环捕获）。",
                Value = JsonSerializer.SerializeToElement(settings.RecordSystemAudio),
                DefaultValue = JsonSerializer.SerializeToElement(true),
            },
            new SettingDescriptor
            {
                Key = KeyRecordMicrophone,
                Label = "录制麦克风",
                Kind = SettingKind.Bool,
                Group = GroupAudio,
                Description = "与系统声音同时开启时会混合录制。",
                Value = JsonSerializer.SerializeToElement(settings.RecordMicrophone),
                DefaultValue = JsonSerializer.SerializeToElement(false),
            },

            new SettingDescriptor
            {
                Key = KeyRecordCursor,
                Label = "录制鼠标指针",
                Kind = SettingKind.Bool,
                Group = GroupCapture,
                Value = JsonSerializer.SerializeToElement(settings.RecordCursor),
                DefaultValue = JsonSerializer.SerializeToElement(true),
            },
            new SettingDescriptor
            {
                Key = KeyFullScreenTarget,
                Label = "全屏录制范围",
                Kind = SettingKind.Choice,
                Group = GroupCapture,
                Description = "区域录制不受此项影响。",
                Value = JsonSerializer.SerializeToElement(settings.FullScreenTarget.ToString()),
                DefaultValue = JsonSerializer.SerializeToElement(nameof(MonitorTarget.Cursor)),
                Choices =
                [
                    new SettingChoice { Value = nameof(MonitorTarget.Cursor), Label = "鼠标所在的屏幕" },
                    new SettingChoice { Value = nameof(MonitorTarget.Primary), Label = "主屏幕" },
                ],
            },
            new SettingDescriptor
            {
                Key = KeyShowControlBar,
                Label = "显示悬浮控制条",
                Kind = SettingKind.Bool,
                Group = GroupCapture,
                Description = "控制条不会录进画面；关闭后只能用热键或托盘菜单停止录制。",
                Value = JsonSerializer.SerializeToElement(settings.ShowControlBar),
                DefaultValue = JsonSerializer.SerializeToElement(true),
            },
        ];
    }

    /// <summary>
    /// 把一项改动应用到设置对象。返回是否真的改动了（用于决定要不要保存）。
    /// 键未知、类型不符、越界时返回 false 且不修改任何字段。
    /// </summary>
    public static bool Apply(RecorderSettings settings, string key, JsonElement value)
    {
        switch (key)
        {
            case KeySaveDirectory:
                return SetString(() => settings.SaveDirectory, v => settings.SaveDirectory = v, value);

            case KeyOpenFolderAfterStop:
                return SetBool(() => settings.OpenFolderAfterStop, v => settings.OpenFolderAfterStop = v, value);

            case KeyAskOnSave:
                return SetBool(() => settings.AskOnSave, v => settings.AskOnSave = v, value);

            case KeyQuality:
                if (!TryGetString(value, out var qualityText)) return false;
                if (!Enum.TryParse<RecordingQuality>(qualityText, ignoreCase: true, out var quality)
                    || !Enum.IsDefined(quality)) return false;
                if (settings.Quality == quality) return false;
                settings.Quality = quality;
                return true;

            case KeyFps:
                return SetInt(() => settings.Fps, v => settings.Fps = v, value, MinFps, MaxFps);

            case KeyRecordSystemAudio:
                return SetBool(() => settings.RecordSystemAudio, v => settings.RecordSystemAudio = v, value);

            case KeyRecordMicrophone:
                return SetBool(() => settings.RecordMicrophone, v => settings.RecordMicrophone = v, value);

            case KeyRecordCursor:
                return SetBool(() => settings.RecordCursor, v => settings.RecordCursor = v, value);

            case KeyFullScreenTarget:
                if (!TryGetString(value, out var targetText)) return false;
                if (!Enum.TryParse<MonitorTarget>(targetText, ignoreCase: true, out var target)
                    || !Enum.IsDefined(target)) return false;
                if (settings.FullScreenTarget == target) return false;
                settings.FullScreenTarget = target;
                return true;

            case KeyShowControlBar:
                return SetBool(() => settings.ShowControlBar, v => settings.ShowControlBar = v, value);

            default:
                return false;
        }
    }

    /// <summary>批量应用改动，返回是否有任何一项生效。</summary>
    public static bool ApplyAll(RecorderSettings settings, IEnumerable<SettingChange> changes)
    {
        var changed = false;
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Key)) continue;
            if (Apply(settings, change.Key, change.Value)) changed = true;
        }
        return changed;
    }

    private static string FormatChoiceLabel(RecordingQuality quality, bool withAudio)
    {
        var bytes = RecordingQualityProfile.TryEstimateBytesPerMinute(quality, withAudio);
        if (bytes is null) return RecordingQualityProfile.Label(quality);
        return $"{RecordingQualityProfile.Label(quality)}（{FormatMb(bytes.Value)} MB/分钟）";
    }

    private static string FormatMb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1");

    // ---- 取值助手（与截图插件保持同一套语义） ----

    private static bool SetString(Func<string> get, Action<string> apply, JsonElement value)
    {
        if (!TryGetString(value, out var text)) return false;
        if (string.Equals(get(), text, StringComparison.Ordinal)) return false;
        apply(text);
        return true;
    }

    private static bool SetBool(Func<bool> get, Action<bool> apply, JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        var parsed = value.GetBoolean();
        if (get() == parsed) return false;
        apply(parsed);
        return true;
    }

    private static bool SetInt(Func<int> get, Action<int> apply, JsonElement value, int min, int max)
    {
        if (!TryGetNumber(value, out var number)) return false;
        if (double.IsNaN(number)) return false;

        var clamped = (int)Math.Round(Math.Clamp(number, min, max));
        if (get() == clamped) return false;
        apply(clamped);
        return true;
    }

    private static bool TryGetNumber(JsonElement value, out double number)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDouble(out number);
            case JsonValueKind.String:
                return double.TryParse(value.GetString(), out number);
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryGetString(JsonElement value, out string text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text = value.GetString() ?? "";
                return true;
            case JsonValueKind.Number:
                text = value.GetRawText();
                return true;
            default:
                text = "";
                return false;
        }
    }
}
