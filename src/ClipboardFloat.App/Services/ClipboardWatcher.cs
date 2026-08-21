using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ClipboardFloat.Core;

namespace ClipboardFloat.App.Services;

/// <summary>基于 AddClipboardFormatListener 的事件驱动剪贴板监听（无轮询）。</summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromMilliseconds(500);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private DateTime _lastSelfWrite = DateTime.MinValue;

    /// <summary>捕获到新剪贴板内容（UI 线程触发）。</summary>
    public event Action<ClipItem>? ItemCaptured;

    public ClipboardWatcher(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    /// <summary>应用自身写入剪贴板后调用，抑制随后的事件回灌（避免把复制回去的内容当新条目）。</summary>
    public void MarkSelfWrite() => _lastSelfWrite = DateTime.UtcNow;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            if (DateTime.UtcNow - _lastSelfWrite < SelfWriteWindow)
                return IntPtr.Zero;

            try
            {
                var item = ClipboardSnapshot.Capture();
                if (item is not null) ItemCaptured?.Invoke(item);
            }
            catch (Exception ex)
            {
                AppLog.Write($"剪贴板捕获失败: {ex.Message}");
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_hwnd);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
