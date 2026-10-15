using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using OneBox.Plugins.Screenshot.Core;
using RapidOcrNet;
using SKBitmapSkia = SkiaSharp.SKBitmap;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>
/// PaddleOCR 双引擎识别（方案①）：用 RapidOcrNet + PP-OCRv5 mobile ONNX 模型做离线推理，
/// 输出统一转成 <see cref="OcrOutcome"/>，与系统引擎（Windows.Media.Ocr）共用
/// OcrLayout / OcrText 的整条版式与文本整理链路。
///
/// 引擎选择策略：模型文件齐全时优先用本引擎（质量明显更高），否则回退系统引擎。
/// 模型不打包进插件——由 PrepareModels.ps1 下载到 models/v5/，缺失时静默回退，零配置可用。
///
/// 线程模型：RapidOcr 实例不是线程安全且初始化要加载三个模型（约几百毫秒），
/// 这里用懒加载单例 + 锁串行化识别。识别本身是 CPU 密集活，调用方应在线程池上调用。
///
/// 预处理选项：见 <see cref="BuildOptions"/>。要点是把 RapidOcrNet 默认的
/// ImgResize=1024（长边封顶）换成短边自适应，否则长图会整体糊掉。
/// </summary>
public static class PaddleOcrService
{
    private static readonly object InitLock = new();
    private static RapidOcr? _engine;
    private static bool _initFailed;
    private static string? _modelDirectory;

    /// <summary>模型目录。默认插件 exe 旁的 models\v5\，测试可注入。</summary>
    public static string ModelDirectory
        => _modelDirectory ??= Path.Combine(AppContext.BaseDirectory, "models", "v5");

    /// <summary>模型文件是否齐全（齐全才启用 PaddleOCR 引擎）。</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(ModelDirectory, "ch_PP-OCRv5_mobile_det.onnx"))
                    && File.Exists(Path.Combine(ModelDirectory, "ch_PP-OCRv5_rec_mobile.onnx"))
                    && File.Exists(Path.Combine(ModelDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"))
                    && File.Exists(Path.Combine(ModelDirectory, "ppocrv5_dict.txt"));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>识别位图里的文字。必须在非 UI 线程调用（内部有 CPU 密集推理）。</summary>
    public static async Task<OcrOutcome> RecognizeAsync(
        BitmapSource source, bool stripCjkSpaces = true, bool enhanceSmallText = true)
    {
        try
        {
            var engine = await GetEngineAsync();
            if (engine is null)
            {
                return OcrOutcome.Fail("PaddleOCR 模型未就绪。");
            }

            // BGRA 像素拷到 SkiaSharp 位图（线程池上执行）。
            var pixels = ImageCodec.ToBgraBytes(source, out _, out var width, out var height);
            var outcome = await Task.Run(() =>
            {
                using var skBitmap = new SKBitmapSkia(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                Marshal.Copy(pixels, 0, skBitmap.GetPixels(), pixels.Length);
                return Detect(engine, skBitmap, stripCjkSpaces, enhanceSmallText);
            }).ConfigureAwait(false);

            return outcome;
        }
        catch (Exception ex)
        {
            AppLog.Write($"PaddleOCR 识别失败: {ex.Message}");
            return OcrOutcome.Fail($"识别失败：{ex.Message}");
        }
    }

    /// <summary>懒加载引擎；模型缺失或初始化失败返回 null（调用方回退系统引擎）。</summary>
    private static async Task<RapidOcr?> GetEngineAsync()
    {
        if (_engine is not null || _initFailed) return _engine;

        return await Task.Run(() =>
        {
            lock (InitLock)
            {
                if (_engine is not null || _initFailed) return _engine;

                try
                {
                    if (!IsAvailable)
                    {
                        _initFailed = true;
                        AppLog.Write("PaddleOCR 模型不齐全，回退系统引擎");
                        return null;
                    }

                    var engine = new RapidOcr();
                    // PP-OCRv5 中文模型集：det/cls 用包内自带，rec 与 dict 用 PrepareModels.ps1 下载的中文版
                    var modelSet = new RapidOcrModelSet
                    {
                        DetModelPath = Path.Combine(ModelDirectory, "ch_PP-OCRv5_mobile_det.onnx"),
                        ClsModelPath = Path.Combine(ModelDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                        RecModelPath = Path.Combine(ModelDirectory, "ch_PP-OCRv5_rec_mobile.onnx"),
                        KeysPath = Path.Combine(ModelDirectory, "ppocrv5_dict.txt"),
                        DetMean = [0.485F * 255F, 0.456F * 255F, 0.406F * 255F],
                        DetStd = [0.229F * 255F, 0.224F * 255F, 0.225F * 255F],
                    };
                    engine.InitModels(modelSet);
                    _engine = engine;
                    AppLog.Write("PaddleOCR 引擎已加载（PP-OCRv5 mobile 中文）");
                    return _engine;
                }
                catch (Exception ex)
                {
                    _initFailed = true;
                    AppLog.Write($"PaddleOCR 引擎初始化失败: {ex.Message}");
                    return null;
                }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 选择识别选项。
    ///
    /// 两套预设都刻意保持 <c>ImgResize = 0</c>（改用 LimitSideLen 做短边自适应），
    /// 因为 RapidOcrNet 默认预设的 <c>ImgResize = 1024</c> 会把长边压到 1024：
    /// 滚动截长图产出的 875×4000 图，13px 小字被压成约 3px，实测**一个文字块都识别不出来**；
    /// 换成短边自适应后同图可正常出 179 块。
    ///
    /// <paramref name="enhanceSmallText"/> 对应设置页的「小字图像增强」：
    /// 开启时短边自适应放大到 736（小字更准），关闭时不做放大（更快）。
    /// </summary>
    private static RapidOcrOptions BuildOptions(bool enhanceSmallText)
        => enhanceSmallText
            ? RapidOcrOptions.PythonCompat
            : RapidOcrOptions.PythonCompat with { LimitSideLen = 0 };

    /// <summary>跑一轮识别并把结果转成 OcrOutcome。</summary>
    private static OcrOutcome Detect(
        RapidOcr engine, SKBitmapSkia bitmap, bool stripCjkSpaces, bool enhanceSmallText)
    {
        var result = engine.Detect(bitmap, BuildOptions(enhanceSmallText));
        if (result.TextBlocks.Length == 0)
        {
            return new OcrOutcome(true, "", null, [], null, null);
        }

        // TextBlock.BoxPoints 是四点多边形（可能带倾斜），取外接矩形转 OcrWord。
        // 坐标系就是输入位图的像素坐标，与 originWidth/Height 一致，无需换算。
        var words = new List<OcrWord>();
        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrEmpty(block.Text)) continue;

            var left = block.BoxPoints.Min(p => p.X);
            var top = block.BoxPoints.Min(p => p.Y);
            var right = block.BoxPoints.Max(p => p.X);
            var bottom = block.BoxPoints.Max(p => p.Y);
            if (right <= left || bottom <= top) continue;

            words.Add(new OcrWord(block.Text, new OcrRect(left, top, right - left, bottom - top)));
        }

        if (words.Count == 0)
        {
            return new OcrOutcome(true, "", null, [], null, null);
        }

        var layout = OcrLayout.Build(words);
        var lines = layout.Lines.Select(l => l.Text).ToList();
        var text = OcrText.NormalizeDocument(layout.DocumentText(), stripCjkSpaces);
        // 备注里带上是哪条引擎、有没有做小字放大，便于排障时一眼看出实际路径
        var note = enhanceSmallText
            ? "PaddleOCR PP-OCRv5 mobile · 小字放大"
            : "PaddleOCR PP-OCRv5 mobile";
        return new OcrOutcome(true, text, null, lines, layout, note);
    }
}
