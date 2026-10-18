using System.Text.Json;
using OneBox.Abstractions;
using OneBox.Host.Services;

namespace OneBox.Tests;

/// <summary>
/// 关闭窗口时的行为取值。
///
/// 这条设置牵着两件容易出事的东西：窗口关不掉（询问框反复弹），以及程序意外退出。
/// 所以"还没设置过 / 值写坏了"必须有确定的行为——一律按默认的"只关窗口"，
/// 而且它还得能随宿主设置一起存下来，否则用户改完下次启动就忘了。
/// </summary>
public class CloseWindowActionsTests
{
    [Fact]
    public void 还没设置过时按只关窗口处理()
    {
        Assert.Equal(CloseWindowActions.CloseWindowOnly, CloseWindowActions.Normalize(null));
        Assert.False(CloseWindowActions.ShouldExit(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("exit")]
    [InlineData("quit")]
    [InlineData("退出")]
    public void 值不认识时回退到只关窗口(string value)
    {
        Assert.Equal(CloseWindowActions.CloseWindowOnly, CloseWindowActions.Normalize(value));
        Assert.False(CloseWindowActions.ShouldExit(value));
    }

    [Theory]
    [InlineData("exitApp")]
    [InlineData("EXITAPP")]
    [InlineData("  exitApp  ")]
    public void 记住退出选择后关窗即退出(string value)
    {
        Assert.Equal(CloseWindowActions.ExitApp, CloseWindowActions.Normalize(value));
        Assert.True(CloseWindowActions.ShouldExit(value));
    }

    [Fact]
    public void 只关窗口的取值照原样识别()
    {
        Assert.Equal(CloseWindowActions.CloseWindowOnly, CloseWindowActions.Normalize(CloseWindowActions.CloseWindowOnly));
        Assert.False(CloseWindowActions.ShouldExit(CloseWindowActions.CloseWindowOnly));
    }

    [Fact]
    public void 新装的宿主没有关闭行为即表示还没问过()
        => Assert.Null(new HostSettings().CloseWindowAction);

    [Fact]
    public void 关闭行为随宿主设置一起持久化()
    {
        var settings = new HostSettings { CloseWindowAction = CloseWindowActions.ExitApp };

        var restored = JsonSerializer.Deserialize<HostSettings>(
            JsonSerializer.Serialize(settings, Protocol.Json), Protocol.Json)!;

        Assert.Equal(CloseWindowActions.ExitApp, restored.CloseWindowAction);
        Assert.True(CloseWindowActions.ShouldExit(restored.CloseWindowAction));
    }

    [Fact]
    public void 只关窗口的选择也能存下来并与没问过区分()
    {
        var settings = new HostSettings { CloseWindowAction = CloseWindowActions.CloseWindowOnly };

        var restored = JsonSerializer.Deserialize<HostSettings>(
            JsonSerializer.Serialize(settings, Protocol.Json), Protocol.Json)!;

        // 仍是"问过了"的状态：不能退化成 null，否则每次关窗口都会被再问一遍
        Assert.NotNull(restored.CloseWindowAction);
        Assert.Equal(CloseWindowActions.CloseWindowOnly, restored.CloseWindowAction);
    }
}
