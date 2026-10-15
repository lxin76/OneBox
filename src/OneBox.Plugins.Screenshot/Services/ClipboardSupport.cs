using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>把截图写入剪贴板（带占用重试）。</summary>
public static class ClipboardSupport
{
    /// <summary>把位图以图片格式写入系统剪贴板。</summary>
    public static bool CopyImage(BitmapSource source)
    {
        try
        {
            var data = new DataObject();
            data.SetImage(source);
            return SetDataObjectSafe(data);
        }
        catch (Exception ex)
        {
            AppLog.Write($"写入剪贴板失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>把纯文本写入剪贴板。</summary>
    public static bool CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var data = new DataObject();
            data.SetText(text);
            return SetDataObjectSafe(data);
        }
        catch (Exception ex)
        {
            AppLog.Write($"写入剪贴板失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>读取剪贴板里的图片；没有图片时返回 null。</summary>
    public static BitmapSource? TryGetImage()
    {
        try
        {
            return System.Windows.Clipboard.ContainsImage()
                ? System.Windows.Clipboard.GetImage()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取剪贴板文本；没有文本时返回空串。</summary>
    public static string TryGetText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 剪贴板可能被其他程序（尤其是剪贴板历史工具）短暂占用，
    /// 这里重试几次，避免瞬时 COMException 直接冒泡。
    /// </summary>
    private static bool SetDataObjectSafe(DataObject data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException)
            {
                if (attempt >= 2)
                {
                    AppLog.Write("剪贴板写入失败（被占用，重试后仍失败）");
                    return false;
                }
                // 不能 Thread.Sleep：调用方常在 UI 线程，干等 30ms×3 会让窗口完全无响应。
                // 用消息泵等待 30ms：等待期间窗口仍能响应移动/关闭。
                PumpWait(TimeSpan.FromMilliseconds(30));
            }
        }
    }

    /// <summary>在等待剪贴板重试间隙泵消息，保持 UI 可响应（仅 UI 线程有效）。</summary>
    private static void PumpWait(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            new Action(() =>
            {
                try { Thread.Sleep(duration); } finally { frame.Continue = false; }
            }), System.Windows.Threading.DispatcherPriority.Background);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
