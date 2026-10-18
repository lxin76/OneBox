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

    // ---- 自定义改键后新增的支持范围 ----

    [Theory]
    [InlineData("Ctrl+Alt+5", 0x35)]
    [InlineData("Ctrl+Home", 0x24)]
    [InlineData("Ctrl+Alt+Left", 0x25)]
    [InlineData("Shift+Tab", 0x09)]
    [InlineData("Ctrl+OEM_COMMA", 0xBC)]
    [InlineData("Alt+Numpad5", 0x65)]
    public void Parse_SpecialAndDigitKeys(string combo, int expectedVk)
    {
        var parsed = HotkeyParser.Parse(combo);
        Assert.NotNull(parsed);
        Assert.Equal((uint)expectedVk, parsed!.Value.Vk);
    }

    [Theory]
    [InlineData("Ctrl+HOME")]         // 特殊键名统一大写
    [InlineData("Alt+SPACE")]
    [InlineData("Ctrl+Alt+5")]
    [InlineData("Shift+Win+X")]       // 修饰键顺序固定为 Ctrl/Shift/Alt/Win
    [InlineData("Ctrl+OEM_COMMA")]
    public void Format_Roundtrip_ForSpecialKeys(string combo)
    {
        // 录制器产出、解析器读回必须一致，否则"录进去的键"保存后读出来会变样
        Assert.Equal(combo, HotkeyParser.Normalize(combo));
    }

    [Fact]
    public void Normalize_ReordersModifiersToFixedOrder()
    {
        Assert.Equal("Ctrl+Shift+Alt+Win+X", HotkeyParser.Normalize("win+alt+shift+ctrl+x"));
    }

    [Fact]
    public void Parse_RejectsTwoMainKeys()
    {
        Assert.Null(HotkeyParser.Parse("Ctrl+V+X"));
    }

    [Fact]
    public void Parse_RejectsUnknownKeyName()
    {
        Assert.Null(HotkeyParser.Parse("Ctrl+NotAKey"));
    }

    [Fact]
    public void Normalize_RejectsPlainKeyWithoutModifier()
    {
        // 纯按键既注册不上，也会把正常打字抢走
        Assert.Null(HotkeyParser.Normalize("F5"));
        Assert.Null(HotkeyParser.Normalize("Space"));
    }

    [Theory]
    [InlineData("ctrl+alt+x", "Alt+Ctrl+X", true)]
    [InlineData("Ctrl+Alt+X", "Ctrl+Alt+Y", false)]
    [InlineData("Ctrl+Alt+X", "Ctrl+X", false)]
    [InlineData("bad", "Ctrl+X", false)]
    public void SameCombination_ComparesNormalizedForms(string a, string b, bool expected)
    {
        Assert.Equal(expected, HotkeyParser.SameCombination(a, b));
    }

    [Fact]
    public void HasModifier_DetectsRealModifiers()
    {
        Assert.True(HotkeyParser.HasModifier(HotkeyParser.MOD_CONTROL));
        Assert.True(HotkeyParser.HasModifier(HotkeyParser.MOD_WIN | HotkeyParser.MOD_NOREPEAT));
        Assert.False(HotkeyParser.HasModifier(0));
        Assert.False(HotkeyParser.HasModifier(HotkeyParser.MOD_NOREPEAT));
    }
}
