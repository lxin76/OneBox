using System.Runtime.InteropServices;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Tests;

public sealed class ScrollInjectorSafetyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(-120)]
    [InlineData(int.MinValue)]
    public void 无目标绝不注入输入(int delta)
    {
        Assert.False(ScrollInjector.TrySendWheel(IntPtr.Zero, 0, 0, delta));
        Assert.False(ScrollInjector.IsTargetReady(IntPtr.Zero, 0, 0));
        Assert.False(ScrollInjector.SendPageDown(IntPtr.Zero));
        Assert.False(ScrollInjector.TryGetScrollPosition(IntPtr.Zero, out var position));
        Assert.Equal(0, position);
    }

    [Fact]
    public void 遮挡检查拒绝无效目标且不移动鼠标或前台()
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetCursorPos(out var before);
        Assert.False(ScrollOcclusionGuard.IsRegionUnobscured(IntPtr.Zero, new System.Drawing.Rectangle(10, 10, 100, 100)));
        Assert.False(ScrollOcclusionGuard.IsRegionUnobscured(new IntPtr(-1), new System.Drawing.Rectangle()));
        Assert.Equal(foreground, Native.GetForegroundWindow());
        Native.GetCursorPos(out var after);
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
    }

    [Fact]
    public void 滚轮增量不重复乘系统行数() => Assert.Equal(120, ScrollInjector.WheelNotchAmount());

    [Fact]
    public void 当前x64输入结构符合原生ABI()
    {
        if (IntPtr.Size != 8) return;
        Assert.Equal(40, Marshal.SizeOf<Native.INPUT>());
        Assert.Equal(8, Marshal.OffsetOf<Native.INPUT>(nameof(Native.INPUT.mi)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<Native.INPUT>(nameof(Native.INPUT.ki)).ToInt32());
    }

    [Fact]
    public void 客户区坐标转换有真实窗口集成覆盖()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var parent = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("OneBox native test")
                {
                    Width = 280, Height = 180, PositionX = -30000, PositionY = -30000,
                    WindowStyle = unchecked((int)0x80000000), // hidden WS_POPUP，不激活、不影响用户窗口。
                });
                using var child = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("OneBox child test")
                {
                    ParentWindow = parent.Handle, Width = 80, Height = 50, PositionX = 20, PositionY = 30,
                    WindowStyle = 0x40000000 | 0x10000000,
                });
                var clientPoint = new Native.POINT { X = -29970, Y = -29960 };
                Assert.True(Native.ScreenToClient(parent.Handle, ref clientPoint));
                Assert.Equal(30, clientPoint.X);
                Assert.Equal(40, clientPoint.Y);
                Assert.Equal(child.Handle, Native.ChildWindowFromPointEx(parent.Handle, clientPoint, Native.CWP_SKIPINVISIBLE));
                Assert.Equal(parent.Handle, Native.GetAncestor(child.Handle, Native.GA_ROOT));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "隐藏窗口测试超时");
        if (failure is not null) throw failure;
    }
}
