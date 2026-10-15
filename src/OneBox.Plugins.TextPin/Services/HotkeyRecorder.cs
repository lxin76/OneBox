using System.Runtime.InteropServices;
using System.Windows.Input;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>
/// 热键录制：低级键盘钩子（WH_KEYBOARD_LL）捕获组合键。
/// 能捕获被其他程序 RegisterHotKey 占用的按键（系统会拦截该类按键、不发给前台窗口，
/// 普通 KeyDown 事件收不到，低级钩子可以）。
/// </summary>
public sealed class HotkeyRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    /// <summary>捕获到按键（UI 线程回调）。key 为实际按键，modifiers 为当前修饰键状态。</summary>
    public event Action<Key, ModifierKeys>? KeyCaptured;

    public HotkeyRecorder() => _proc = HookProc;

    /// <summary>开始监听。返回是否成功。</summary>
    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = KeyInterop.KeyFromVirtualKey((int)kbd.vkCode);
                KeyCaptured?.Invoke(key, Keyboard.Modifiers);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
