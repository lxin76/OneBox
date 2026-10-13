using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

/// <summary>
/// 贴图置顶的层叠次序策略测试。
/// 关键回归点：周期性保活绝不能改变 Z 序，否则多张贴图会循环争抢置顶。
/// </summary>
public class TopmostPolicyTests
{
    [Fact]
    public void 保活重申_已置顶时不改变层叠次序()
    {
        // 这是本次修复的核心：多张贴图互相遮挡时，保活不得插队
        Assert.False(TopmostPolicy.ShouldReorder(alreadyTopmost: true, explicitRequest: false));
    }

    [Fact]
    public void 保活重申_尚未置顶时仍需插入置顶组()
    {
        // 只设了样式位但还没真正进入置顶组的窗口，保活要把它插进去
        Assert.True(TopmostPolicy.ShouldReorder(alreadyTopmost: false, explicitRequest: false));
    }

    [Fact]
    public void 显式请求_即使已置顶也抬到最前()
    {
        // 用户点击贴图、新建贴图、手动开启置顶都属显式意图
        Assert.True(TopmostPolicy.ShouldReorder(alreadyTopmost: true, explicitRequest: true));
        Assert.True(TopmostPolicy.ShouldReorder(alreadyTopmost: false, explicitRequest: true));
    }

    [Fact]
    public void 多张贴图反复保活_不会互相争抢次序()
    {
        // 模拟三张贴图：首次保活把它们插进置顶组，之后每轮都不该再改 Z 序
        const int pinCount = 3;
        var topmost = new bool[pinCount]; // 初始都还没真正进入置顶组
        var reorderCount = 0;

        // 每张贴图各经历 2 轮保活重申
        for (int round = 0; round < 2; round++)
        for (int i = 0; i < pinCount; i++)
        {
            if (TopmostPolicy.ShouldReorder(topmost[i], explicitRequest: false))
            {
                reorderCount++;
                topmost[i] = true; // 插入后即处于置顶状态
            }
        }

        // 只有第一轮的 3 次进入置顶组会改 Z 序，第二轮起完全不动
        Assert.Equal(pinCount, reorderCount);
    }
}
