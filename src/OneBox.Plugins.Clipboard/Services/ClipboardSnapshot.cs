using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Plugins.Clipboard.Services;

/// <summary>读取当前剪贴板并归一化为 ClipItem。</summary>
public static class ClipboardSnapshot
{
    public static ClipItem? Capture()
    {
        IDataObject data;
        try
        {
            // 全限定：本插件命名空间里也有 "Clipboard"，会遮蔽 System.Windows.Clipboard
            data = System.Windows.Clipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            return null;
        }

        if (data is null) return null;

        string? text = GetString(data, DataFormats.UnicodeText);
        string? html = GetString(data, DataFormats.Html);
        string? rtf = GetString(data, DataFormats.Rtf);
        string[]? files = data.GetDataPresent(DataFormats.FileDrop)
            ? data.GetData(DataFormats.FileDrop) as string[]
            : null;

        byte[]? imagePng = null;
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            imagePng = EncodePng(bmp);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrEmpty(html) && string.IsNullOrEmpty(rtf)
            && imagePng is null && (files is null || files.Length == 0))
            return null;

        // 主类型优先级：文本 > HTML > RTF > 图片 > 文件
        ClipKind kind;
        string preview;
        if (!string.IsNullOrEmpty(text))
        {
            kind = ClipKind.Text;
            preview = TextPreview(text);
        }
        else if (!string.IsNullOrEmpty(html))
        {
            kind = ClipKind.Html;
            preview = TextPreview(ClipboardText.HtmlToPlainText(ClipboardHtml.ExtractFragment(html)));
            if (string.IsNullOrWhiteSpace(preview)) preview = "[HTML]";
        }
        else if (!string.IsNullOrEmpty(rtf))
        {
            kind = ClipKind.Rtf;
            preview = "[RTF 富文本]";
        }
        else if (imagePng is not null)
        {
            kind = ClipKind.Image;
            preview = "[图片]";
        }
        else
        {
            kind = ClipKind.Files;
            preview = string.Join("; ", files!);
        }

        return ClipItem.Create(kind, text, html, rtf, imagePng, files, preview);
    }

    private static string? GetString(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format) ? data.GetData(format) as string : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static string TextPreview(string text)
    {
        var t = ClipboardText.NormalizeNewlines(text).Trim();
        return t.Length <= 200 ? t : t[..200] + "…";
    }

    private static byte[]? EncodePng(BitmapSource bmp)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
