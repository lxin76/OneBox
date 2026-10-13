using System.Text.Json;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>设置中心里的一个分组（同一 Group 的设置项归在一起）。</summary>
public sealed class SettingSection
{
    public required string Title { get; init; }
    public required IReadOnlyList<SettingDescriptor> Items { get; init; }
}

/// <summary>
/// 把插件声明的设置项整理成设置中心的分组结构。
/// 纯逻辑，不碰 WPF，便于单元测试。
/// </summary>
public static class SettingsFormBuilder
{
    /// <summary>未声明分组的设置项归入此分组。</summary>
    public const string DefaultGroupTitle = "常规";

    /// <summary>
    /// 按 <see cref="SettingDescriptor.Group"/> 分组。
    /// 分组按首次出现的顺序排列（尊重插件的声明顺序），组内保持声明顺序。
    /// </summary>
    public static IReadOnlyList<SettingSection> Group(IEnumerable<SettingDescriptor> descriptors)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<SettingDescriptor>>(StringComparer.Ordinal);

        foreach (var d in descriptors)
        {
            if (string.IsNullOrWhiteSpace(d.Key)) continue;

            var title = string.IsNullOrWhiteSpace(d.Group) ? DefaultGroupTitle : d.Group.Trim();
            if (!buckets.TryGetValue(title, out var list))
            {
                list = [];
                buckets[title] = list;
                order.Add(title);
            }
            list.Add(d);
        }

        return order
            .Select(title => new SettingSection { Title = title, Items = buckets[title] })
            .ToList();
    }
}

/// <summary>
/// 设置值的 JSON 读写。
///
/// 宿主与插件之间用 <see cref="JsonElement"/> 传值，读的时候必须对
/// "值缺失、类型不符、越界" 全部给出确定行为，否则设置界面会出现
/// 空控件或异常。这里把这些边界集中处理，并做单元测试。
/// </summary>
public static class SettingValueCodec
{
    /// <summary>读布尔；缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
    public static bool GetBool(JsonElement? value, bool fallback = false)
    {
        if (value is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            JsonValueKind.Number when v.TryGetDouble(out var d) => Math.Abs(d) > double.Epsilon,
            _ => fallback,
        };
    }

    /// <summary>读文本；缺失时返回 <paramref name="fallback"/>。数字也会转成文本。</summary>
    public static string GetString(JsonElement? value, string fallback = "")
    {
        if (value is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? fallback,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            _ => fallback,
        };
    }

    /// <summary>读数字；缺失或类型不符时返回 <paramref name="fallback"/>。</summary>
    public static double GetNumber(JsonElement? value, double fallback = 0)
    {
        if (value is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => fallback,
        };
    }

    /// <summary>
    /// 读数字并按声明里的 Min/Max 夹取。
    /// 界面上用户可能手输超范围的值，插件不该为此崩溃或行为异常。
    /// </summary>
    public static double GetNumberClamped(SettingDescriptor descriptor, double fallback = 0)
    {
        var value = GetNumber(descriptor.Value, GetNumber(descriptor.DefaultValue, fallback));
        if (descriptor.Min is { } min && value < min) value = min;
        if (descriptor.Max is { } max && value > max) value = max;
        return value;
    }

    /// <summary>取设置项的当前值：优先 <see cref="SettingDescriptor.Value"/>，缺失时回退默认值。</summary>
    public static JsonElement? EffectiveValue(SettingDescriptor descriptor)
        => descriptor.Value ?? descriptor.DefaultValue;

    public static JsonElement FromBool(bool value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement FromString(string value) => JsonSerializer.SerializeToElement(value ?? "");
    public static JsonElement FromNumber(double value) => JsonSerializer.SerializeToElement(value);
}
