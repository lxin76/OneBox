using System.Runtime.InteropServices;
using System.Windows.Input;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>
/// 宿主侧的按键录制：用低级键盘钩子（WH_KEYBOARD_LL）抓组合键。
///
/// 为什么不用窗口的 KeyDown：被其他程序 RegisterHotKey 占用的组合键，
/// 系统会直接吞掉、根本不送到前台窗口，KeyDown 永远收不到——
/// 而那恰恰是用户最需要看到提示的情形（"这个键已经被人占了"）。
/// 钩子在系统分发之前拿到按键，因此能捕获任何组合。
///
/// 钩子回调运行在安装钩子的线程上（这里是 UI 线程，需要消息泵），
/// 因此回调里只做换算，真正的界面更新交给调用方。
/// </summary>
public sealed class HotkeyRecorderService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private bool _active;

    public HotkeyRecorderService() => _proc = HookProc;

    /// <summary>是否正在录制。</summary>
    public bool IsRecording => _active;

    /// <summary>捕获到一个完整组合（已排除纯修饰键）。</summary>
    public event Action<string>? Captured;

    /// <summary>用户按了 Esc，应取消录制。</summary>
    public event Action? Cancelled;

    /// <summary>开始录制；已有录制在进行时先停止旧的。</summary>
    public bool Start()
    {
        Stop();
        _modState = ModState.None; // 每次录制从干净状态开始
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        _active = _hook != IntPtr.Zero;
        return _active;
    }

    /// <summary>停止录制并卸载钩子。重复调用安全。</summary>
    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _active = false;
        _modState = ModState.None;
    }

    /// <summary>
    /// 低级键盘钩子回调。
    ///
    /// 录制期间**吞掉全部按键**（返回 1），不再往下传递：否则用户按下正在录制的组合键时，
    /// 系统会照常把它派发给前台窗口和已注册的全局热键，正在改的那条热键会立刻触发一次插件功能
    /// （例如按 Ctrl+Alt+V 改键，置顶文本窗口会先弹出来）。录制只是"读取按键"，
    /// 不该产生任何业务副作用。
    /// </summary>
    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_active) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

        // 按下与松开都要处理：松开用于把修饰键从状态里移除，否则一只手放掉 Ctrl 之后
        // 后续单按的字母会被误判成 Ctrl+字母
        if (isDown || isUp)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            UpdateModifierState(data.vkCode, isDown);
            if (isDown) HandleKey(data.vkCode);
        }

        // 录制期间不放行任何按键（包括鼠标点进来的修饰键残留）
        return (isDown || isUp) ? new IntPtr(1) : CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 修饰键的按下状态，由钩子自己记账。
    ///
    /// 不能用 WPF 的 <c>Keyboard.Modifiers</c>：它读的是本线程的键盘状态，
    /// 钩子回调不一定能同步到，结果就是"明明按着 Ctrl，却判定成没有修饰键"，
    /// 组合被当成无效输入直接丢弃，界面停留在录制态、看起来像卡住了。
    /// 钩子拿到的是系统级按键事件，自己记账最可靠。
    /// </summary>
    [Flags]
    private enum ModState
    {
        None = 0,
        Control = 1,
        Shift = 2,
        Alt = 4,
        Win = 8,
    }

    private ModState _modState;

    /// <summary>虚拟键码 → 修饰键种类；不是修饰键返回 None。</summary>
    private static ModState ModifierOf(uint vkCode) => vkCode switch
    {
        0xA2 or 0xA3 => ModState.Control, // VK_LCONTROL / VK_RCONTROL
        0xA0 or 0xA1 => ModState.Shift,   // VK_LSHIFT / VK_RSHIFT
        0xA4 or 0xA5 => ModState.Alt,     // VK_LMENU / VK_RMENU
        0x5B or 0x5C => ModState.Win,     // VK_LWIN / VK_RWIN
        _ => ModState.None,
    };

    /// <summary>更新修饰键按下/松开状态。</summary>
    private void UpdateModifierState(uint vkCode, bool isDown)
    {
        var modifier = ModifierOf(vkCode);
        if (modifier == ModState.None) return;

        if (isDown) _modState |= modifier;
        else _modState &= ~modifier;
    }

    /// <summary>内部修饰键状态 → RegisterHotKey 的修饰位。</summary>
    private static uint ToHotkeyModifiers(ModState state)
    {
        uint mods = 0;
        if ((state & ModState.Control) != 0) mods |= HotkeyParser.MOD_CONTROL;
        if ((state & ModState.Shift) != 0) mods |= HotkeyParser.MOD_SHIFT;
        if ((state & ModState.Alt) != 0) mods |= HotkeyParser.MOD_ALT;
        if ((state & ModState.Win) != 0) mods |= HotkeyParser.MOD_WIN;
        return mods;
    }

    /// <summary>把虚拟键码 + 已记账的修饰键状态换算成组合串；不可用作热键按键时忽略。</summary>
    private void HandleKey(uint vkCode)
    {
        // 修饰键本身不构成组合
        if (ModifierOf(vkCode) != ModState.None) return;

        var key = KeyInterop.KeyFromVirtualKey((int)vkCode);

        if (key == Key.Escape)
        {
            Stop();
            Cancelled?.Invoke();
            return;
        }

        if (key == Key.None) return;

        // 必须至少有一个修饰键，否则不是可用的全局热键
        if (_modState == ModState.None) return;

        var combination = HotkeyParser.Format(ToHotkeyModifiers(_modState), vkCode);
        if (HotkeyParser.Parse(combination) is null) return;

        Stop();
        Captured?.Invoke(combination);
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
