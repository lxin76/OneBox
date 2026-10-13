using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using OneBox.Plugins.Screenshot.Core;
using WinRTImg = Windows.Graphics.Imaging;
using WinRTStreams = Windows.Storage.Streams;
// 本插件的 Windows 命名空间会遮蔽 WinRT 的 Windows，必须用 global:: 限定
using WinOcr = global::Windows.Media.Ocr;
using WinLanguage = global::Windows.Globalization.Language;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>一次识别结果。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="Text">整理后的整篇文本。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Lines">引擎给出的行文本（仅作诊断，版面请用 <paramref name="Layout"/>）。</param>
/// <param name="Layout">
/// 词级版面，坐标已还原到传入位图的像素坐标系。
/// 用于在图上划选文本；识别失败或没有文字时为 null。
/// </param>
public sealed record OcrOutcome(
    bool Success,
    string Text,
    string? Error,
    IReadOnlyList<string> Lines,
    OcrLayout? Layout = null)
{
    public static OcrOutcome Fail(string error) => new(false, "", error, []);
}

/// <summary>
/// 文本识别。使用 Windows 自带的 OCR 引擎（Windows.Media.Ocr）：
/// 无需联网、无需额外模型文件，识别语言取决于系统已安装的 OCR 语言包。
/// </summary>
public static class OcrService
{
    /// <summary>引擎支持的图片边长上限（超过需要先缩放）。</summary>
    public static int MaxImageDimension
    {
        get
        {
            try { return (int)WinOcr.OcrEngine.MaxImageDimension; }
            catch { return 10000; }
        }
    }

    /// <summary>当前系统可用的识别语言标签。</summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        try
        {
            return WinOcr.OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>识别位图里的文字。</summary>
    public static async Task<OcrOutcome> RecognizeAsync(
        BitmapSource source, string? language = null, bool stripCjkSpaces = true)
    {
        try
        {
            var engine = CreateEngine(language);
            if (engine is null)
            {
                var available = AvailableLanguages();
                var hint = available.Count > 0
                    ? $"可用语言：{string.Join(", ", available)}"
                    : "系统未安装任何 OCR 语言包（可在「设置 → 时间和语言 → 语言」里添加语言的「光学字符识别」功能）";
                return OcrOutcome.Fail($"找不到可用的识别语言。{hint}");
            }

            var scaled = FitWithinLimit(source);
            var png = ImageCodec.EncodePng(scaled);
            using var software = await DecodeToSoftwareBitmapAsync(png);

            var result = await engine.RecognizeAsync(software);
            var lines = result.Lines.Select(l => l.Text).ToList();
            var text = OcrText.Normalize(result.Text, stripCjkSpaces);

            // 词框是在（可能被缩小过的）scaled 上算出来的，要还原回原图坐标，
            // 否则贴图缩放后高亮会整体偏移。
            var scaleX = source.PixelWidth / (double)Math.Max(1, scaled.PixelWidth);
            var scaleY = source.PixelHeight / (double)Math.Max(1, scaled.PixelHeight);
            var words = ExtractWords(result, scaleX, scaleY);
            var layout = words.Count > 0 ? OcrLayout.Build(words) : null;

            return new OcrOutcome(true, text, null, lines, layout);
        }
        catch (Exception ex)
        {
            return OcrOutcome.Fail($"识别失败：{ex.Message}");
        }
    }

    /// <summary>识别 PNG 字节里的文字。</summary>
    public static Task<OcrOutcome> RecognizeAsync(byte[] png, string? language = null, bool stripCjkSpaces = true)
    {
        var source = ImageCodec.Decode(png);
        return source is null
            ? Task.FromResult(OcrOutcome.Fail("图片无法解码。"))
            : RecognizeAsync(source, language, stripCjkSpaces);
    }

    /// <summary>
    /// 把引擎输出的词连同像素框取出来，并按给定比例还原到原图坐标系。
    /// 中文 OCR 的"词"常常是单个字，直接画框会是断开的方格；
    /// 上游会按几何聚成行再合并高亮，这里只负责忠实取出。
    /// </summary>
    private static List<OcrWord> ExtractWords(WinOcr.OcrResult result, double scaleX, double scaleY)
    {
        var words = new List<OcrWord>();

        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                if (string.IsNullOrEmpty(word.Text)) continue;

                var rect = word.BoundingRect;
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                words.Add(new OcrWord(word.Text, new OcrRect(
                    rect.X * scaleX,
                    rect.Y * scaleY,
                    rect.Width * scaleX,
                    rect.Height * scaleY)));
            }
        }

        return words;
    }

    private static WinOcr.OcrEngine? CreateEngine(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            try
            {
                var lang = new WinLanguage(language);
                if (WinOcr.OcrEngine.IsLanguageSupported(lang))
                    return WinOcr.OcrEngine.TryCreateFromLanguage(lang);
            }
            catch
            {
                // 语言标签非法时回退到系统首选语言
            }
        }

        return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>OCR 引擎对图片边长有上限，超出时等比缩小。</summary>
    private static BitmapSource FitWithinLimit(BitmapSource source)
    {
        var limit = MaxImageDimension;
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= limit) return ImageCodec.EnsureBgra(source);

        var scale = (double)limit / longest;
        var target = new System.Windows.Media.Imaging.TransformedBitmap(
            source, new System.Windows.Media.ScaleTransform(scale, scale));
        target.Freeze();
        return ImageCodec.EnsureBgra(target);
    }

    private static async Task<WinRTImg.SoftwareBitmap> DecodeToSoftwareBitmapAsync(byte[] png)
    {
        var ras = new WinRTStreams.InMemoryRandomAccessStream();
        await ras.WriteAsync(png.AsBuffer());
        ras.Seek(0);

        var decoder = await WinRTImg.BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(
            WinRTImg.BitmapPixelFormat.Bgra8, WinRTImg.BitmapAlphaMode.Premultiplied);
    }
}
