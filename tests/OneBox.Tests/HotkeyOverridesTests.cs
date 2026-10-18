using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>用户改键覆盖层：存取、规范化与坏数据容错。</summary>
public class HotkeyOverridesTests
{
    [Fact]
    public void Get_ReturnsNullWhenNotCustomized()
    {
        var overrides = new HotkeyOverrides();

        Assert.Null(overrides.Get("textpin", "toggle"));
        Assert.Equal(0, overrides.Count);
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "Ctrl+Alt+Q");

        Assert.Equal("Ctrl+Alt+Q", overrides.Get("textpin", "toggle"));
        Assert.Equal(1, overrides.Count);
    }

    [Fact]
    public void Set_NormalizesSpelling()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "alt+ctrl+q");

        Assert.Equal("Ctrl+Alt+Q", overrides.Get("textpin", "toggle"));
    }

    [Fact]
    public void Set_IgnoresInvalidCombination()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "NotAKey");

        Assert.Null(overrides.Get("textpin", "toggle"));
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("TextPin", "Toggle", "Ctrl+Alt+Q");

        Assert.Equal("Ctrl+Alt+Q", overrides.Get("textpin", "toggle"));
    }

    [Fact]
    public void Remove_DeletesOnlyThatAction()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "Ctrl+Alt+Q");
        overrides.Set("textpin", "paste", "Ctrl+Alt+P");

        Assert.True(overrides.Remove("textpin", "toggle"));
        Assert.Null(overrides.Get("textpin", "toggle"));
        Assert.NotNull(overrides.Get("textpin", "paste"));
    }

    [Fact]
    public void Remove_ReturnsFalseWhenAbsent()
    {
        Assert.False(new HotkeyOverrides().Remove("textpin", "toggle"));
    }

    [Fact]
    public void RemovePlugin_ClearsAllItsOverrides()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "Ctrl+Alt+Q");
        overrides.Set("textpin", "paste", "Ctrl+Alt+P");
        overrides.Set("screenshot", "region", "Ctrl+Alt+R");

        var removed = overrides.RemovePlugin("textpin");

        Assert.Equal(2, removed);
        Assert.Null(overrides.Get("textpin", "toggle"));
        Assert.NotNull(overrides.Get("screenshot", "region"));
    }

    [Fact]
    public void RemovePlugin_DoesNotTouchSimilarPrefix()
    {
        // "text" 是 "textpin" 的前缀，按插件删除时不能误伤
        var overrides = new HotkeyOverrides();
        overrides.Set("text", "a", "Ctrl+Alt+1");
        overrides.Set("textpin", "a", "Ctrl+Alt+2");

        overrides.RemovePlugin("text");

        Assert.Null(overrides.Get("text", "a"));
        Assert.NotNull(overrides.Get("textpin", "a"));
    }

    [Fact]
    public void FromPersisted_DropsInvalidEntries()
    {
        var persisted = new Dictionary<string, string>
        {
            ["textpin|toggle"] = "Ctrl+Alt+Q",
            ["textpin|paste"] = "垃圾数据",
            ["textpin|copy"] = "",
        };

        var overrides = HotkeyOverrides.FromPersisted(persisted);

        Assert.NotNull(overrides.Get("textpin", "toggle"));
        Assert.Null(overrides.Get("textpin", "paste"));
        Assert.Null(overrides.Get("textpin", "copy"));
        Assert.Equal(1, overrides.Count);
    }

    [Fact]
    public void FromPersisted_NormalizesAndHandlesNull()
    {
        var overrides = HotkeyOverrides.FromPersisted(new Dictionary<string, string>
        {
            ["textpin|toggle"] = "alt+ctrl+q",
        });

        Assert.Equal("Ctrl+Alt+Q", overrides.Get("textpin", "toggle"));
        Assert.Equal(0, HotkeyOverrides.FromPersisted(null).Count);
    }

    [Fact]
    public void ToDictionary_IsASnapshot()
    {
        var overrides = new HotkeyOverrides();
        overrides.Set("textpin", "toggle", "Ctrl+Alt+Q");

        var snapshot = overrides.ToDictionary();
        overrides.Remove("textpin", "toggle");

        Assert.Equal("Ctrl+Alt+Q", snapshot[HotkeyOverrides.KeyOf("textpin", "toggle")]);
        Assert.Equal(0, overrides.Count);
    }
}
