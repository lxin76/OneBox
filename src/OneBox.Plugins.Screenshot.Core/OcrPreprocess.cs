namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 图像预处理决策：在识别前估计"文字是不是太小"，并给出放大方案。
///
/// 背景（实测结论）：Windows.Media.Ocr 对小于约 20 像素的行高识别很差——
/// 同样一张 11px 中文截图，原图识别准确率约 78%；按 2 倍最近邻/双线性放大后
/// 提升到约 86%（编辑距离 21 → 13）。而把图缩小反而会变差，所以这里只放大、不缩小。
///
/// 纯逻辑，不依赖 WPF，输入是 OCR 自己算出的词框，可完整单元测试。
/// 这样"要不要放大"完全由识别结果的几何说话，而不是靠用户手填倍率。
/// </summary>
public static class OcrPreprocess
{
    /// <summary>低于该行高就认为文字偏小、值得放大。取自实测：20px 以下提升明显。</summary>
    public const double MinTextHeight = 20.0;

    /// <summary>放大倍率上限，避免极端小图被放大到无意义的尺寸。</summary>
    public const int MaxScaleFactor = 3;

    /// <summary>放大后裁剪区域的像素数上限（约 2400 万像素 = 5120×4680）。
    /// 实测 2560×1440 全屏放大 2 倍（约 1470 万像素）识别耗时仅从 225ms 增到 306ms。</summary>
    public const long DefaultMaxScaledPixels = 24_000_000L;

    /// <summary>裁掉文字外围留白时，四周各留出"中位行高 × 该系数"的余量，避免切到字形。</summary>
    private const double BoundsPaddingRatio = 0.5;

    /// <summary>余量像素的下限。</summary>
    private const double MinPadding = 4.0;

    /// <summary>
    /// 一次放大方案。<paramref name="Region"/> 为 null 或 <paramref name="Factor"/> 为 1 时表示不需要放大。
    /// </summary>
    public readonly record struct OcrEnhancementPlan(
        int Factor,
        OcrRect? Region,
        double MedianLineHeight,
        string Reason)
    {
        /// <summary>是否真的需要走放大这条路。</summary>
        public bool IsUseful => Factor > 1 && Region is { } r && !r.IsEmpty;
    }

    /// <summary>一组行框的中位高度；空输入返回 0。</summary>
    public static double MedianLineHeight(IReadOnlyList<OcrRect> lineBounds)
    {
        if (lineBounds is null || lineBounds.Count == 0) return 0;

        var heights = lineBounds
            .Where(b => !b.IsEmpty)
            .Select(b => b.Height)
            .OrderBy(h => h)
            .ToList();

        if (heights.Count == 0) return 0;
        var middle = heights.Count / 2;
        return heights.Count % 2 == 1
            ? heights[middle]
            : (heights[middle - 1] + heights[middle]) / 2.0;
    }

    /// <summary>
    /// 文字区域的包围盒，向外留出一点余量并夹到图片内。没有有效行框时返回 null。
    /// </summary>
    public static OcrRect? EstimateTextBounds(
        IReadOnlyList<OcrRect> lineBounds, int pixelWidth, int pixelHeight, double padding)
    {
        if (lineBounds is null || lineBounds.Count == 0) return null;
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;

        var bounds = lineBounds[0];
        var any = !bounds.IsEmpty;
        foreach (var b in lineBounds.Skip(1))
        {
            if (b.IsEmpty) continue;
            bounds = any ? bounds.Union(b) : b;
            any = true;
        }
        if (!any) return null;

        var pad = Math.Max(MinPadding, padding);
        var left = Math.Max(0, bounds.X - pad);
        var top = Math.Max(0, bounds.Y - pad);
        var right = Math.Min(pixelWidth, bounds.Right + pad);
        var bottom = Math.Min(pixelHeight, bounds.Bottom + pad);
        if (right <= left || bottom <= top) return null;

        return new OcrRect(left, top, right - left, bottom - top);
    }

    /// <summary>按行高选择放大倍率：越小放大越多，受像素预算与边长上限双重约束。</summary>
    public static int ChooseScaleFactor(
        double medianLineHeight, int width, int height,
        long maxScaledPixels = DefaultMaxScaledPixels, int maxDimension = 10000)
    {
        if (medianLineHeight <= 0 || width <= 0 || height <= 0) return 1;
        if (medianLineHeight >= MinTextHeight) return 1;

        // 越小给越大的倍率。2× 对 12-13px 的小字仍不够——放大后只有 25px 上下，
        // 刚过引擎的可靠阈值，"搜索→喂素、文件夹→牛夹"这类错字原样保留（实测）。
        // 14px 及以下直接给 3×；15-20px 维持 2×。
        var factor = medianLineHeight <= MinTextHeight * 0.7 ? 3 : 2;
        if (factor > MaxScaleFactor) factor = MaxScaleFactor;

        // 逐级回退直到同时满足像素预算与引擎边长上限
        while (factor > 1)
        {
            var pixels = (long)width * factor * height * factor;
            var longest = (long)Math.Max(width, height) * factor;
            if (pixels <= maxScaledPixels && longest <= maxDimension) break;
            factor--;
        }

        return factor;
    }

    /// <summary>
    /// 是否采纳放大后的识别结果。
    ///
    /// 为什么不直接用"字符数更多"：放大常常把"讠丁纟冒喋"这类错字纠正成"订单号"，
    /// 字符数可能完全不增（实测原图与放大后都是 102 个字符，但后者才是正确内容）。
    /// 也不能无条件采纳：实测一张 40 行、行高 19px 的全屏图放大 2× 后，引擎把
    /// 每个长行读成了两行，行数 40 → 77，字符数 1591 → 1473（丢掉约 7%）。
    /// 把这种结果拼成文本，行版式会被打散，还会丢内容。
    ///
    /// 因此判据是"行结构没被打散，且内容没变少"：
    /// 行数不超过原来的 1.3 倍，字符数不少于原来的 95%。
    /// - 小字图（6 行）：行数不变、字符数不降 → 采纳，错字被纠正。
    /// - 19px 全屏图（40 行）：行数 77 &gt; 52 且字符数下降 7% → 拒绝，保留原图结果。
    /// </summary>
    public static bool ShouldAcceptEnhanced(
        int baseLines, int baseChars, int enhancedLines, int enhancedChars)
    {
        if (enhancedChars <= 0) return false;
        if (baseChars <= 0) return true;

        var linesNotBroken = enhancedLines <= baseLines * 1.3;
        var charsNotLost = enhancedChars >= baseChars * 0.95;
        return linesNotBroken && charsNotLost;
    }

    /// <summary>给定版面与图片尺寸，产出放大方案。</summary>
    public static OcrEnhancementPlan Plan(
        OcrLayout? layout, int pixelWidth, int pixelHeight,
        long maxScaledPixels = DefaultMaxScaledPixels, int maxDimension = 10000)
    {
        if (layout is null || layout.IsEmpty) return new OcrEnhancementPlan(1, null, 0, "没有识别到文字");

        var lineBounds = layout.Lines.Select(l => l.Bounds).ToList();
        var median = MedianLineHeight(lineBounds);
        if (median <= 0) return new OcrEnhancementPlan(1, null, median, "无法估计行高");

        var region = EstimateTextBounds(lineBounds, pixelWidth, pixelHeight, median * BoundsPaddingRatio);
        if (region is null) return new OcrEnhancementPlan(1, null, median, "文字区域无效");

        var factor = ChooseScaleFactor(median, (int)region.Value.Width, (int)region.Value.Height,
            maxScaledPixels, maxDimension);

        var reason = factor > 1
            ? $"行高 {median:0.#}px 偏小，放大 {factor}×"
            : $"行高 {median:0.#}px，无需放大";

        return new OcrEnhancementPlan(factor, region, median, reason);
    }
}