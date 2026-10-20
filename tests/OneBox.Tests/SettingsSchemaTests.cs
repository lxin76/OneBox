using System.Text.Json;
using OneBox.Plugins.TextPin.Core;

namespace OneBox.Tests;

/// <summary>
/// 设置中心的声明与应用。宿主传来的 JSON 可能是任何东西（手输、旧版本残留），
/// 因此这里重点钉住"非法值不改变设置对象"这条底线。
/// </summary>
public class SettingsSchemaTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static Settings Apply(Settings settings, string key, string raw)
    {
        SettingsSchema.Apply(settings, key, Json(raw));
        return settings;
    }

    [Fact]
    public void Describe_CoversEveryDeclaredKey()
    {
        var keys = SettingsSchema.Describe(new Settings()).Select(d => d.Key).ToList();

        Assert.Contains(SettingsSchema.KeyPinned, keys);
        Assert.Contains(SettingsSchema.KeyNoActivate, keys);
        Assert.Contains(SettingsSchema.KeyFontSize, keys);
        Assert.Contains(SettingsSchema.KeyWordWrap, keys);
        Assert.Contains(SettingsSchema.KeyMaxTextLength, keys);

        // 键不能重复，否则设置中心会出现两个同键的控件
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Apply_BoolKeys_TakeEffect()
    {
        var settings = new Settings { Pinned = true, NoActivate = true, WordWrap = true };

        Assert.True(SettingsSchema.Apply(settings, SettingsSchema.KeyPinned, Json("false")));
        Assert.False(settings.Pinned);

        Assert.True(SettingsSchema.Apply(settings, SettingsSchema.KeyNoActivate, Json("false")));
        Assert.False(settings.NoActivate);

        Assert.True(SettingsSchema.Apply(settings, SettingsSchema.KeyWordWrap, Json("false")));
        Assert.False(settings.WordWrap);
    }

    [Fact]
    public void Apply_SameValue_ReportsNoChange()
    {
        var settings = new Settings { Pinned = true };
        Assert.False(SettingsSchema.Apply(settings, SettingsSchema.KeyPinned, Json("true")));
    }

    [Fact]
    public void Apply_FontSize_ClampsToRange()
    {
        Assert.Equal(32.0, Apply(new Settings(), SettingsSchema.KeyFontSize, "999").FontSize);
        Assert.Equal(9.0, Apply(new Settings(), SettingsSchema.KeyFontSize, "1").FontSize);
    }

    [Fact]
    public void Apply_FontSize_AcceptsStringNumbers()
    {
        // 界面上手输的数字会以字符串形式传来
        Assert.Equal(16.0, Apply(new Settings(), SettingsSchema.KeyFontSize, "\"16\"").FontSize);
    }

    [Fact]
    public void Apply_MaxTextLength_ClampsToRange()
    {
        Assert.Equal(5_000_000, Apply(new Settings(), SettingsSchema.KeyMaxTextLength, "99999999").MaxTextLength);
        Assert.Equal(1000, Apply(new Settings(), SettingsSchema.KeyMaxTextLength, "1").MaxTextLength);
    }

    [Theory]
    [InlineData(SettingsSchema.KeyPinned, "\"yes\"")]        // 字符串不是布尔
    [InlineData(SettingsSchema.KeyPinned, "1")]              // 数字不是布尔
    [InlineData(SettingsSchema.KeyFontSize, "\"abc\"")]      // 非数字
    [InlineData(SettingsSchema.KeyFontSize, "null")]
    [InlineData(SettingsSchema.KeyMaxTextLength, "{}")]
    public void Apply_InvalidValue_LeavesSettingsUntouched(string key, string raw)
    {
        var settings = new Settings();
        var before = JsonSerializer.Serialize(settings);

        Assert.False(SettingsSchema.Apply(settings, key, Json(raw)));
        Assert.Equal(before, JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void Apply_UnknownKey_IsIgnored()
    {
        var settings = new Settings();
        Assert.False(SettingsSchema.Apply(settings, "notAKey", Json("123")));
    }

    [Fact]
    public void ApplyAll_AppliesKnownAndSkipsUnknown()
    {
        var settings = new Settings { Pinned = true, WordWrap = true };
        var changes = new List<OneBox.Abstractions.SettingChange>
        {
            new() { Key = SettingsSchema.KeyPinned, Value = Json("false") },
            new() { Key = "bogus", Value = Json("1") },
            new() { Key = SettingsSchema.KeyWordWrap, Value = Json("false") },
        };

        Assert.True(SettingsSchema.ApplyAll(settings, changes));
        Assert.False(settings.Pinned);
        Assert.False(settings.WordWrap);
    }

    [Fact]
    public void ApplyAll_NoEffectiveChange_ReturnsFalse()
    {
        var settings = new Settings { Pinned = true };
        var changes = new List<OneBox.Abstractions.SettingChange>
        {
            new() { Key = SettingsSchema.KeyPinned, Value = Json("true") },
            new() { Key = "bogus", Value = Json("1") },
        };

        Assert.False(SettingsSchema.ApplyAll(settings, changes));
    }
}
