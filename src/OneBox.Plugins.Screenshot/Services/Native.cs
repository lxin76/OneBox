using System.Runtime.InteropServices;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>
/// 截图插件的 Win32 互操作声明。
/// </summary>
internal static class Native
{
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal const int SRCCOPY = 0x00CC0020;
    internal const int CAPTUREBLT = 0x40000000;
    internal const int BI_RGB = 0;
    internal const int DIB_RGB_COLORS = 0;

    /// <summary>显示器个数（用于判断多屏环境）。</summary>
    internal const int SM_CMONITORS = 80;

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>取窗口（含非工作区）的设备上下文：滚动截长图从这里取图，避开屏幕上的覆盖层。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>PER_MONITOR_AWARE_V2 的既有句柄值。</summary>
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT point);

    /// <summary>在父窗口客户区坐标中命中直接子窗口；调用方负责转换屏幕坐标。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT pt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    internal const uint CWP_SKIPINVISIBLE = 0x0001;
    internal const uint CWP_SKIPDISABLED = 0x0002;
    internal const uint CWP_SKIPTRANSPARENT = 0x0004;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    /// <summary>直接父窗口；顶层窗口返回 <see cref="IntPtr.Zero"/>。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    internal const uint GA_ROOT = 2;

    /// <summary>取窗口类名（诊断用：区分浏览器渲染子窗口与顶层窗口）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

    /// <summary>窗口是否已启用（未启用的窗口不会收到任何鼠标消息）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>窗口样式 GWL_STYLE（判断 WS_VSCROLL 是否存在）。</summary>
    internal const int GWL_STYLE = -16;

    /// <summary>窗口是否带竖直滚动条。</summary>
    internal const int WS_VSCROLL = 0x00200000;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>把窗口提到前台（滚轮等合成输入只对前台窗口生效）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>句柄是否是有效窗口（可能已经关闭）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    // ---- 真实输入注入（SendInput）----
    //
    // 为什么必须用 SendInput 而不是 PostMessage：
    // 浏览器/Electron 一类窗口只处理来自系统输入队列的按键与滚轮，
    // 直接 PostMessage(WM_MOUSEWHEEL) 投递的消息不会触发页面滚动（实测投递成功但页面纹丝不动）。

    /// <summary>
    /// SendInput 的 INPUT 结构。
    ///
    /// 对齐很关键：Win32 的 INPUT 是 40 字节（x64），其中 type(4) 之后有 4 字节填充，
    /// 使 MOUSEINPUT（内含 IntPtr）落在 8 字节边界上。
    /// 少了这段填充，Marshal.SizeOf 会算出 36，SendInput 因 cbSize 不符直接返回 0——
    /// 表现就是“注入命令发出去了，但页面纹丝不动，而且没有任何报错”。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;

        /// <summary>键盘输入；与 <see cref="mi"/> 重叠在同一块联合体空间里，按 type 二选一。</summary>
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    internal const uint INPUT_MOUSE = 0;

    internal const uint INPUT_KEYBOARD = 1;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    internal const int VK_RBUTTON = 0x02;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    /// <summary>PageDown 的虚拟键码。</summary>
    internal const ushort VK_NEXT = 0x22;

    /// <summary>Esc 的虚拟键码。</summary>
    internal const ushort VK_ESCAPE = 0x1B;

    /// <summary>键盘输入结构（与 MOUSEINPUT 共用 INPUT 的联合体空间）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>取最近一次 Win32 调用的错误码。</summary>
    [DllImport("kernel32.dll")]
    internal static extern uint GetLastError();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>取当前线程 id（配合 AttachThreadInput 让窗口能被合法激活）。</summary>
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// 显示/隐藏窗口（同步生效，不依赖消息泵）。
    ///
    /// 抓帧循环跑在界面线程上同步阻塞执行，这期间 WPF 的消息泵不转，
    /// 改 Left/Top 根本不会真正移动窗口。需要"立刻让它从屏幕上消失"时只能用这条。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>隐藏窗口。</summary>
    internal const int SW_HIDE = 0;

    /// <summary>以"不抢焦点"的方式显示窗口（保持原层叠位置）。</summary>
    internal const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

    /// <summary>
    /// 等到 DWM 完成下一次合成提交。
    ///
    /// 抓帧前隐藏浮窗后，必须等底层内容被重新合成出来再 BitBlt，
    /// 否则会抓到"浮窗刚消失、底色还没补上"的中间帧，长图里就会出现一块空白或残影。
    /// </summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmFlush();

    /// <summary>读取值为 DWORD 的窗口属性（如被遮罩标记）。</summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>DWMWA_CLOAKED：UWP 挂起或虚拟桌面切换时窗口会被“遮罩”，不应参与取景。</summary>
    internal const int DWMWA_CLOAKED = 14;

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS：不含投影的实际窗口边界。</summary>
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>取窗口所在显示器的 DPI（Win10 1607+）。失败返回 0。</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    /// <summary>MOUSEEVENTF_WHEEL：发送滚轮事件（作用在光标所在位置）。</summary>
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;

    /// <summary>左键按下 / 抬起（用于把键盘焦点交给页面）。</summary>
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>一格滚轮的增量值。</summary>
    internal const int WHEEL_DELTA = 120;

    [DllImport("user32.dll")]
    internal static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    /// <summary>滚轮消息：发给窗口后由它自己换算成页面滚动。</summary>
    internal const uint WM_MOUSEWHEEL = 0x020A;

    /// <summary>标准滚动消息：直接要求窗口做一次「行/页/顶/底」滚动。</summary>
    internal const uint WM_VSCROLL = 0x0115;

    internal const int SB_LINEUP = 0;
    internal const int SB_LINEDOWN = 1;
    internal const int SB_PAGEUP = 2;
    internal const int SB_PAGEDOWN = 3;
    internal const int SB_THUMBPOSITION = 4;
    internal const int SB_THUMBTRACK = 5;
    internal const int SB_TOP = 6;
    internal const int SB_BOTTOM = 7;

    /// <summary>读取指定窗口的标准滚动条信息；调用方必须设置 cbSize 和 fMask，并立即记录失败码。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO info);

    internal const int SB_VERT = 1;
    internal const uint SIF_POS = 0x0004;
    internal const uint SIF_ALL = 0x17;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SCROLLINFO
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    /// <summary>
    /// 往窗口的消息队列投递消息（不等待窗口处理，也绝不因为对方卡在模态循环里而把本线程挂死）。
    ///
    /// 两点必须注意：
    ///  - 必须用 PostMessage 而不是 SendMessage：SendMessage 是跨进程同步调用，
    ///    目标窗口若正处在一个嵌套消息循环里（浏览器主进程、某些 Electron 窗口），
    ///    调用方会被无限期挂住，表现为「滚动截图卡死」。
    ///  - 必须用 PostMessageW 并显式传递 wParam 的 64 位值：Win32 的 PostMessage 接收的是
    ///    LPARAM（消息参数），若声明成 int 会在 x64 上把负数/高位截断成 32 位，
    ///    个别窗口的 64 位检查会让整条消息被丢弃。
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>把两个 16 位坐标拼成 MAKELPARAM 的 lParam。</summary>
    internal static IntPtr MakeLParam(int low, int high)
        => (IntPtr)((int)((ushort)low | ((uint)(ushort)high << 16)));

    /// <summary>把两个 16 位值拼成 MAKEWPARAM 的 wParam。</summary>
    internal static IntPtr MakeWParam(int low, int high)
        => (IntPtr)((int)((ushort)low | ((uint)(ushort)high << 16)));

    /// <summary>SPI_GETWHEELSCROLLLINES（104）：滚轮“每格滚动行数”设置。</summary>
    internal const uint SPI_GETWHEELSCROLLLINES = 104;

    /// <summary>
    /// 取系统“每格滚动行数”（pvParam 为 uint，返回 WHEEL_PAGESCROLL 表示“一次一页”）。
    /// 每格输入固定为 WHEEL_DELTA，行数/整页设置由接收程序解释，不应乘进输入增量。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);

    /// <summary>滚轮设置里“一次滚动一页”的取值。</summary>
    internal const uint WHEEL_PAGESCROLL = 0xFFFFFFFF;

    internal const int VK_LBUTTON = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);
}