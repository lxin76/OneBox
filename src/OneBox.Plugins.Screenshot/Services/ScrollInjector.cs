using System.Runtime.InteropServices;
using System.Text;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>保留旧调用方的滚动投递方式标识；新流程只显式使用 WheelInput，不自动切换或回退。</summary>
public enum ScrollMethod
{
    /// <summary>SendInput 注入真实滚轮事件（和用户自己滚鼠标完全等价）。</summary>
    WheelInput,

    /// <summary>往光标下的渲染子窗口投递 WM_MOUSEWHEEL（绕过输入队列，但 Chromium 只认光标位置）。</summary>
    WheelMessage,

    /// <summary>往子窗口投递 WM_VSCROLL / SB_PAGEDOWN（老式 Win32 与部分 Electron 窗口认这条）。</summary>
    VScroll,

    /// <summary>SendInput 注入 PageDown 键（页面获得键盘焦点时最可靠的一条）。</summary>
    PageDown,
}

/// <summary>一次滚动的结果。</summary>
public readonly record struct ScrollAttempt(ScrollMethod Method, bool Delivered);

/// <summary>截图专用的滚动指令投递器：仅向已就绪的目标投递输入，不激活窗口或模拟点击。</summary>
public static class ScrollInjector
{
    private const int WheelDelta = Native.WHEEL_DELTA;

    /// <summary>从屏幕坐标逐层命中客户区子窗口，且每一层必须仍属于最初命中的根窗口。</summary>
    public static IntPtr HitTestFeedbackWindow(int x, int y)
    {
        var screenPoint = new Native.POINT { X = x, Y = y };
        var hit = Native.WindowFromPoint(screenPoint);
        if (hit == IntPtr.Zero) return IntPtr.Zero;
        var root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root == IntPtr.Zero) return IntPtr.Zero;

        var window = root;
        for (var i = 0; i < 32; i++)
        {
            // ChildWindowFromPointEx 接受当前父窗口的客户区坐标，不能复用屏幕坐标。
            var clientPoint = screenPoint;
            if (!Native.ScreenToClient(window, ref clientPoint))
            {
                var error = Marshal.GetLastWin32Error();
                LogWin32Failure("ScreenToClient", window, error);
                return IntPtr.Zero;
            }
            var child = Native.ChildWindowFromPointEx(window, clientPoint,
                Native.CWP_SKIPINVISIBLE | Native.CWP_SKIPDISABLED | Native.CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == window) break;
            if (!Native.IsWindow(child) || Native.GetAncestor(child, Native.GA_ROOT) != root)
            {
                AppLog.Write($"滚动命中测试拒绝越界子窗口：root=0x{root.ToInt64():X} child=0x{child.ToInt64():X}");
                return IntPtr.Zero;
            }
            window = child;
        }
        // 窗口可能在命中过程中关闭或重新挂接，返回前再次检查归属及实际落点。
        var finalHit = Native.WindowFromPoint(screenPoint);
        return Native.IsWindow(window) && Native.GetAncestor(window, Native.GA_ROOT) == root
            && finalHit != IntPtr.Zero && Native.GetAncestor(finalHit, Native.GA_ROOT) == root
            ? window : IntPtr.Zero;
    }

    /// <summary>只检查就绪状态，不移动光标、不激活、不点击；targetRoot 必须是原始顶层根窗口。</summary>
    public static bool IsTargetReady(IntPtr targetRoot, int x, int y)
    {
        if (!IsForegroundTarget(targetRoot)) return false;
        var hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        return hit != IntPtr.Zero && Native.GetAncestor(hit, Native.GA_ROOT) == targetRoot;
    }

    private static bool IsForegroundTarget(IntPtr targetRoot) =>
        targetRoot != IntPtr.Zero && Native.IsWindow(targetRoot)
        && Native.GetAncestor(targetRoot, Native.GA_ROOT) == targetRoot
        && Native.IsWindowVisible(targetRoot) && Native.IsWindowEnabled(targetRoot)
        && !Native.IsIconic(targetRoot) && Native.GetForegroundWindow() == targetRoot;

    /// <summary>仅依据实时高位按下状态；不使用不可靠的 GetAsyncKeyState 低位历史标记。</summary>
    public static bool AreInputButtonsReleased() =>
        !IsPressed(Native.VK_CONTROL) && !IsPressed(Native.VK_SHIFT) && !IsPressed(Native.VK_MENU)
        && !IsPressed(Native.VK_LWIN) && !IsPressed(Native.VK_RWIN)
        && !IsPressed(Native.VK_LBUTTON) && !IsPressed(Native.VK_RBUTTON);

    public static bool IsEscapePressed() => IsPressed(Native.VK_ESCAPE);

    private static bool IsPressed(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>
    /// 单次 SendInput 投递一个滚轮事件，wheelDelta 正值向下、负值向上。
    /// 不移动光标、不激活、不点击；调用方须先将光标安全定位到指定屏幕坐标。
    /// 实际光标必须仍在该坐标，避免把滚轮发往坐标以外的窗口或容器。
    /// 返回 true 仅表示系统接受了输入，不保证页面发生滚动。
    /// </summary>
    public static bool TrySendWheel(IntPtr targetRoot, int screenX, int screenY, int wheelDelta)
    {
        if (wheelDelta == 0 || wheelDelta == int.MinValue) return false;
        if (!IsTargetReady(targetRoot, screenX, screenY) || !AreInputButtonsReleased() || IsEscapePressed())
            return false;
        if (!Native.GetCursorPos(out var cursor))
        {
            var error = Marshal.GetLastWin32Error();
            LogWin32Failure("GetCursorPos", targetRoot, error);
            return false;
        }
        if (cursor.X != screenX || cursor.Y != screenY) return false;
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            mi = new Native.MOUSEINPUT
            {
                dwFlags = Native.MOUSEEVENTF_WHEEL,
                mouseData = unchecked((uint)-wheelDelta),
            },
        };
        // 构造输入后紧邻 SendInput 再检查，尽量收窄前台切换/用户接管的竞态窗口。
        if (!IsTargetReady(targetRoot, screenX, screenY) || !AreInputButtonsReleased() || IsEscapePressed())
            return false;
        if (!Native.GetCursorPos(out cursor))
        {
            var error = Marshal.GetLastWin32Error();
            LogWin32Failure("GetCursorPos", targetRoot, error);
            return false;
        }
        if (cursor.X != screenX || cursor.Y != screenY) return false;
        return SendInputs(targetRoot, [input], "SendInput(Wheel)");
    }

    /// <summary>取窗口类名（诊断日志用）。</summary>
    public static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "-";
        var sb = new StringBuilder(256);
        if (Native.GetClassNameW(hwnd, sb, sb.Capacity) > 0) return sb.ToString();
        var error = Marshal.GetLastWin32Error();
        LogWin32Failure("GetClassNameW", hwnd, error);
        return "-";
    }

    /// <summary>只读取指定窗口自己的竖直滚动位置；不退读父窗口，避免随后向子窗口恢复父位置。</summary>
    public static bool TryGetScrollPosition(IntPtr hwnd, out int position)
    {
        position = 0;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        var info = new Native.SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<Native.SCROLLINFO>(),
            fMask = Native.SIF_POS,
        };
        if (!Native.GetScrollInfo(hwnd, Native.SB_VERT, ref info))
        {
            var error = Marshal.GetLastWin32Error();
            LogWin32Failure("GetScrollInfo", hwnd, error);
            return false;
        }
        position = info.nPos;
        return true;
    }

    /// <summary>兼容消息投递；amount 正值向下。新滚动流程应使用 TrySendWheel，不作隐式回退。</summary>
    public static bool PostWheelMessage(IntPtr target, int x, int y, int amount)
    {
        if (target == IntPtr.Zero || amount == 0 || !Native.IsWindow(target)) return false;
        var root = Native.GetAncestor(target, Native.GA_ROOT);
        if (!IsTargetReady(root, x, y) || !AreInputButtonsReleased() || IsEscapePressed()) return false;
        var delta = amount > 0 ? -WheelDelta : WheelDelta;
        if (Native.PostMessage(target, Native.WM_MOUSEWHEEL,
            Native.MakeWParam(0, delta), Native.MakeLParam(x, y))) return true;
        var error = Marshal.GetLastWin32Error();
        LogWin32Failure("PostMessage(WM_MOUSEWHEEL)", target, error);
        return false;
    }

    /// <summary>兼容标准滚动条消息；新滚动流程不应将它作为隐式回退。</summary>
    public static bool PostVScrollPage(IntPtr target, bool down, bool bottom = false)
    {
        if (target == IntPtr.Zero || !Native.IsWindow(target)
            || !IsForegroundTarget(Native.GetAncestor(target, Native.GA_ROOT))
            || !AreInputButtonsReleased() || IsEscapePressed()) return false;
        var code = bottom ? Native.SB_BOTTOM : down ? Native.SB_PAGEDOWN : Native.SB_PAGEUP;
        if (Native.PostMessage(target, Native.WM_VSCROLL, (IntPtr)code, IntPtr.Zero)) return true;
        var error = Marshal.GetLastWin32Error();
        LogWin32Failure("PostMessage(WM_VSCROLL)", target, error);
        return false;
    }

    /// <summary>仅供旧调用方二进制/源码兼容的空操作。不要调用：自动点击不能安全取得页面焦点。</summary>
    public static void FocusPage(int x, int y)
    {
        // 刻意不做任何事：不移动光标、不改变焦点，也绝不注入鼠标点击。
    }

    /// <summary>兼容 PageDown：targetRoot 必须是当前前台根窗口；一次 SendInput 包含按下和抬起。</summary>
    public static bool SendPageDown(IntPtr targetRoot)
    {
        if (!IsForegroundTarget(targetRoot) || !AreInputButtonsReleased() || IsEscapePressed()
            || IsPressed(Native.VK_NEXT)) return false;
        var down = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            ki = new Native.KEYBDINPUT { wVk = Native.VK_NEXT, dwFlags = Native.KEYEVENTF_EXTENDEDKEY },
        };
        var up = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            ki = new Native.KEYBDINPUT
            {
                wVk = Native.VK_NEXT,
                dwFlags = Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP,
            },
        };
        if (!IsForegroundTarget(targetRoot) || !AreInputButtonsReleased() || IsEscapePressed()) return false;
        return SendInputs(targetRoot, [down, up], "SendInput(PageDown)");
    }

    private static bool SendInputs(IntPtr targetRoot, Native.INPUT[] inputs, string operation)
    {
        // 当前 INPUT 联合体维持项目已有 x64 ABI（40 字节，联合体偏移 8）；不在其他架构冒险注入。
        if (IntPtr.Size != 8 || Marshal.SizeOf<Native.INPUT>() != 40
            || Marshal.OffsetOf<Native.INPUT>(nameof(Native.INPUT.mi)).ToInt32() != 8
            || Marshal.OffsetOf<Native.INPUT>(nameof(Native.INPUT.ki)).ToInt32() != 8)
        {
            AppLog.Write($"{operation} 拒绝：INPUT 布局不是受支持的 x64 ABI");
            return false;
        }
        var sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
        // 必须在日志等其他互操作之前抓取错误；部分投递也属于失败，不自动重试或切换 mouse_event。
        var error = Marshal.GetLastWin32Error();
        if (sent == (uint)inputs.Length) return true;
        AppLog.Write($"{operation} 失败：root=0x{targetRoot.ToInt64():X} sent={sent}/{inputs.Length} Win32Error={error}（SendInput 的 UIPI 拦截可能不设置错误码）");
        return false;
    }

    private static void LogWin32Failure(string operation, IntPtr hwnd, int error) =>
        AppLog.Write($"{operation} 失败：hwnd=0x{hwnd.ToInt64():X} Win32Error={error}");

    /// <summary>一格固定为 WHEEL_DELTA（120）；系统滚动行数由接收程序处理，不能重复乘算。</summary>
    public static int WheelNotchAmount() => WheelDelta;
}
