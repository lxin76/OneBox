namespace OneBox.Plugins.Screenshot.Core;

/// <summary>滚动输入的纯逻辑边界：生产流程允许选区正文内移动；保留旧中心抖动判据供兼容。</summary>
public static class ScrollInputPolicy
{
    /// <summary>允许选区正文内的当前位置；排除边缘滚动条，不强制回到中心。窗口归属由原生适配层另验。</summary>
    public static bool IsInsideCapture(int left, int top, int width, int height, int cursorX, int cursorY, bool buttonsReleased)
    {
        if (!buttonsReleased || width <= 0 || height <= 0) return false;
        var insetX = Math.Min(20, width / 4);
        var insetY = Math.Min(8, height / 4);
        return cursorX >= (long)left + insetX && cursorX < (long)left + width - insetX
            && cursorY >= (long)top + insetY && cursorY < (long)top + height - insetY;
    }

    public static bool CanUsePointer(int anchorX, int anchorY, int cursorX, int cursorY, bool buttonsReleased)
        => buttonsReleased && Math.Abs((long)cursorX - anchorX) <= 12 && Math.Abs((long)cursorY - anchorY) <= 12;
}
