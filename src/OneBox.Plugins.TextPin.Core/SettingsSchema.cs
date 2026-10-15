using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Plugins.TextPin.Core;

/// <summary>
/// 置顶文本插件在宿主「设置中心」里暴露的设置项声明，以及把改动应用到
/// <see cref="Settings"/> 的逻辑。
///
/// 为什么放在 Core：声明与应用都是纯数据变换，不碰 WPF，可以脱离界面单测。
/// 值域约定：宿主传来的 JSON 可能是任何东西（手输、旧版本残留），
/// <see cref="Apply"/> 对每个键都做类型与范围校验，非法值一律忽略并保留原值。
///
/// 热键不在此列 —— 热键由插件自己的设置窗处理（需要展示"被占用"等注册结果）。
/// </summary>
public static class SettingsSchema
{
    // 设置键。用常量而非字面量，避免声明与应用两处写错拼写。
    public const string KeyPinned = "pinned";
    public const string KeyNoActivate = "noActivate";
    public const string KeyAutoPaste = "autoPaste";
    public const string KeyFontSize = "fontSize";
    public const string KeyWordWrap = "wordWrap";
    public const string KeyMaxTextLength = "maxTextLength";

    private const string GroupWindow = "窗口";
    private const string GroupEdit = "编辑";

    /// <summary>生成设置项声明。</summary>
    public static IReadOnlyList<SettingDescriptor> Describe(Settings settings) =>
    [
        new SettingDescriptor
        {
            Key = KeyPinned,
            Label = "窗口置顶",
            Kind = SettingKind.Bool,
            Group = GroupWindow,
            Description = "始终保持在其他窗口上方。",
            Value = JsonSerializer.SerializeToElement(settings.Pinned),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },
        new SettingDescriptor
        {
            Key = KeyNoActivate,
            Label = "不抢焦点",
            Kind = SettingKind.Bool,
            Group = GroupWindow,
            Description = "点击窗口不打断当前正在编辑的软件；点进文本框时会临时取得焦点，离开后自动归还。",
            Value = JsonSerializer.SerializeToElement(settings.NoActivate),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },
        new SettingDescriptor
        {
            Key = KeyAutoPaste,
            Label = "复制后自动粘贴",
            Kind = SettingKind.Bool,
            Group = GroupEdit,
            Description = "复制文本到剪贴板后，自动模拟 Ctrl+V 粘贴到前台窗口。",
            Value = JsonSerializer.SerializeToElement(settings.AutoPaste),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },
        new SettingDescriptor
        {
            Key = KeyFontSize,
            Label = "字号",
            Kind = SettingKind.Number,
            Group = GroupEdit,
            Value = JsonSerializer.SerializeToElement(settings.FontSize),
            DefaultValue = JsonSerializer.SerializeToElement(13.0),
            Min = 9,
            Max = 32,
        },
        new SettingDescriptor
        {
            Key = KeyWordWrap,
            Label = "自动换行",
            Kind = SettingKind.Bool,
            Group = GroupEdit,
            Description = "关闭后长行不换行，改用横向滚动。",
            Value = JsonSerializer.SerializeToElement(settings.WordWrap),
            DefaultValue = JsonSerializer.SerializeToElement(true),
        },
        new SettingDescriptor
        {
            Key = KeyMaxTextLength,
            Label = "文本长度上限",
            Kind = SettingKind.Number,
            Group = GroupEdit,
            Description = "超出部分会被截断，避免异常大的粘贴内容拖垮界面与磁盘写入。",
            Value = JsonSerializer.SerializeToElement(settings.MaxTextLength),
            DefaultValue = JsonSerializer.SerializeToElement(200_000),
            Min = 1000,
            Max = 5_000_000,
        },
    ];

    /// <summary>
    /// 把一项改动应用到设置对象。返回是否真的改动了（用于决定要不要保存）。
    /// 键未知、类型不符、越界时返回 false 且不修改任何字段。
    /// </summary>
    public static bool Apply(Settings settings, string key, JsonElement value)
    {
        switch (key)
        {
            case KeyPinned:
                return SetBool(() => settings.Pinned, v => settings.Pinned = v, value);

            case KeyNoActivate:
                return SetBool(() => settings.NoActivate, v => settings.NoActivate = v, value);

            case KeyAutoPaste:
                return SetBool(() => settings.AutoPaste, v => settings.AutoPaste = v, value);

            case KeyWordWrap:
                return SetBool(() => settings.WordWrap, v => settings.WordWrap = v, value);

            case KeyFontSize:
                return SetDouble(() => settings.FontSize, v => settings.FontSize = v, value, 9, 32);

            case KeyMaxTextLength:
                return SetInt(() => settings.MaxTextLength, v => settings.MaxTextLength = v, value, 1000, 5_000_000);

            default:
                return false;
        }
    }

    /// <summary>批量应用改动，返回是否有任何一项生效。</summary>
    public static bool ApplyAll(Settings settings, IEnumerable<SettingChange> changes)
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
        if (!TryGetNumber(value, out var number) || double.IsNaN(number)) return false;

        var clamped = (int)Math.Round(Math.Clamp(number, min, max));
        if (get() == clamped) return false;
        apply(clamped);
        return true;
    }

    private static bool SetDouble(Func<double> get, Action<double> apply, JsonElement value, double min, double max)
    {
        if (!TryGetNumber(value, out var number) || double.IsNaN(number)) return false;

        var clamped = Math.Round(Math.Clamp(number, min, max), 1);
        if (Math.Abs(get() - clamped) < 0.01) return false;
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
}
