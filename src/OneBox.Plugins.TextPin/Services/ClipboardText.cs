using System.Runtime.InteropServices;
using System.Threading;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>
/// 纯文本剪贴板读取。
/// 只在用户显式操作（按「抓取选中文字」热键）时读一次，**不注册任何剪贴板监听器**：
/// 插件不感知、也不记录系统里其他地方的复制内容。
///
/// 写入方向刻意不实现：把文本放进剪贴板就是文本框里的 Ctrl+C，插件不再重复造一套。
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
}
