using ClipboardFloat.Core;

namespace ClipboardFloat.Tests;

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
}
