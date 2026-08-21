using System.IO;
using System.Windows.Media.Imaging;
using ClipboardFloat.Core;

namespace ClipboardFloat.App.ViewModels;

/// <summary>历史条目的展示模型。</summary>
public sealed class ClipItemViewModel
{
    public ClipItem Item { get; }

    public ClipItemViewModel(ClipItem item)
    {
        Item = item;
        Preview = item.Preview;
        KindIcon = item.Kind switch
        {
            ClipKind.Text => "T",
            ClipKind.Html => "H",
            ClipKind.Rtf => "R",
            ClipKind.Image => "🖼",
            ClipKind.Files => "📁",
            _ => "·",
        };
        KindLabel = item.Kind switch
        {
            ClipKind.Text => "文本",
            ClipKind.Html => "HTML",
            ClipKind.Rtf => "富文本",
            ClipKind.Image => "图片",
            ClipKind.Files => "文件",
            _ => "未知",
        };
        TimeText = FormatTime(item.CapturedAt);
        Meta = $"{KindLabel} · {TimeText}";
        HasThumbnail = item.ImagePng is { Length: > 0 };
        Thumbnail = HasThumbnail ? DecodeThumbnail(item.ImagePng!) : null;
    }

    public string Preview { get; }
    public string KindIcon { get; }
    public string KindLabel { get; }
    public string TimeText { get; }
    public string Meta { get; }
    public bool HasThumbnail { get; }
    public BitmapImage? Thumbnail { get; }

    private static BitmapImage? DecodeThumbnail(byte[] png)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(png);
            bi.DecodePixelWidth = 48;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTime(DateTime time)
    {
        var age = DateTime.Now - time;
        if (age < TimeSpan.FromMinutes(1)) return "刚刚";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} 分钟前";
        if (time.Date == DateTime.Today) return time.ToString("HH:mm");
        return time.ToString("MM-dd HH:mm");
    }
}
