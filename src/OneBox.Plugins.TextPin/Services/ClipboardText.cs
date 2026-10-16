using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using OneBox.Plugins.TextPin.Core;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>
/// 纯文本剪贴板读写。
/// 只在用户显式操作（点按钮 / 按热键）时读写剪贴板，**不注册任何剪贴板监听器**：
/// 插件不感知、也不记录系统里其他地方的复制内容。
/// </summary>
public static class ClipboardText
{
    /// <summary>读取当前剪贴板的纯文本；没有文本内容时返回 null。</summary>
    public static string? TryRead()
    {
        // WPF 的 Clipboard 是 OLE 调用，必须在 STA 线程上执行。
        // 本方法会被热键回调用到，而那是普通的后台线程（MTA），
        // 直接调用会抛 ThreadStateException 并让插件进程整体退出——
        // 用户看到的就是"按了热键没反应"。这里在需要时切到专用 STA 线程。
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return TryReadCore();

        string? result = null;
        Exception? failure = null;
        using (var done = new ManualResetEventSlim(false))
        {
            var thread = new Thread(() =>
            {
                try { result = TryReadCore(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            })
            {
                IsBackground = true,
                Name = "ClipboardReader",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // 剪贴板读取本身是毫秒级的；给足余量但不无限等待，
            // 避免调用方（热键回调线程）被卡死。
            if (!done.Wait(TimeSpan.FromSeconds(3)))
            {
                AppLog.Write("读取剪贴板超时（STA 线程未在 3 秒内返回）");
                return null;
            }
        }

        if (failure is not null)
        {
            AppLog.Write($"读取剪贴板异常: {failure.Message}");
            return null;
        }
        return result;
    }

    /// <summary>真正的读取逻辑，必须在 STA 线程上调用。</summary>
    private static string? TryReadCore()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : null;
        }
        catch (ExternalException)
        {
            // 剪贴板被其他程序占用：这次读不到就当作没有内容，不打断用户
            AppLog.Write("读取剪贴板失败（被占用）");
            return null;
        }
    }

    /// <summary>写入纯文本（同步，调用方负责线程）。</summary>
    public static void Write(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        SetDataObjectSafe(data);
    }

    /// <summary>在后台 STA 线程异步写剪贴板（写完后回调），避免 UI 线程因剪贴板争用而卡顿。
    /// 所有写请求经单一专用线程串行执行，避免连点时多个线程互相抢剪贴板。</summary>
    public static void WriteAsync(string text, Action? onComplete = null)
        => Enqueue(() =>
        {
            Write(text);
            onComplete?.Invoke();
        });

    // ---- 专用 STA 工作线程 + 串行队列 ----

    private static readonly object QueueLock = new();
    private static readonly Queue<Action> WorkQueue = new();
    private static Thread? _worker;

    private static void Enqueue(Action work)
    {
        lock (QueueLock)
        {
            WorkQueue.Enqueue(work);
            if (_worker is null || !_worker.IsAlive)
            {
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "ClipboardWriter",
                };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
            Monitor.Pulse(QueueLock);
        }
    }

    private static void WorkerLoop()
    {
        while (true)
        {
            Action? work;
            lock (QueueLock)
            {
                while (WorkQueue.Count == 0) Monitor.Wait(QueueLock);
                work = WorkQueue.Dequeue();
            }

            try
            {
                work();
            }
            catch (Exception ex)
            {
                AppLog.Write($"后台剪贴板操作异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 写入剪贴板；连续快速写入时剪贴板可能被其他程序（如剪贴板监听工具）短暂占用，
    /// 这里重试几次避免瞬时 COMException 导致崩溃。
    /// </summary>
    private static void SetDataObjectSafe(DataObject data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // 全限定：本插件命名空间里也有 "ClipboardText"，避免歧义
                System.Windows.Clipboard.SetDataObject(data, true);
                return;
            }
            catch (ExternalException)
            {
                if (attempt >= 2)
                {
                    AppLog.Write("剪贴板写入失败（被占用，重试后仍失败）");
                    return;
                }
                Thread.Sleep(25);
            }
        }
    }
}
