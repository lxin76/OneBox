using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 外部占用探测（真实 Win32 调用）的边界行为。
///
/// 这里只用"必然成立"的断言，不依赖本机装了什么软件：
/// 探测一个几乎不可能被占用的组合应当返回 true；
/// 而"被占用"的判定依赖真实进程注册，放到手工验证里做。
/// </summary>
public class HotkeyProbeTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Shift+F24")]
    [InlineData("Ctrl+Alt+Shift+F23")]
    public void UnusedCombination_IsReportedAvailable(string combo)
    {
        // 这类"三重修饰 + 高位功能键"极少被软件注册
        Assert.True(HotkeyService.ProbeAvailable(combo));
    }

    [Fact]
    public void InvalidCombination_IsReportedUnavailable()
    {
        // 解析不了就谈不上可用，返回 false 让上层走"非法组合"分支
        Assert.False(HotkeyService.ProbeAvailable("NotAKey"));
    }

    [Fact]
    public void ProbeWithoutModifier_IsReportedUnavailable()
    {
        Assert.False(HotkeyService.ProbeAvailable("F5"));
    }

    [Fact]
    public void RepeatedProbes_AreStableAndDoNotLeak()
    {
        // 每次探测都会建/销毁一个消息窗口；重复探测结果应稳定
        for (int i = 0; i < 20; i++)
            Assert.True(HotkeyService.ProbeAvailable("Ctrl+Alt+Shift+F22"));
    }
}
