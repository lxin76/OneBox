using System.Drawing;
using OneBox.Plugins.Recorder.Core;

namespace OneBox.Tests;

/// <summary>
/// 多显示器坐标换算。录屏库的裁剪矩形是相对单块显示器的，
/// 这里把「虚拟屏幕坐标 → 目标屏 + 局部坐标」的边界情况全部钉住。
/// </summary>
public class MonitorLayoutTests
{
    private static MonitorBounds Monitor(int index, int left, int top, int width, int height, bool primary = false)
        => new(index, $"\\\\.\\DISPLAY{index + 1}", new Rectangle(left, top, width, height),
            new Rectangle(left, top, width, height - 40), primary);

    private static readonly MonitorBounds LeftScreen = Monitor(0, 0, 0, 1920, 1080, primary: true);
    private static readonly MonitorBounds RightScreen = Monitor(1, 1920, 0, 2560, 1440);
    private static readonly MonitorBounds TopScreen = Monitor(2, -1280, -720, 1280, 720);

    private static IReadOnlyList<MonitorBounds> Monitors => [LeftScreen, RightScreen, TopScreen];

    [Fact]
    public void 命中坐标为负的显示器()
    {
        var found = MonitorLayout.MonitorAt(Monitors, -100, -100);

        Assert.NotNull(found);
        Assert.Equal(TopScreen.Index, found!.Value.Index);
    }

    [Fact]
    public void 光标落在屏幕外时取最近的一块()
    {
        var found = MonitorLayout.MonitorAt(Monitors, 5000, 100);

        Assert.NotNull(found);
        Assert.Equal(RightScreen.Index, found!.Value.Index);
    }

    [Fact]
    public void 主屏按标记返回()
    {
        Assert.Equal(LeftScreen.Index, MonitorLayout.Primary(Monitors)!.Value.Index);
    }

    [Fact]
    public void 跨屏选区取重叠面积最大的屏幕()
    {
        var region = new Rectangle(1600, 100, 1000, 400); // 320 像素在左屏，680 像素在右屏

        var dominant = MonitorLayout.DominantForRegion(Monitors, region);

        Assert.NotNull(dominant);
        Assert.Equal(RightScreen.Index, dominant!.Value.Index);
    }

    [Fact]
    public void 选区裁剪到屏幕并转换为局部坐标()
    {
        var region = new Rectangle(1700, 100, 500, 300);

        Assert.True(MonitorLayout.TryLocalizeRegion(Monitors, region, out var monitor, out var local));

        Assert.Equal(RightScreen.Index, monitor.Index);
        Assert.Equal(new Rectangle(0, 100, 280, 300), local);
    }

    [Fact]
    public void 完全落在屏幕外的选区被拒绝()
    {
        var region = new Rectangle(9000, 9000, 200, 200);

        Assert.False(MonitorLayout.TryLocalizeRegion(Monitors, region, out _, out _));
    }

    [Fact]
    public void 过小的选区被拒绝()
    {
        var region = new Rectangle(100, 100, 8, 8);

        Assert.False(MonitorLayout.TryLocalizeRegion(Monitors, region, out _, out _));
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 1280, 720)]
    [InlineData(2560, 1440, 1280, 1280, 720)]
    [InlineData(800, 600, 1280, 800, 600)]
    [InlineData(641, 361, 1280, 640, 360)]
    [InlineData(1, 1, 1280, 2, 2)]
    public void 输出尺寸等比缩放且保证偶数(int sourceWidth, int sourceHeight, int maxWidth, int expectedWidth, int expectedHeight)
    {
        var (width, height) = MonitorLayout.FitOutputSize(sourceWidth, sourceHeight, maxWidth);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    [Fact]
    public void 输出尺寸不会放大源画面()
    {
        var (width, height) = MonitorLayout.FitOutputSize(640, 360, 1280);

        Assert.Equal(640, width);
        Assert.Equal(360, height);
    }
}
