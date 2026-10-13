using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Plugins.Clipboard.Services;

/// <summary>把历史条目转换为剪贴板数据对象 / 写回剪贴板（带占用重试）。</summary>
public static class ClipboardWriter
{
    /// <summary>构建包含条目所有可用格式的 DataObject（复制与拖拽共用）。</summary>
    public static DataObject BuildDataObject(ClipItem item)
    {
        var data = new DataObject();

        if (item.Text is not null) data.SetText(item.Text);
        if (item.Html is not null) data.SetData(DataFormats.Html, item.Html);
        if (item.Rtf is not null) data.SetData(DataFormats.Rtf, item.Rtf);
        if (item.ImagePng is { Length: > 0 } png)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(png);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            data.SetImage(bi);
        }
        if (item.Files is { Count: > 0 } files)
            data.SetData(DataFormats.FileDrop, files.ToArray());

        return data;
    }

    /// <summary>把历史条目原样写回剪贴板（尽量保留各格式）。</summary>
    public static void SetClipboard(ClipItem item)
        => SetDataObjectSafe(BuildDataObject(item));

    /// <summary>仅以纯文本写入剪贴板（用于"复制为纯文本"/"粘贴为纯文本"）。</summary>
    public static void SetClipboardText(string text)
        => SetDataObjectSafe(CreateTextDataObject(text));

    /// <summary>在后台 STA 线程异步写剪贴板（写完后回调），避免 UI 线程因剪贴板争用而卡顿。
    /// 所有写请求经单一专用线程串行执行，避免连点时多个线程互相抢剪贴板。</summary>
    public static void SetClipboardAsync(ClipItem item, Action? onComplete = null)
        => Enqueue(() =>
        {
            SetDataObjectSafe(BuildDataObject(item));
            onComplete?.Invoke();
        });

    /// <summary>在后台 STA 线程异步以纯文本写剪贴板。</summary>
    public static void SetClipboardTextAsync(string text, Action? onComplete = null)
        => Enqueue(() =>
        {
            SetDataObjectSafe(CreateTextDataObject(text));
            onComplete?.Invoke();
        });

    private static DataObject CreateTextDataObject(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        return data;
    }

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
                // 全限定：本插件命名空间里也有 "Clipboard"，会遮蔽 System.Windows.Clipboard
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
