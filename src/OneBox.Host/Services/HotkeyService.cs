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

    /// <summary>
    /// 探测组合键当前是否空闲（未被其他进程占用）。探测后立即释放。
    ///
    /// 用的是消息专用窗口（HWND_MESSAGE）而不是 <c>GetConsoleWindow()</c>：
    /// 宿主是 WinExe，根本没有控制台窗口，那个句柄恒为 0，
    /// 旧实现于是每次都直接"当作可用"，外部冲突检测形同虚设。
    ///
    /// 注意：宿主自己已经注册的键也会让探测失败（ERROR_HOTKEY_ALREADY_REGISTERED），
    /// 因此调用方必须先排除自身占用，否则会把"自己用的键"误报成"被别的软件占用"。
    /// 每次探测都新建并销毁一个窗口，避免与宿主主窗口的注册 id 空间互相干扰。
    /// </summary>
    public static bool ProbeAvailable(string combination)
    {
        if (HotkeyParser.Parse(combination) is not { } p) return false;

        var hwnd = CreateWindowEx(0, "STATIC", "OneBoxHotkeyProbe", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return true; // 建不出窗口就无法判断，不误报

        const int probeId = 0x0001;
        try
        {
            if (RegisterHotKey(hwnd, probeId, p.Modifiers | HotkeyParser.MOD_NOREPEAT, p.Vk))
            {
                UnregisterHotKey(hwnd, probeId);
                return true;
            }

            // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED：被某个进程占着，才算"不可用"
            // 其他错误（例如输入法把该组合整体拦下）不作为占用结论，按可用处理
            return Marshal.GetLastWin32Error() != ErrorHotkeyAlreadyRegistered;
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED：组合键已被某进程注册。</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>消息专用窗口的父句柄，不会出现在屏幕与任务栏上。</summary>
    private static readonly IntPtr HwndMessage = new(-3);

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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
