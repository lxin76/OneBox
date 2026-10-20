using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

/// <summary>
/// 滚动投递方式轮换逻辑的测试。
///
/// 这些用例锁死的是"滚动截图长期修不好"那件事的根因：投递方式没被接住时，
/// 正确反应是换一条路，而不是不断加大滚轮量。轮换顺序、何时放弃、全部试完后的终态，
/// 都必须稳定可预期，否则修好的行为会在下一次改动里悄悄退化。
/// </summary>
public class ScrollMethodPlanTests
{
    [Fact]
    public void 初始状态是首选方式且没有失败记录()
    {
        var plan = new ScrollMethodPlan();

        Assert.Equal(ScrollDelivery.PageDown, plan.Current);
        Assert.Empty(plan.Rejected);
        Assert.True(plan.HasUntried);
        Assert.Equal(0, plan.SwitchCount);
    }

    [Fact]
    public void 顺序以实测有效的方式优先()
    {
        // 依据 ScrollMethodPlan.Order 上记录的实测结论（Edge/Chromium，2026-10-19）：
        //   - 真实滚轮有效，排第一；
        //   - PageDown 有效，是滚轮失败时最可靠的后备，排第二；
        //   - WM_MOUSEWHEEL 对 Chromium 无效，只对部分 Electron/自绘控件有效，排第三；
        //   - WM_VSCROLL 对浏览器页面无效，仅为老式 Win32 窗口兜底，排最后。
        Assert.Equal(
            [ScrollDelivery.PageDown, ScrollDelivery.WheelInput, ScrollDelivery.WheelMessage, ScrollDelivery.VScroll],
            ScrollMethodPlan.Order);
    }

    [Fact]
    public void 实测无效的方式必须排在实测有效的方式之后()
    {
        var order = ScrollMethodPlan.Order.ToList();

        // 这两个在 Edge/Chromium 上实测不滚页面，不能排在有效方式前面，
        // 否则每次滚动都会先白试两次，用户看到的是一段明显卡顿。
        var wheelMessageAt = order.IndexOf(ScrollDelivery.WheelMessage);
        var vScrollAt = order.IndexOf(ScrollDelivery.VScroll);

        Assert.True(wheelMessageAt > order.IndexOf(ScrollDelivery.WheelInput));
        Assert.True(wheelMessageAt > order.IndexOf(ScrollDelivery.PageDown));
        Assert.True(vScrollAt > order.IndexOf(ScrollDelivery.PageDown));
    }

    [Fact]
    public void 投递失败后按顺序换到下一种()
    {
        var plan = new ScrollMethodPlan();

        Assert.True(plan.TryRejectCurrent());
        Assert.Equal(ScrollDelivery.WheelInput, plan.Current);

        Assert.True(plan.TryRejectCurrent());
        Assert.Equal(ScrollDelivery.WheelMessage, plan.Current);

        Assert.True(plan.TryRejectCurrent());
        Assert.Equal(ScrollDelivery.VScroll, plan.Current);
    }

    [Fact]
    public void 每次轮换都记下被否决的方式()
    {
        var plan = new ScrollMethodPlan();
        plan.TryRejectCurrent();
        plan.TryRejectCurrent();

        Assert.Equal([ScrollDelivery.PageDown, ScrollDelivery.WheelInput], plan.Rejected);
        Assert.Equal(2, plan.SwitchCount);
    }

    [Fact]
    public void 全部方式都试完后返回false并把当前复位到首选()
    {
        var plan = new ScrollMethodPlan();

        for (var i = 0; i < ScrollMethodPlan.Order.Length - 1; i++)
            Assert.True(plan.TryRejectCurrent());

        // 最后一次：没有下一种可选
        Assert.False(plan.TryRejectCurrent());

        // 终态必须稳定：不能停在一个"随机"的方式上，否则日志与行为每次都不一样
        Assert.Equal(ScrollDelivery.PageDown, plan.Current);
        Assert.False(plan.HasUntried);
    }

    [Fact]
    public void 已被否决的方式不会被再次选中()
    {
        var plan = new ScrollMethodPlan();

        // 一路否决到底，期间绝不允许重复回到已否决的方式
        var seen = new List<ScrollDelivery> { plan.Current };
        while (plan.TryRejectCurrent())
            seen.Add(plan.Current);

        Assert.Equal(ScrollMethodPlan.Order.Length, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(ScrollMethodPlan.Order, seen);
    }

    [Fact]
    public void 轮换次数等于被否决的方式数()
    {
        var plan = new ScrollMethodPlan();
        plan.TryRejectCurrent();
        plan.TryRejectCurrent();
        plan.TryRejectCurrent();

        Assert.Equal(plan.Rejected.Count, plan.SwitchCount);
    }
}