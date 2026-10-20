using System.Runtime.InteropServices;
using System.Text;

internal static class P
{
    private const uint WM_MOUSEWHEEL = 0x020A, WM_VSCROLL = 0x0115, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int SB_PAGEDOWN = 3, SB_THUMBPOSITION = 4, VK_NEXT = 0x22, VK_NEXT_U = 0x22;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, MOUSEEVENTF_WHEEL = 0x0800, KEYEVENTF_KEYUP = 2;
    private const uint CWP_SKIPINVISIBLE = 1, CWP_SKIPDISABLED = 2, CWP_SKIPTRANSPARENT = 4;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct SCROLLINFO { public uint cbSize, fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint f);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] p, int s);
    [DllImport("user32.dll", EntryPoint="PostMessageW")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint u, out IntPtr bits, IntPtr s, uint o);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d,int dx,int dy,int w,int h,IntPtr s,int sx,int sy,int rop);
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int colors; }

    private static void Activate(IntPtr h)
    {
        var tt = GetWindowThreadProcessId(h, out _);
        var ct = GetCurrentThreadId();
        var att = tt != 0 && tt != ct && AttachThreadInput(ct, tt, true);
        SetForegroundWindow(h); BringWindowToTop(h);
        if (att) AttachThreadInput(ct, tt, false);
        Thread.Sleep(250);
    }

    private static byte[] Capture(int cx, int cy)
    {
        const int W = 500, H = 500;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var buf = new byte[W * H * 4];
        var info = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 } };
        var dib = CreateDIBSection(mem, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib != IntPtr.Zero)
        {
            var old = SelectObject(mem, dib);
            BitBlt(mem, 0, 0, W, H, screen, cx - W/2, cy - H/2, 0x00CC0020 | 0x40000000);
            SelectObject(mem, old);
            Marshal.Copy(bits, buf, 0, buf.Length);
            DeleteObject(dib);
        }
        DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return buf;
    }

    private static int Diff(byte[] a, byte[] b)
    {
        long t = 0; int n = 0;
        for (int i = 0; i + 3 < a.Length; i += 40)
        {
            int la = (a[i+2]*77 + a[i+1]*151 + a[i]*28) >> 8;
            int lb = (b[i+2]*77 + b[i+1]*151 + b[i]*28) >> 8;
            t += Math.Abs(la - lb); n++;
        }
        return n == 0 ? -1 : (int)(t / n);
    }

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int x = int.Parse(args[0]), y = int.Parse(args[1]);
        var root = WindowFromPoint(new POINT { X = x, Y = y });
        var child = root;
        for (int i = 0; i < 8; i++)
        {
            var c = ChildWindowFromPointEx(child, new POINT { X = x, Y = y }, CWP_SKIPINVISIBLE|CWP_SKIPDISABLED|CWP_SKIPTRANSPARENT);
            if (c == IntPtr.Zero || c == child) break;
            child = c;
        }
        Console.WriteLine($"root=0x{root.ToInt64():X} child=0x{child.ToInt64():X}");

        // 读滚动条位置：这是判断"页面动没动"最直接的依据
        int Pos(IntPtr h)
        {
            foreach (var t in new[] { h, root })
            {
                var si = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = 0x17 };
                if (GetScrollInfo(t, 1, ref si)) return si.nPos;
            }
            return -1;
        }

        Activate(root);
        SetCursorPos(x, y);
        Thread.Sleep(300);
        Console.WriteLine($"foreground=0x{GetForegroundWindow().ToInt64():X}  target=0x{root.ToInt64():X}");

        void Try(string name, Action a)
        {
            var before = Capture(x, y);
            a();
            Thread.Sleep(900);
            var after = Capture(x, y);
            var d = Diff(before, after);
            Console.WriteLine($"  {name,-32} diff={d,4}  {(d > 3 ? "★SCROLLED" : "no change")}");
        }

        Console.WriteLine("== 方法对照 ==");
        Try("SendInput PageDown", () => {
            var s = Marshal.SizeOf<INPUT>();
            var d = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_NEXT } };
            var u = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_NEXT, dwFlags = KEYEVENTF_KEYUP } };
            SendInput(1, [d], s); Thread.Sleep(20); SendInput(1, [u], s);
        });
        Try("SendInput wheel -360", () => {
            var s = Marshal.SizeOf<INPUT>();
            var i = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)-360) } };
            SendInput(1, [i], s);
        });
        Try("PostMessage VScroll PAGEDOWN", () => PostMessage(child, WM_VSCROLL, (IntPtr)SB_PAGEDOWN, IntPtr.Zero));
        Try("PostMessage VScroll child->root", () => PostMessage(root, WM_VSCROLL, (IntPtr)SB_PAGEDOWN, IntPtr.Zero));
        Try("PostMessage WM_MOUSEWHEEL", () => {
            long wp = unchecked((long)((ulong)(ushort)(short)-120 << 16));
            var w = (IntPtr)wp;
            var l = (IntPtr)(int)((ushort)x | ((uint)(ushort)y << 16));
            PostMessage(child, WM_MOUSEWHEEL, w, l);
        });
        Try("PostMessage WM_KEYDOWN PgDn", () => { PostMessage(child, WM_KEYDOWN, (IntPtr)VK_NEXT_U, IntPtr.Zero); PostMessage(child, WM_KEYUP, (IntPtr)VK_NEXT_U, IntPtr.Zero); });
        Console.WriteLine("done");
    }
}
