using System.Text.Json;
using OneBox.Abstractions;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>设置中心表单分组与取值编解码的纯逻辑测试。</summary>
public class SettingsFormBuilderTests
{
    private static SettingDescriptor Descriptor(string key, string? group = null, string label = "")
        => new() { Key = key, Group = group, Label = label };

    [Fact]
    public void 未声明分组的项归入常规()
    {
        var sections = SettingsFormBuilder.Group([Descriptor("a"), Descriptor("b")]);

        Assert.Single(sections);
        Assert.Equal(SettingsFormBuilder.DefaultGroupTitle, sections[0].Title);
        Assert.Equal(2, sections[0].Items.Count);
    }

    [Fact]
    public void 相同分组归到一起()
    {
        var sections = SettingsFormBuilder.Group(
        [
            Descriptor("a", "保存"),
            Descriptor("b", "识别"),
            Descriptor("c", "保存"),
        ]);

        Assert.Equal(2, sections.Count);
        Assert.Equal("保存", sections[0].Title);
        Assert.Equal(["a", "c"], sections[0].Items.Select(i => i.Key));
        Assert.Equal("识别", sections[1].Title);
    }

    [Fact]
    public void 分组顺序按首次出现排列()
    {
        // 尊重插件声明的顺序，避免设置页每次重建时分组乱跳
        var sections = SettingsFormBuilder.Group(
        [
            Descriptor("a", "乙"),
            Descriptor("b", "甲"),
            Descriptor("c", "乙"),
        ]);

        Assert.Equal(["乙", "甲"], sections.Select(s => s.Title));
    }

    [Fact]
    public void 组内保持声明顺序()
    {
        var sections = SettingsFormBuilder.Group(
        [
            Descriptor("z", "g"),
            Descriptor("a", "g"),
            Descriptor("m", "g"),
        ]);

        Assert.Equal(["z", "a", "m"], sections[0].Items.Select(i => i.Key));
    }

    [Fact]
    public void 空白分组名归入常规()
    {
        var sections = SettingsFormBuilder.Group([Descriptor("a", "   "), Descriptor("b", "")]);

        Assert.Single(sections);
        Assert.Equal(SettingsFormBuilder.DefaultGroupTitle, sections[0].Title);
    }

    [Fact]
    public void 分组名首尾空格被去掉()
    {
        var sections = SettingsFormBuilder.Group([Descriptor("a", "  保存  ")]);

        Assert.Equal("保存", sections[0].Title);
    }

    [Fact]
    public void 无键的项被忽略()
    {
        var sections = SettingsFormBuilder.Group([Descriptor(""), Descriptor("  "), Descriptor("a")]);

        Assert.Single(sections);
        Assert.Single(sections[0].Items);
    }

    [Fact]
    public void 空输入返回空()
    {
        Assert.Empty(SettingsFormBuilder.Group([]));
    }
}

/// <summary>设置值 JSON 编解码的边界测试。</summary>
public class SettingValueCodecTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void 读布尔_真值()
    {
        Assert.True(SettingValueCodec.GetBool(Json("true")));
        Assert.True(SettingValueCodec.GetBool(Json("\"true\"")));
    }

    [Fact]
    public void 读布尔_假值()
    {
        Assert.False(SettingValueCodec.GetBool(Json("false")));
        Assert.False(SettingValueCodec.GetBool(Json("\"false\"")));
    }

    [Fact]
    public void 读布尔_缺失时用回退值()
    {
        Assert.True(SettingValueCodec.GetBool(null, fallback: true));
        Assert.False(SettingValueCodec.GetBool(null, fallback: false));
    }

    [Fact]
    public void 读布尔_类型不符时用回退值()
    {
        Assert.True(SettingValueCodec.GetBool(Json("\"不是布尔\""), fallback: true));
        Assert.False(SettingValueCodec.GetBool(Json("{}"), fallback: false));
    }

    [Fact]
    public void 读文本_字符串()
    {
        Assert.Equal("你好", SettingValueCodec.GetString(Json("\"你好\"")));
    }

    [Fact]
    public void 读文本_数字转成文本()
    {
        Assert.Equal("92", SettingValueCodec.GetString(Json("92")));
    }

    [Fact]
    public void 读文本_缺失时用回退值()
    {
        Assert.Equal("默认", SettingValueCodec.GetString(null, "默认"));
    }

    [Fact]
    public void 读文本_null值用回退值()
    {
        Assert.Equal("默认", SettingValueCodec.GetString(Json("null"), "默认"));
    }

    [Fact]
    public void 读数字_数字与数字字符串()
    {
        Assert.Equal(92, SettingValueCodec.GetNumber(Json("92")));
        Assert.Equal(92, SettingValueCodec.GetNumber(Json("\"92\"")));
    }

    [Fact]
    public void 读数字_类型不符时用回退值()
    {
        Assert.Equal(7, SettingValueCodec.GetNumber(Json("\"abc\""), 7));
        Assert.Equal(7, SettingValueCodec.GetNumber(null, 7));
    }

    [Fact]
    public void 读数字并按上限夹取()
    {
        var d = new SettingDescriptor { Key = "q", Max = 100, Value = Json("500") };

        Assert.Equal(100, SettingValueCodec.GetNumberClamped(d));
    }

    [Fact]
    public void 读数字并按下限夹取()
    {
        var d = new SettingDescriptor { Key = "q", Min = 1, Value = Json("0") };

        Assert.Equal(1, SettingValueCodec.GetNumberClamped(d));
    }

    [Fact]
    public void 值缺失时回退到默认值再夹取()
    {
        var d = new SettingDescriptor { Key = "q", Min = 1, Max = 100, DefaultValue = Json("500") };

        Assert.Equal(100, SettingValueCodec.GetNumberClamped(d));
    }

    [Fact]
    public void 值缺失且无默认值时用回退参数()
    {
        var d = new SettingDescriptor { Key = "q" };

        Assert.Equal(5, SettingValueCodec.GetNumberClamped(d, fallback: 5));
    }

    [Fact]
    public void 生效值优先取当前值()
    {
        var d = new SettingDescriptor { Key = "q", Value = Json("1"), DefaultValue = Json("2") };

        Assert.Equal(1, SettingValueCodec.GetNumber(SettingValueCodec.EffectiveValue(d)));
    }

    [Fact]
    public void 生效值在当前值缺失时取默认值()
    {
        var d = new SettingDescriptor { Key = "q", DefaultValue = Json("2") };

        Assert.Equal(2, SettingValueCodec.GetNumber(SettingValueCodec.EffectiveValue(d)));
    }

    [Fact]
    public void 编码函数产出可回读的值()
    {
        Assert.True(SettingValueCodec.GetBool(SettingValueCodec.FromBool(true)));
        Assert.Equal("x", SettingValueCodec.GetString(SettingValueCodec.FromString("x")));
        Assert.Equal(3, SettingValueCodec.GetNumber(SettingValueCodec.FromNumber(3)));
    }
}
