using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 截图插件在宿主「设置中心」里暴露的设置项声明，以及把改动应用到
/// <see cref="ScreenshotSettings"/> 的逻辑。
///
/// 为什么把"声明"和"应用"放在 Core：这两件事都是纯数据变换，不碰 WPF，
/// 因此可以脱离界面单元测试。插件主体只负责在合适时机调用它们。
///
/// 值域约定：宿主传来的 JSON 可能是任何东西（用户手输、旧版本残留），
/// 因此 <see cref="Apply"/> 对每个键都做类型与范围校验，非法值一律忽略并
/// 保留原值，绝不把设置对象改坏。
/// </summary>
public static class ScreenshotSettingsSchema
{
    // 设置键。用常量而非字面量，避免声明与应用两处写错拼写。
    public const string KeySaveDirectory = "saveDirectory";
    public const string KeyFileNameTemplate = "fileNameTemplate";
    public const string KeyFormat = "format";
    public const string KeyJpegQuality = "jpegQuality";
    public const string KeyCopyToClipboard = "copyToClipboard";
    public const string KeyPinTopmost = "pinTopmost";
    public const string KeyShowToolbarAfterRegion = "showToolbarAfterRegion";
    public const string KeyScrollMinMatchRows = "scrollMinMatchRows";
    public const string KeyScrollMaxHeight = "scrollMaxHeight";
    public const string KeyScrollWheelNotches = "scrollWheelNotches";
    public const string KeyOcrLanguage = "ocrLanguage";
    public const string KeyOcrStripCjkSpaces = "ocrStripCjkSpaces";
    public const string KeyOcrEnhanceImage = "ocrEnhanceImage";

    // 分组标题
    private const string GroupSave = "保存";
    private const string GroupCapture = "截图";
    private const string GroupPin = "贴图";
    private const string GroupScroll = "滚动截长图";
    private const string GroupOcr = "文字识别";

    /// <summary>
    /// 生成设置项声明。热键不在此列 —— 热键由插件自己的设置窗处理
    /// （需要展示"被占用"等注册结果，宿主通用表单表达不了）。
    /// </summary>
    public static IReadOnlyList<SettingDescriptor> Describe(ScreenshotSettings settings) =>
    [
        new SettingDescriptor
        {
            Key = KeySaveDirectory,
            Label = "保存目录",
            Kind = SettingKind.DirectoryPath,
            Group = GroupSave,
            Description = "留空则使用系统图片目录。",
            Value = JsonSerializer.SerializeToElement(settings.SaveDirectory),
            DefaultValue = JsonSerializer.SerializeToElement(""),
            Placeholder = AppPaths.DefaultSaveDirectory,
        },
        new SettingDescriptor
        {
            Key = KeyFileNameTemplate,
            Label = "文件名模板",
            Kind = SettingKind.Text,
            Group = GroupSave,
            Description = "支持 {yyyy}/{MM}/{dd}/{HH}/{mm}/{ss}/{fff} 时间占位符；不要写扩展名。",
            Value = JsonSerializer.SerializeToElement(settings.FileNameTemplate),
            DefaultValue = JsonSerializer.SerializeToElement("Shot_{yyyyMMdd_HHmmss}"),
        },
        new SettingDescriptor
        {
            Key = KeyFormat,
            Label = "图片格式",
            Kind = SettingKind.Choice,
            Group = GroupSave,
            Value = JsonSerializer.SerializeToElement(settings.Format.ToString()),
            DefaultValue = JsonSerializer.SerializeToElement(nameof(ShotImageFormat.Png)),
            Choices =
            [
                new SettingChoice { Value = nameof(ShotImageFormat.Png), Label = "PNG（无损）" },
                new SettingChoice { Value = nameof(ShotImageFormat.Jpeg), Label = "JPEG（体积小）" },
            ],
        },
        new SettingDescriptor
        {
            Key = KeyJpegQuality,
            Label = "JPEG 质量",
            Kind = SettingKind.Number,
            Group = GroupSave,
            Description = "仅 JPEG 格式生效。",
            Value = JsonSerializer.SerializeToElement(settings.JpegQuality),
            DefaultValue = JsonSerializer.SerializeToElement(92),
            Min = 1,
            Max = 100,
        },
        new SettingDescriptor
        {
            Key = KeyCopyToClipboard,
            Label = "截图后复制到剪贴板",
            Kind = SettingKind.Bool,
            Group = GroupSave,
            Value = JsonSerializer.SerializeToElement(settings.CopyToClipboard),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },

        new SettingDescriptor
        {
            Key = KeyShowToolbarAfterRegion,
            Label = "区域截图后显示工具条",
            Kind = SettingKind.Bool,
            Group = GroupCapture,
            Description = "关闭则截完直接入库，不再弹出工具条。",
            Value = JsonSerializer.SerializeToElement(settings.ShowToolbarAfterRegion),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },

        new SettingDescriptor
        {
            Key = KeyPinTopmost,
            Label = "贴图默认置顶",
            Kind = SettingKind.Bool,
            Group = GroupPin,
            Value = JsonSerializer.SerializeToElement(settings.PinTopmost),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },

        new SettingDescriptor
        {
            Key = KeyScrollMinMatchRows,
            Label = "最小匹配行数",
            Kind = SettingKind.Number,
            Group = GroupScroll,
            Description = "两帧之间至少要有多少行内容一致才认为滚动成功。太小会把动态内容误判为滚动，太大会导致长页面匹配失败。",
            Value = JsonSerializer.SerializeToElement(settings.ScrollMinMatchRows),
            DefaultValue = JsonSerializer.SerializeToElement(24),
            Min = 4,
            Max = 400,
        },
        new SettingDescriptor
        {
            Key = KeyScrollMaxHeight,
            Label = "长图高度上限",
            Kind = SettingKind.Number,
            Group = GroupScroll,
            Description = "达到该像素高度或默认 256 MiB 内存预算后停止；结果会标明未确认完整。",
            Value = JsonSerializer.SerializeToElement(settings.ScrollMaxHeight),
            DefaultValue = JsonSerializer.SerializeToElement(20000),
            Min = 1000,
            Max = 200000,
        },
        new SettingDescriptor
        {
            Key = KeyScrollWheelNotches,
            Label = "自适应滚动格数上限",
            Description = "从一格起步，根据实际位移调整；安全上限为六格，旧配置超过六格时按六格执行。",
            Kind = SettingKind.Number,
            Group = GroupScroll,
            Value = JsonSerializer.SerializeToElement(settings.ScrollWheelNotches),
            DefaultValue = JsonSerializer.SerializeToElement(3),
            Min = 1,
            Max = 30,
        },

        new SettingDescriptor
        {
            Key = KeyOcrLanguage,
            Label = "识别语言",
            Kind = SettingKind.Text,
            Group = GroupOcr,
            Description = "BCP-47 语言标记，如 zh-Hans-CN；留空则用系统首选语言。",
            Value = JsonSerializer.SerializeToElement(settings.OcrLanguage),
            DefaultValue = JsonSerializer.SerializeToElement(""),
            Placeholder = "zh-Hans-CN",
        },
        new SettingDescriptor
        {
            Key = KeyOcrStripCjkSpaces,
            Label = "去掉中文之间的空格",
            Kind = SettingKind.Bool,
            Group = GroupOcr,
            Description = "识别结果里中文词之间常有空格，开启后自动去掉。",
            Value = JsonSerializer.SerializeToElement(settings.OcrStripCjkSpaces),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },
        new SettingDescriptor
        {
            Key = KeyOcrEnhanceImage,
            Label = "小字图像增强",
            Kind = SettingKind.Bool,
            Group = GroupOcr,
            Description = "文字偏小时先放大再识别，提高小字识别率；关闭则直接用原图识别。",
            Value = JsonSerializer.SerializeToElement(settings.OcrEnhanceImage),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },
    ];

    /// <summary>
    /// 把一项改动应用到设置对象。返回是否真的改动了（用于决定要不要保存）。
    /// 键未知、类型不符、越界时返回 false 且不修改任何字段。
    /// </summary>
    public static bool Apply(ScreenshotSettings settings, string key, JsonElement value)
    {
        switch (key)
        {
            case KeySaveDirectory:
                return SetString(() => settings.SaveDirectory, v => settings.SaveDirectory = v, value);

            case KeyFileNameTemplate:
                // 模板为空会让文件名退化成只有扩展名，保留原值更安全
                if (!TryGetString(value, out var template)) return false;
                if (string.IsNullOrWhiteSpace(template)) return false;
                if (string.Equals(settings.FileNameTemplate, template, StringComparison.Ordinal)) return false;
                settings.FileNameTemplate = template;
                return true;

            case KeyFormat:
                if (!TryGetString(value, out var formatText)) return false;
                if (!Enum.TryParse<ShotImageFormat>(formatText, ignoreCase: true, out var format)) return false;
                if (settings.Format == format) return false;
                settings.Format = format;
                return true;

            case KeyJpegQuality:
                return SetInt(() => settings.JpegQuality, v => settings.JpegQuality = v, value, 1, 100);

            case KeyCopyToClipboard:
                return SetBool(() => settings.CopyToClipboard, v => settings.CopyToClipboard = v, value);

            case KeyPinTopmost:
                return SetBool(() => settings.PinTopmost, v => settings.PinTopmost = v, value);

            case KeyShowToolbarAfterRegion:
                return SetBool(() => settings.ShowToolbarAfterRegion, v => settings.ShowToolbarAfterRegion = v, value);

            case KeyScrollMinMatchRows:
                return SetInt(() => settings.ScrollMinMatchRows, v => settings.ScrollMinMatchRows = v, value, 4, 400);

            case KeyScrollMaxHeight:
                return SetInt(() => settings.ScrollMaxHeight, v => settings.ScrollMaxHeight = v, value, 1000, 200000);

            case KeyScrollWheelNotches:
                return SetInt(() => settings.ScrollWheelNotches, v => settings.ScrollWheelNotches = v, value, 1, 30);

            case KeyOcrLanguage:
                return SetString(() => settings.OcrLanguage, v => settings.OcrLanguage = v, value);

            case KeyOcrStripCjkSpaces:
                return SetBool(() => settings.OcrStripCjkSpaces, v => settings.OcrStripCjkSpaces = v, value);

            case KeyOcrEnhanceImage:
                return SetBool(() => settings.OcrEnhanceImage, v => settings.OcrEnhanceImage = v, value);

            default:
                return false;
        }
    }

    /// <summary>批量应用改动，返回是否有任何一项生效。</summary>
    public static bool ApplyAll(ScreenshotSettings settings, IEnumerable<SettingChange> changes)
    {
        var changed = false;
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Key)) continue;
            if (Apply(settings, change.Key, change.Value)) changed = true;
        }
        return changed;
    }

    // ---- 取值助手 ----
    //
    // 都带 get 委托：既用于比较"值是否真的变了"，也保证非法输入时返回 false
    // 且完全不触碰设置对象。

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
                // 界面上手输的数字会以字符串形式传来
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