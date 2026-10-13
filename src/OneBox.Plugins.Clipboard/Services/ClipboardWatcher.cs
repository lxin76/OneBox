using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Plugins.Clipboard.Services;

/// <summary>基于 AddClipboardFormatListener 的事件驱动剪贴板监听（无轮询）。</summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromMilliseconds(500);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private DateTime _lastSelfWrite = DateTime.MinValue;
    private bool _disposed;

    /// <summary>捕获到新剪贴板内容（UI 线程触发）。</summary>
    public event Action<ClipItem>? ItemCaptured;

    /// <summary>当前是否已注册剪贴板监听器（关闭时完全不监听）。</summary>
    public bool IsListening { get; private set; }

    public ClipboardWatcher(Window window, bool startListening = false)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
        if (startListening) Start();
    }

    /// <summary>开始监听剪贴板更新（重复调用安全）。</summary>
    public void Start()
    {
        if (_disposed || IsListening) return;
        if (AddClipboardFormatListener(_hwnd))
        {
            IsListening = true;
            AppLog.Write("剪贴板监听已开启");
        }
        else
        {
            AppLog.Write("剪贴板监听开启失败: AddClipboardFormatListener 返回 false");
        }
    }

    /// <summary>暂停监听：注销监听器，系统不再投递 WM_CLIPBOARDUPDATE。</summary>
    public void Stop()
    {
        if (!IsListening) return;
        RemoveClipboardFormatListener(_hwnd);
        IsListening = false;
        AppLog.Write("剪贴板监听已暂停");
    }

    /// <summary>应用自身写入剪贴板后调用，抑制随后的事件回灌（避免把复制回去的内容当新条目）。</summary>
    public void MarkSelfWrite() => _lastSelfWrite = DateTime.UtcNow;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            // 已暂停监听：即使收到残留消息也不捕获
            if (!IsListening) return IntPtr.Zero;

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
        Stop();
        _disposed = true;
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
