using System.Runtime.InteropServices;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>一次抓取的结果。</summary>
/// <param name="Success">是否成功取到选中文字。</param>
/// <param name="Text">取到的文本（成功时非空）。</param>
/// <param name="Error">失败原因（成功时为 null）。</param>
public readonly record struct CaptureResult(bool Success, string? Text, string? Error)
{
    public static CaptureResult Ok(string text) => new(true, text, null);
    public static CaptureResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 抓取任意窗口里选中的文字：模拟 Ctrl+C，然后等剪贴板真的变化，再把文本取出来。
///
/// 为什么不用剪贴板监听器：本插件刻意不注册 <c>AddClipboardFormatListener</c>，
/// 常驻监听会感知并记录系统里所有复制动作，这正是本次改造要避免的。
/// 这里只在一次显式操作期间轮询剪贴板序号（<c>GetClipboardSequenceNumber</c>），
/// 序号没变就说明复制没发生，操作结束即停止，不留下任何后台监听。
///
/// 关键点：Ctrl+C 之前先记下剪贴板序号，只在序号**变化**时才认这次复制，
/// 否则会把上一次留在剪贴板里的内容误当成"选中文字"。
/// </summary>
public static class SelectionCapture
{
    /// <summary>等待剪贴板序号变化的总时长。太短会漏掉慢应用，太长会让用户觉得卡。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1200);

    /// <summary>轮询间隔。20ms 在响应速度与 CPU 占用之间取平衡。</summary>
    public const int PollIntervalMs = 20;

    /// <summary>
    /// 抓取当前前台窗口里选中的文字。必须在后台线程调用（内部有等待与按键注入）。
    /// </summary>
    /// <param name="restoreTarget">
    /// 若置顶窗当前正被激活（用户刚点过文本框），先把前台还给这个窗口再发 Ctrl+C，
    /// 否则复制到的是置顶窗自己选中的内容。IntPtr.Zero 表示无需归还。
    /// </param>
    public static CaptureResult CaptureSelectedText(IntPtr restoreTarget = default)
    {
        if (restoreTarget != IntPtr.Zero)
        {
            TopmostService.ActivateWindow(restoreTarget);
            Thread.Sleep(80); // 等前台切换生效，太短会复制到错误的窗口
        }

        var before = GetClipboardSequenceNumber();

        // 注入 Ctrl+C 前先确保热键的修饰键已松开，否则会变成别的组合
        PasteService.CopyByKey();

        return WaitForClipboardText(
            before,
            GetClipboardSequenceNumber,
            ClipboardText.TryRead,
            DefaultTimeout,
            Thread.Sleep);
    }

    /// <summary>
    /// 等待剪贴板序号变化并取出文本。抽出来是为了能脱离真实按键注入单测：
    /// 判定逻辑（"序号变了才算复制成功"、超时、非文本内容）都集中在这里。
    /// </summary>
    /// <param name="sequenceBefore">注入 Ctrl+C 之前的剪贴板序号。</param>
    /// <param name="readSequence">读取当前剪贴板序号。</param>
    /// <param name="readText">读取剪贴板文本；无文本时返回 null。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="sleep">等待一个轮询间隔。</param>
    internal static CaptureResult WaitForClipboardText(
        uint sequenceBefore,
        Func<uint> readSequence,
        Func<string?> readText,
        TimeSpan timeout,
        Action<int> sleep)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            sleep(PollIntervalMs);

            // 序号没变 = 复制还没发生（或目标程序忽略了 Ctrl+C），继续等
            if (readSequence() == sequenceBefore) continue;

            var text = readText();
            if (string.IsNullOrEmpty(text))
            {
                // 剪贴板变了但不是文本（例如复制了图片/文件）：明确告知，而不是静默失败
                return CaptureResult.Fail("选中的内容不是纯文本");
            }
            return CaptureResult.Ok(text);
        }

        return CaptureResult.Fail("没有取到选中文字（可能未选中内容，或目标程序不支持 Ctrl+C）");
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
