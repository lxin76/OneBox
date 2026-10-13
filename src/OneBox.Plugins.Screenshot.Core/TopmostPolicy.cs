namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 贴图窗口置顶时的层叠次序策略。
///
/// 背景：Win32 的 <c>SetWindowPos(hwnd, HWND_TOPMOST, ...)</c> 不只是「确保置顶」，
/// 它还会把窗口插到整个置顶组的最前面。多张贴图互相遮挡时，若各自周期性重申置顶，
/// 就会不断互相插队——表现为画面闪烁、谁也稳不住在上层。
///
/// 因此区分两种意图：
/// <list type="bullet">
/// <item>保活重申（周期计时器）：只确认「处于置顶组」，不改变组内次序。</item>
/// <item>显式请求（新建贴图、用户开启置顶、用户点击贴图）：允许抬到最前。</item>
/// </list>
/// 多张贴图之间的遮挡关系交给用户手动拖拽决定，程序不再自动干预。
/// </summary>
public static class TopmostPolicy
{
    /// <summary>
    /// 本次置顶操作是否应改变 Z 序（把窗口插到置顶组最前面）。
    /// </summary>
    /// <param name="alreadyTopmost">窗口当前是否已处于置顶状态。</param>
    /// <param name="explicitRequest">
    /// 是否为用户的显式意图（新建贴图、开启置顶、点击贴图）。
    /// 周期性的保活重申必须传 false。
    /// </param>
    public static bool ShouldReorder(bool alreadyTopmost, bool explicitRequest)
        => explicitRequest || !alreadyTopmost;
}
