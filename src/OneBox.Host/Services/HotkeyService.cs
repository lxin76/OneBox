using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>
/// 宿主全局热键托管：所有插件声明的热键都由宿主在这一个窗口上注册，
/// 从而避免插件之间互相抢占，也能统一检测与外部程序的冲突。
/// </summary>
public sealed class HotkeyService : IHotkeyRegistrar, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    /// <summary>热键 id 起始值，避免与探测用的 0x0001 冲突。</summary>
    private const int FirstId = 0xB000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = FirstId;
    private bool _hooked;

    public HotkeyService(Window window)
        : this(HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
               ?? throw new InvalidOperationException("窗口句柄尚未创建，无法注册全局热键"))
    {
    }

    /// <summary>直接用消息窗口构造（单元测试用，无需 WPF Window）。</summary>
    internal HotkeyService(HwndSource source)
    {
        _source = source;
        _hwnd = source.Handle;
    }

    /// <summary>注册热键；返回失败原因（成功时返回 null）。</summary>
    public string? Register(string combination, Action callback)
    {
        var parsed = HotkeyParser.Parse(combination);
        if (parsed is not { } p)
            return $"无法解析的热键组合：{combination}";

        EnsureHook();
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, p.Modifiers | HotkeyParser.MOD_NOREPEAT, p.Vk))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1409
                ? $"热键 {combination} 已被其他程序占用"
                : $"热键 {combination} 注册失败（错误码 {error}）";
        }

        _callbacks[id] = callback;
        return null;
    }

    /// <summary>探测组合键当前是否空闲（未被其他进程占用）。探测成功立即释放。</summary>
    public static bool ProbeAvailable(string combination)
    {
        if (HotkeyParser.Parse(combination) is not { } p) return false;

        var hwnd = GetConsoleWindow();
        if (hwnd == IntPtr.Zero) return true; // 无窗口时无法探测，按可用处理

        const int probeId = 0x0001;
        if (RegisterHotKey(hwnd, probeId, p.Modifiers | HotkeyParser.MOD_NOREPEAT, p.Vk))
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
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                HostLog.Write($"热键回调异常: {ex.Message}");
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>注销全部热键（插件停用或宿主退出时调用）。</summary>
    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys) UnregisterHotKey(_hwnd, id);
        _callbacks.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
