using System.Runtime.InteropServices;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>
/// 键盘注入：为「抓取选中文字」模拟一次 Ctrl+C，以及热键回调前后的修饰键处理。
///
/// 只保留这一个注入方向——把文本放进剪贴板由文本框自身的 Ctrl+C 完成，
/// 粘贴也由目标软件自己的 Ctrl+V 完成，插件不再模拟 Ctrl+V。
/// </summary>
public static class PasteService
{
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const byte VK_SHIFT = 0x10;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_RWIN = 0x5C;
    private const byte VK_C = 0x43;

    private static bool IsKeyDown(byte vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>模拟 Ctrl+C 复制前台窗口里选中的内容（"抓取选中文字"流程用）。
    /// 注入后自检：若 Ctrl 从"未按下"变为"按下"（说明我们的 Ctrl-up 丢失），自动补发释放，防止键盘卡键。</summary>
    public static bool CopyByKey()
    {
        bool ctrlDownBefore = IsKeyDown(VK_CONTROL);
        PressAndRelease(VK_CONTROL, VK_C);
        Thread.Sleep(15);
        if (!ctrlDownBefore && IsKeyDown(VK_CONTROL))
        {
            ReleaseKey(VK_CONTROL);
            AppLog.Write("检测到注入导致 Ctrl 卡住，已自动释放");
        }
        return true;
    }

    /// <summary>按顺序按下-抬起 Ctrl+key（keybd_event，VK 码）。
    /// 用 keybd_event 而不是 SendInput：扫描码注入对 WPF 文本框不生效（实测）。</summary>
    private static void PressAndRelease(byte ctrl, byte key)
    {
        keybd_event(ctrl, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);
        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(15);
        keybd_event(ctrl, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>
    /// 等热键修饰键（Ctrl/Alt/Shift/Win）松开后再执行动作（后台线程）。
    /// 热键回调中注入按键前必须调用：否则注入的键会带着热键的修饰键（如 Ctrl+Alt+C 里
    /// Alt 未松开 → 注入的 C 变成 Ctrl+Alt+C，而不是复制命令）。
    /// </summary>
    public static void RunAfterModifiersReleased(Action action, int timeoutMs = 1000)
    {
        var thread = new Thread(() =>
        {
            for (int i = 0; i < timeoutMs / 25; i++)
            {
                if (!IsKeyDown(VK_CONTROL) && !IsKeyDown(VK_MENU)
                    && !IsKeyDown(VK_SHIFT) && !IsKeyDown(VK_LWIN) && !IsKeyDown(VK_RWIN))
                    break;
                Thread.Sleep(25);
            }
            action();
        })
        {
            IsBackground = true,
            Name = "HotkeyReleaseAwaiter",
        };
        thread.Start();
    }

    /// <summary>
    /// 兜底释放所有修饰键（Ctrl/Shift/Alt/Win）。
    /// 用于注入异常后自愈，以及用户手动"修复键盘输入"（托盘菜单）。
    /// </summary>
    public static void ReleaseModifiers()
    {
        ReleaseKey(VK_SHIFT);
        ReleaseKey(VK_CONTROL);
        ReleaseKey(VK_MENU);
        ReleaseKey(VK_LWIN);
        ReleaseKey(VK_RWIN);
    }

    private static void ReleaseKey(byte vk) =>
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
