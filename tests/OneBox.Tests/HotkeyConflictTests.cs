using OneBox.Abstractions;
using OneBox.Plugins.Clipboard.Services;

namespace OneBox.Tests;

/// <summary>热键冲突判定：插件自身重复用本地检查，跨插件由宿主仲裁。</summary>
public class HotkeyConflictTests
{
    [Fact]
    public void NoConflict_ReturnsNull()
    {
        Assert.Null(HotkeyConflictChecker.FindSelfConflict("Ctrl+Alt+Q", new[] { "Ctrl+Alt+V", "Ctrl+Alt+P" }));
    }

    [Fact]
    public void EmptyOthers_ReturnsNull()
    {
        Assert.Null(HotkeyConflictChecker.FindSelfConflict("Ctrl+Alt+Q", Array.Empty<string>()));
    }

    [Fact]
    public void DuplicateWithAnother_ReturnsWarning()
    {
        var warning = HotkeyConflictChecker.FindSelfConflict("Ctrl+Alt+V", new[] { "Ctrl+Alt+V", "Ctrl+Alt+P" });
        Assert.NotNull(warning);
        Assert.Contains("重复", warning);
    }

    [Fact]
    public void DuplicateCheck_IsCaseInsensitive()
    {
        var warning = HotkeyConflictChecker.FindSelfConflict("ctrl+alt+p", new[] { "Ctrl+Alt+P" });
        Assert.NotNull(warning);
    }

    [Fact]
    public void BuiltinResetHotkey_ReturnsWarning()
    {
        var warning = HotkeyConflictChecker.FindSelfConflict("Ctrl+Alt+K", new[] { "Ctrl+Alt+V" });
        Assert.NotNull(warning);
        Assert.Contains("修复键盘输入", warning);
    }

    /// <summary>宿主对插件声明的热键做规范化后再判重，写法不同也应算冲突。</summary>
    [Fact]
    public void HostRebuild_DetectsEquivalentSpellings()
    {
        Assert.Equal(
            HotkeyParser.Normalize("Ctrl+Alt+V"),
            HotkeyParser.Normalize("alt+ctrl+v"));
    }
}
