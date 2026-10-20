using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>桌面像素捕获前后的保守遮挡检查；不激活窗口，不移动光标，不注入输入。</summary>
public static class ScrollOcclusionGuard
{
    private const uint GwHwndNext = 2;
    private const uint GwHwndPrevious = 3;
    private const uint GaRoot = 2;
    private const int DwmwaCloaked = 14;
    private const int MaxWindows = 4096;

    /// <summary>region 使用物理屏幕坐标。仅在完整遍历目标上方窗口且未发现交集时返回 true。</summary>
    /// <remarks>这是瞬时检查，不是原子屏幕快照；调用方仍须在抓帧前后各检查一次。
    /// 分层、透明、禁用和工具窗口均不放行，宁可拒绝也不捕获潜在的其他窗口内容。</remarks>
    public static bool IsRegionUnobscured(IntPtr root, Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0 || !IsValidRoot(root, out var initialRect)) return false;
        var right = (long)region.X + region.Width;
        var bottom = (long)region.Y + region.Height;
        if (region.Left < initialRect.Left || region.Top < initialRect.Top ||
            right > initialRect.Right || bottom > initialRect.Bottom) return false;

        var first = GetTopWindow(IntPtr.Zero);
        if (first == IntPtr.Zero) return false;
        var previous = GetWindow(root, GwHwndPrevious);
        var desktop = GetDesktopWindow();
        var seen = new HashSet<IntPtr>();
        var window = first;
        for (var count = 0; window != IntPtr.Zero && count < MaxWindows; count++)
        {
            // Z 序会并发变化：失效句柄、循环、目标未被遍历到均保守拒绝。
            if (!seen.Add(window) || !IsWindow(window)) return false;
            if (window == root)
            {
                return IsValidRoot(root, out var finalRect) && SameRect(initialRect, finalRect) &&
                    GetTopWindow(IntPtr.Zero) == first && GetWindow(root, GwHwndPrevious) == previous;
            }

            var next = GetWindow(window, GwHwndNext);
            if (window != desktop && IsWindowVisible(window) && !IsIconic(window) && !IsCloaked(window) &&
                !IsDesktopClass(window) && GetWindowRect(window, out var rect) &&
                rect.Right > rect.Left && rect.Bottom > rect.Top &&
                rect.Left < right && rect.Top < bottom && rect.Right > region.Left && rect.Bottom > region.Top)
                return false;
            window = next;
        }
        return false;
    }

    private static bool IsValidRoot(IntPtr root, out WindowRect rect)
    {
        rect = default;
        return root != IntPtr.Zero && IsWindow(root) && GetAncestor(root, GaRoot) == root &&
            IsWindowVisible(root) && !IsIconic(root) && !IsCloaked(root) &&
            GetWindowRect(root, out rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsDesktopClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        if (GetClassName(window, name, name.Capacity) == 0) return false;
        var value = name.ToString();
        return value is "Progman" or "WorkerW" or "#32769";
    }

    private static bool SameRect(WindowRect a, WindowRect b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
