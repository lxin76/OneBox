namespace OneBox.Plugins.Screenshot.Core;

/// <summary>滚动指令的投递方式。与界面/系统无关，纯枚举，便于单元测试。</summary>
public enum ScrollDelivery
{
    /// <summary>SendInput 注入真实滚轮事件。</summary>
    WheelInput,

    /// <summary>往渲染子窗口投递 WM_MOUSEWHEEL。</summary>
    WheelMessage,

    /// <summary>SendInput 注入 PageDown 键。</summary>
    PageDown,

    /// <summary>往窗口投递 WM_VSCROLL / SB_PAGEDOWN。</summary>
    VScroll,
}

/// <summary>
/// 旧版投递轮换的兼容类型；v2 生产流程不使用本类，也不自动切换 PageDown/消息。
///
/// 为什么要把这件事单独抽出来：滚动截图长期修不好的根因，是"投递方式没被接住"时
/// 代码只会不断加大滚轮量，而问题根本不在"量"上。正确做法是换一条投递路径再试。
/// "什么时候该换、换成哪一条、换完了还不行怎么办"是一组可以完整枚举的状态转移，
/// 不依赖任何窗口句柄或系统调用，因此应当被单元测试锁死，而不是散落在抓帧循环里靠实测碰运气。
/// </summary>
public sealed class ScrollMethodPlan
{
    /// <summary>
    /// 尝试顺序。
    ///
    /// 实测结论（Microsoft Edge / Chromium 页面，测试页为连续彩色条带，2026-10-19）：
    ///   1. WheelInput —— 有效，但推进量不可控（受系统"每格行数"与浏览器缩放影响），
    ///      作为 PageDown 的替补。前提是目标窗口在前台、光标落在页面内容上。
    ///   2. PageDown —— 有效；**排在首位**。一次就是一屏，不受滚轮行数设置影响。
    ///      滚轮（WheelInput）一格的推进量取决于浏览器与系统设置，实测只推进约 100px，
    ///      而页面高常常有几千 px，靠滚轮要几十次，既慢又容易在中途被合并/限流搞乱。
    ///   3. WheelMessage —— 对 Chromium 实测无效（投递返回成功，页面不动），
    ///      但部分 Electron / 自绘控件只认这条消息，因此保留在后备位置。
    ///   4. VScroll —— 对 Chromium 页面内容实测无效（浏览器自己不处理页面滚动），
    ///      仅为老式 Win32 带滚动条的窗口兜底，放最后。
    ///
    /// 这组结论由 ScrollMethodPlanTests 锁死顺序；若将来实测结果变化，应同时更新两处。
    /// </summary>
    public static readonly ScrollDelivery[] Order =
    [
        ScrollDelivery.PageDown,
        ScrollDelivery.WheelInput,
        ScrollDelivery.WheelMessage,
        ScrollDelivery.VScroll,
    ];

    private readonly HashSet<ScrollDelivery> _rejected = [];

    /// <summary>当前生效的投递方式。</summary>
    public ScrollDelivery Current { get; private set; } = Order[0];

    /// <summary>已经被证明无效的方式。</summary>
    public IReadOnlyCollection<ScrollDelivery> Rejected => _rejected;

    /// <summary>已经轮换过几次（诊断用）。</summary>
    public int SwitchCount { get; private set; }

    /// <summary>是否还有没试过的方式。</summary>
    public bool HasUntried => Order.Any(m => !_rejected.Contains(m));

    /// <summary>
    /// 当前方式投递后画面毫无变化：把它标记为无效，并切换到下一条没试过的路。
    ///
    /// 返回 true 表示"已换到新方式，应立刻用新方式重试"；
    /// 返回 false 表示所有方式都试过了，调用方应当按"已到底 / 无法继续"处理。
    /// 全部试完时会把当前方式复位到首选，避免留下一个随机的终态。
    /// </summary>
    public bool TryRejectCurrent()
    {
        _rejected.Add(Current);

        foreach (var candidate in Order)
        {
            if (_rejected.Contains(candidate)) continue;

            Current = candidate;
            SwitchCount++;
            return true;
        }

        Current = Order[0];
        return false;
    }
}