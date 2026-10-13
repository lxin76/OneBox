using OneBox.Abstractions;

namespace OneBox.Tests;

/// <summary>热键组合串解析：宿主与插件共用同一套规则。</summary>
public class HotkeyParserTests
{
    [Fact]
    public void Parse_CtrlShiftV()
    {
        var parsed = HotkeyParser.Parse("Ctrl+Shift+V");
        Assert.NotNull(parsed);
        Assert.Equal(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_SHIFT, parsed!.Value.Modifiers);
        Assert.Equal('V', (char)parsed.Value.Vk);
    }

    [Fact]
    public void Parse_CtrlAltV()
    {
        var parsed = HotkeyParser.Parse("Ctrl+Alt+V");
        Assert.NotNull(parsed);
        Assert.Equal(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_ALT, parsed!.Value.Modifiers);
        Assert.Equal('V', (char)parsed.Value.Vk);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var parsed = HotkeyParser.Parse("ctrl+shift+k");
        Assert.NotNull(parsed);
        Assert.Equal('K', (char)parsed!.Value.Vk);
    }

    [Fact]
    public void Parse_WinModifier()
    {
        var parsed = HotkeyParser.Parse("Win+Alt+T");
        Assert.NotNull(parsed);
        Assert.Equal(HotkeyParser.MOD_WIN | HotkeyParser.MOD_ALT, parsed!.Value.Modifiers);
    }

    [Fact]
    public void Parse_FunctionKeys()
    {
        var parsed = HotkeyParser.Parse("Ctrl+Alt+F8");
        Assert.NotNull(parsed);
        Assert.Equal((uint)0x77, parsed!.Value.Vk); // VK_F8
    }

    [Theory]
    [InlineData("V")]          // 无修饰键
    [InlineData("Ctrl+")]      // 缺按键
    [InlineData("Ctrl+V+X")]   // 修饰键出现在末尾
    [InlineData("")]           // 空
    [InlineData("Ctrl+Shift+LongKey")] // 非 F 键的长名字
    public void Parse_Invalid_ReturnsNull(string combo)
    {
        Assert.Null(HotkeyParser.Parse(combo));
    }

    [Fact]
    public void Format_Roundtrip()
    {
        var parsed = HotkeyParser.Parse("Ctrl+Shift+V");
        Assert.Equal("Ctrl+Shift+V", HotkeyParser.Format(parsed!.Value.Modifiers, parsed.Value.Vk));
    }

    [Fact]
    public void Normalize_UnifiesCaseAndOrder()
    {
        Assert.Equal("Ctrl+Alt+V", HotkeyParser.Normalize("alt+ctrl+v"));
    }

    [Fact]
    public void Normalize_Invalid_ReturnsNull()
    {
        Assert.Null(HotkeyParser.Normalize("V"));
    }

    [Fact]
    public void Normalize_MakesDifferentSpellingsEqual()
    {
        // 冲突检测依赖规范化：写法不同但语义相同的组合必须判定为同一个
        Assert.Equal(HotkeyParser.Normalize("ctrl+alt+v"), HotkeyParser.Normalize("Alt+Ctrl+V"));
    }
}
