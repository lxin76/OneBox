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
/// <param name="Text">
/// 整理后的整篇文本。按图片版式输出：保留换行，行距明显大的地方留空行分段。
/// 注意不要改用引擎的 OcrResult.Text —— 实测它把各行用空格拼成一行，没有换行。
/// </param>
/// <param name="Error">失败原因。</param>
/// <param name="Lines">引擎给出的行文本（仅作诊断，版面请用 <paramref name="Layout"/>）。</param>
/// <param name="Layout">
/// 词级版面，坐标已还原到传入位图的像素坐标系。
/// 用于在图上划选文本；识别失败或没有文字时为 null。
/// </param>
/// <param name="EnhancementNote">放大重识别的说明；没有放大时为 null。</param>
public sealed record OcrOutcome(
    bool Success,
    string Text,
    string? Error,
    IReadOnlyList<string> Lines,
    OcrLayout? Layout = null,
    string? EnhancementNote = null)
{
    public static OcrOutcome Fail(string error) => new(false, "", error, []);
}

/// <summary>
/// 文本识别。使用 Windows 自带的 OCR 引擎（Windows.Media.Ocr）：
/// 无需联网、无需额外模型文件，识别语言取决于系统已安装的 OCR 语言包。
///
/// 准确率优化（都基于实测）：
/// 1. 文字很小（行高 &lt; 20px）时，把文字区域按 2-3 倍双线性放大后重识别一次。
///    同一张 11px 中文截图，原图相似度约 78%，2 倍放大后约 86%。
///    插值方式很关键：最近邻会把"订单编号"识别成"订解号"，双线性/双三次则明显更好。
/// 2. 只采纳"识别出的字数确实更多"的放大结果。引擎偶尔会在放大图上漏行，
///    宁可保留原图结果，也不要为了看起来更清晰而丢字。
/// 3. 段落靠行距判断，不靠引擎给的行划分（引擎的行划分在文档渲染图上并不稳）。
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
    /// <param name="enhanceSmallText">文字偏小时是否放大后重识别一次。</param>
    /// <remarks>
    /// 双引擎策略：PaddleOCR 模型齐全时优先走 <see cref="PaddleOcrService"/>（质量更高）；
    /// 模型缺失或识别异常时回退到系统引擎 Windows.Media.Ocr（零依赖，始终可用）。
    /// 系统引擎路径保持原样（含小字放大增强）。
    /// </remarks>
    public static async Task<OcrOutcome> RecognizeAsync(
        BitmapSource source,
        string? language = null,
        bool stripCjkSpaces = true,
        bool enhanceSmallText = true)
    {
        // PaddleOCR 引擎优先：模型齐全时质量明显好于系统引擎（小字错字显著减少）。
        if (PaddleOcrService.IsAvailable)
        {
            var paddle = await PaddleOcrService.RecognizeAsync(source, stripCjkSpaces, enhanceSmallText)
                .ConfigureAwait(false);
            if (paddle.Success)
            {
                return paddle;
            }
            AppLog.Write($"PaddleOCR 识别未成功（{paddle.Error}），回退系统引擎");
        }

        return await RecognizeWithSystemEngineAsync(source, language, stripCjkSpaces, enhanceSmallText)
            .ConfigureAwait(false);
    }

    /// <summary>系统引擎（Windows.Media.Ocr）识别路径，原有逻辑不变。</summary>
    private static async Task<OcrOutcome> RecognizeWithSystemEngineAsync(
        BitmapSource source,
        string? language,
        bool stripCjkSpaces,
        bool enhanceSmallText)
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

            // 需要放大时，先把像素固化成一张独立的位图：下面的 await 会把后续代码
            // 切到线程池，而来源位图（尤其是解码得到的 BitmapFrame）带线程亲和性，
            // 跨线程读它的像素会抛"调用线程无法访问此对象"。固化后即可安全跨线程。
            // 像素拷贝是大图上的 CPU 密集活，放线程池做，别把调用方（通常是 UI 线程）
            // 卡住几百毫秒——用户此时可能正在关窗口。
            var bitmap = enhanceSmallText
                ? await Task.Run(() => ToIndependentBitmap(source)).ConfigureAwait(false)
                : source;

            // 第一遍：原图识别。这样拿到的是最真实的文字框（缩放会改变框坐标）。
            var first = await RunAsync(engine, bitmap);
            if (first.Error is not null) return OcrOutcome.Fail(first.Error);

            var lines = first.Lines;
            var layout = first.Words.Count > 0 ? OcrLayout.Build(first.Words) : null;
            string? enhancementNote = null;

            // 第二遍：文字偏小时放大重识别。只有确实识别出更多文字才采纳。
            if (enhanceSmallText)
            {
                var plan = OcrPreprocess.Plan(
                    layout, bitmap.PixelWidth, bitmap.PixelHeight,
                    OcrPreprocess.DefaultMaxScaledPixels, MaxImageDimension);

                if (plan.IsUseful && plan.Region is { } region)
                {
                    var enhanced = await RunEnhancedAsync(engine, bitmap, region, plan.Factor);
                    if (enhanced.Error is null)
                    {
                        var enhancedLayout = enhanced.Words.Count > 0 ? OcrLayout.Build(enhanced.Words) : null;
                        if (enhancedLayout is not null &&
                            OcrPreprocess.ShouldAcceptEnhanced(
                                layout?.Lines.Count ?? 0, layout?.Text.Length ?? 0,
                                enhancedLayout.Lines.Count, enhancedLayout.Text.Length))
                        {
                            layout = enhancedLayout;
                            lines = enhanced.Lines;
                            enhancementNote = plan.Reason;
                        }
                    }
                }
            }

            // 文本一律由版面（几何聚类后的行）生成：引擎的 OcrResult.Text 没有换行。
            var text = layout is not null
                ? OcrText.NormalizeDocument(layout.DocumentText(), stripCjkSpaces)
                : OcrText.Normalize(string.Join("\n", lines), stripCjkSpaces);

            return new OcrOutcome(true, text, null, lines, layout, enhancementNote);
        }
        catch (Exception ex)
        {
            return OcrOutcome.Fail($"识别失败：{ex.Message}");
        }
    }

    /// <summary>识别 PNG 字节里的文字。</summary>
    public static Task<OcrOutcome> RecognizeAsync(
        byte[] png, string? language = null, bool stripCjkSpaces = true, bool enhanceSmallText = true)
    {
        var source = ImageCodec.Decode(png);
        return source is null
            ? Task.FromResult(OcrOutcome.Fail("图片无法解码。"))
            : RecognizeAsync(source, language, stripCjkSpaces, enhanceSmallText);
    }

    /// <summary>
    /// 把任意来源的位图拷成一张独立的冻结位图（自带像素，不再依赖解码器或创建线程）。
    /// </summary>
    private static BitmapSource ToIndependentBitmap(BitmapSource source)
    {
        if (source.IsFrozen && source.Format == System.Windows.Media.PixelFormats.Bgra32)
            return source;
        var pixels = ImageCodec.ToBgraBytes(source, out _, out var width, out var height);
        return ImageCodec.FromBgraBytes(pixels, width, height);
    }

    /// <summary>引擎一轮识别的原始产物：行文本 + 映射到该位图坐标系的词。</summary>
    private sealed record EnginePass(IReadOnlyList<string> Lines, IReadOnlyList<OcrWord> Words, string? Error);

    /// <summary>对整张位图跑一轮识别。</summary>
    private static async Task<EnginePass> RunAsync(WinOcr.OcrEngine engine, BitmapSource bitmap)
    {
        try
        {
            // 缩放 + PNG 编码是整图级 CPU 密集活，放线程池做，避免卡住调用方 UI 线程。
            var (scaled, png) = await Task.Run(() =>
            {
                var s = FitWithinLimit(bitmap);
                return (s, ImageCodec.EncodePng(s));
            }).ConfigureAwait(false);
            using var software = await DecodeToSoftwareBitmapAsync(png).ConfigureAwait(false);

            var result = await engine.RecognizeAsync(software);
            var lines = result.Lines.Select(l => l.Text).ToList();

            // 词框是在（可能被缩小过的）scaled 上算出来的，要还原回原图坐标，
            // 否则贴图缩放后高亮会整体偏移。
            var scaleX = bitmap.PixelWidth / (double)Math.Max(1, scaled.PixelWidth);
            var scaleY = bitmap.PixelHeight / (double)Math.Max(1, scaled.PixelHeight);
            return new EnginePass(lines, ExtractWords(result, scaleX, scaleY), null);
        }
        catch (Exception ex)
        {
            return new EnginePass([], [], $"识别失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 放大文字区域后重识别，并把词框还原回原图坐标系。
    /// 裁剪与放大走 <see cref="OcrImageScaler"/> 的纯像素运算，不用 WPF 的
    /// CroppedBitmap/TransformedBitmap —— 后者带线程亲和性，在 await 之后
    /// 的线程池线程上使用会抛"调用线程无法访问此对象"（实测）。
    /// </summary>
    private static async Task<EnginePass> RunEnhancedAsync(
        WinOcr.OcrEngine engine, BitmapSource source, OcrRect region, int factor)
    {
        try
        {
            // 取像素 + 双线性放大是逐像素四通道插值，3× 放大后可达上千万像素，
            // 是整条识别链里最重的一段纯计算，放线程池做。
            var scaledBitmap = await Task.Run(() =>
            {
                var pixels = ImageCodec.ToBgraBytes(source, out _, out var width, out var height);
                var (scaled, scaledWidth, scaledHeight) =
                    OcrImageScaler.CropAndScale(pixels, width, height, region, factor);
                return scaledWidth <= 0 || scaledHeight <= 0
                    ? null
                    : ImageCodec.FromBgraBytes(scaled, scaledWidth, scaledHeight);
            }).ConfigureAwait(false);
            if (scaledBitmap is null) return new EnginePass([], [], "放大区域无效。");

            var pass = await RunAsync(engine, scaledBitmap).ConfigureAwait(false);
            if (pass.Error is not null) return pass;

            // zoomed 坐标 → 原图坐标：先除以放大倍率，再加上裁剪原点
            var restored = pass.Words
                .Select(w => new OcrWord(w.Text, new OcrRect(
                    region.X + w.Bounds.X / factor,
                    region.Y + w.Bounds.Y / factor,
                    w.Bounds.Width / factor,
                    w.Bounds.Height / factor)))
                .ToList();

            return new EnginePass(pass.Lines, restored, null);
        }
        catch (Exception ex)
        {
            return new EnginePass([], [], $"放大识别失败：{ex.Message}");
        }
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