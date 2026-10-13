using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>图片编码 / 解码 / 保存。</summary>
public static class ImageCodec
{
    /// <summary>把 BitmapSource 编码为 PNG 字节。</summary>
    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(EnsureBgra(source)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>从 PNG（或其他 WPF 可解码格式）字节还原 BitmapSource。</summary>
    public static BitmapSource? Decode(byte[] data)
    {
        if (data.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按设置保存到磁盘；返回实际写入的路径。</summary>
    public static string Save(BitmapSource source, ScreenshotSettings settings, DateTime now, ISet<string>? reserved = null)
    {
        var directory = settings.ResolveSaveDirectory();
        Directory.CreateDirectory(directory);

        var baseName = FileNameBuilder.Build(settings.FileNameTemplate, now);
        var extension = FileNameBuilder.ExtensionFor(settings.Format);
        var path = FileNameBuilder.BuildUniquePath(directory, baseName, extension, reserved);

        SaveTo(source, path, settings.Format, settings.JpegQuality);
        return path;
    }

    /// <summary>按指定格式写入文件（按扩展名推断格式）。</summary>
    public static void SaveTo(BitmapSource source, string path, ShotImageFormat format, int jpegQuality)
    {
        BitmapEncoder encoder = format == ShotImageFormat.Jpeg
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) }
            : new PngBitmapEncoder();

        var frame = BitmapFrame.Create(PrepareForFormat(source, format));
        encoder.Frames.Add(frame);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>JPEG 不支持透明通道，先合成到白底再编码，避免透明区域变黑。</summary>
    private static BitmapSource PrepareForFormat(BitmapSource source, ShotImageFormat format)
    {
        var normalized = EnsureBgra(source);
        if (format != ShotImageFormat.Jpeg) return normalized;
        if (normalized.Format != PixelFormats.Bgra32 && normalized.Format != PixelFormats.Bgr32)
            return normalized;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, normalized.PixelWidth, normalized.PixelHeight));
            dc.DrawImage(normalized, new Rect(0, 0, normalized.PixelWidth, normalized.PixelHeight));
        }
        var target = new RenderTargetBitmap(
            normalized.PixelWidth, normalized.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>统一转成 Bgra32（Bgra32 的每一行都是存储格式一致的 4 字节，便于 OCR 与拼接）。</summary>
    public static BitmapSource EnsureBgra(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>把 BitmapSource 拷成紧凑的 BGRA 字节数组（每行 width*4 字节）。</summary>
    public static byte[] ToBgraBytes(BitmapSource source, out int stride, out int width, out int height)
    {
        var bgra = EnsureBgra(source);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        stride = width * 4;

        var buffer = new byte[(long)stride * height];
        bgra.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>由紧凑的 BGRA 缓冲构造 BitmapSource。</summary>
    public static BitmapSource FromBgraBytes(byte[] buffer, int width, int height)
    {
        var stride = width * 4;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        source.Freeze();
        return source;
    }

    /// <summary>
    /// 生成一份与调用线程脱钩的冻结副本。
    /// 剪贴板等来源的 BitmapSource 与创建它的线程绑定，一旦后续代码切到线程池
    /// （例如 await 之后走了 ConfigureAwait(false)）再访问就会抛
    /// “调用线程无法访问此对象，因为另一个线程拥有该对象”。
    /// 因此在切线程之前先在本线程把像素拷出来。
    /// </summary>
    public static BitmapSource Snapshot(BitmapSource source)
    {
        if (source.IsFrozen && source.Format == PixelFormats.Bgra32) return source;
        var buffer = ToBgraBytes(source, out _, out var width, out var height);
        return FromBgraBytes(buffer, width, height);
    }

    /// <summary>等比缩放到不超过给定边长（用于贴图小窗预览）。</summary>
    public static BitmapSource ScaleToFit(BitmapSource source, double maxWidth, double maxHeight)
    {
        var scale = Math.Min(maxWidth / source.PixelWidth, maxHeight / source.PixelHeight);
        if (scale >= 1 || scale <= 0) return source;

        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
