using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipboardFloat.App.Services;

/// <summary>全局热键（RegisterHotKey）注册与分发，支持多热键。</summary>
public sealed class HotkeyService : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_V = 0x56;
    public const uint VK_K = 0x4B;

    private const int WM_HOTKEY = 0x0312;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 0xA100;
    private bool _hooked;

    /// <param name="window">承载热键消息的窗口（需已创建句柄）。</param>
    public HotkeyService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
    }

    /// <summary>注册热键；返回是否成功（失败通常是被其他程序占用）。</summary>
    public bool Register(uint modifiers, uint vk, Action callback)
    {
        EnsureHook();
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers, vk)) return false;
        _callbacks[id] = callback;
        return true;
    }

    /// <summary>
    /// 探测组合键当前是否空闲（未被其他进程占用）。探测成功立即释放。
    /// 用于设置页自定义快捷键时的冲突检查（仅警告，不阻止保存）。
    /// </summary>
    public static bool ProbeAvailable(uint modifiers, uint vk, IntPtr hwnd)
    {
        const int probeId = 0x0001; // 与实例 id 段（0xA100+）不冲突
        if (RegisterHotKey(hwnd, probeId, modifiers, vk))
        {
            UnregisterHotKey(hwnd, probeId);
            return true;
        }
        return false;
    }

    private void EnsureHook()
    {
        if (_hooked) return;
        _source.AddHook(WndProc);
        _hooked = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _callbacks.Keys) UnregisterHotKey(_hwnd, id);
        _callbacks.Clear();
        if (_hooked)
        {
            _source.RemoveHook(WndProc);
            _hooked = false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
