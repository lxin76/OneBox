namespace OneBox.Plugins.Screenshot.Core;

/// <summary>仅含标量的配准诊断，不持有原始像素或特征数组。</summary>
public sealed record ScrollMatchDiagnostics
{
    public string Stage { get; init; } = "NotStarted";
    public string Status { get; init; } = "";
    public int CandidateCount { get; init; }
    public int CoarsePassedCount { get; init; }
    public int RefinedCount { get; init; }
    public int BestCoarseOverlap { get; init; }
    public int BestCoarseBad { get; init; }
    public int BestCoarseEvidence { get; init; }
    /// <summary>最佳候选的坏行占采样行比例，越低越好；无候选时为 -1。</summary>
    public double BestCoarseScore { get; init; } = -1;
    public int OverlapRows { get; init; }
    public int MatchedRows { get; init; }
    public double RepaintScore { get; init; } = -1;
    public double RepaintPeak { get; init; }
    public double RepaintRunnerUp { get; init; }
    public int RepaintTileCount { get; init; }
    public int RejectedTileCount { get; init; }
    public int OverlayCount { get; init; }
    /// <summary>两帧底部一致的极薄中性窗口边框；组装时由新帧覆盖，不裁掉正文行。</summary>
    public int BottomBorderRows { get; init; }
    public string IgnoredOverlayBounds { get; init; } = "";
}

/// <summary>
/// 只估计两张原始等宽帧的向下滚动量，不修改帧、不读取已拼接图。
/// exact 优先逐 BGRA 验证；独立重绘路径要求全文前景微区的二维梯度和唯一位移证据。
/// 不支持横向位移、缩放或固定头尾；无法唯一确认时宁可拒绝，绝不凭新增内容猜测。
/// </summary>
internal static class ScrollFrameRegistration
{
    private const int MaxColumns = 96;
    private const int MaxProbeRows = 32;
    private const int MaxRefinements = 64;
    private const int PixelTolerance = 8;

    internal readonly record struct Match(ScrollAppendStatus Status, int Overlap, int MatchedRows)
    {
        public ScrollMatchDiagnostics? Diagnostics { get; init; }
        public int BottomBorderRows { get; init; }
    }

    // 候选扫描 O(height * MaxProbeRows * MaxColumns)，非全宽全高二次匹配。
    // 至多精验 MaxRefinements 个候选，候选太多本身就是不可信的纹理/周期信号。
    public static Match Register(byte[] previous, int previousHeight, byte[] current, int height,
        int width, int minimumRows)
    {
        var border = BottomBorder(previous, previousHeight, current, height, width);
        if (border == 0 || previous.AsSpan().SequenceEqual(current))
            return RegisterCore(previous, previousHeight, current, height, width, minimumRows);
        // Use logical heights, not cropped/recolored buffers. The raw accepted frame remains intact.
        // Both viewports lose the same edge band, so the confirmed displacement is unchanged.
        var match = RegisterCore(previous, previousHeight - border, current, height - border, width, minimumRows);
        var overlap = match.Status == ScrollAppendStatus.Appended ? match.Overlap + border : match.Overlap;
        return match with { Overlap = overlap, BottomBorderRows = border, Diagnostics = match.Diagnostics! with
            { BottomBorderRows = border, OverlapRows = overlap } };
    }

    private static int BottomBorder(byte[] previous, int previousHeight, byte[] current, int height, int width)
    {
        if (previousHeight != height || height < 100 || width < 96) return 0;
        var inset = MatchingInset(width);
        // At most three rows, exactly uniform opaque neutral colors inside the scrollbar gutters.
        // Require an actual gray stroke (not a blank margin), identical in both viewports.
        for (var rows = 1; rows <= 3; rows++)
        {
            var y = height - rows;
            var p = (y * width + inset) * 4;
            var shade = previous[p];
            for (var x = inset; x < width - inset; x++)
            {
                var i = (y * width + x) * 4;
                if (previous[i] != shade || previous[i + 1] != shade || previous[i + 2] != shade
                    || previous[i + 3] != 255 || !previous.AsSpan(i, 4).SequenceEqual(current.AsSpan(i, 4)))
                    return 0;
            }
            if (shade is >= 160 and <= 224) return rows;
            if (shade < 240) return 0;
        }
        return 0;
    }

    private static Match RegisterCore(byte[] previous, int previousHeight, byte[] current, int height,
        int width, int minimumRows)
    {
        var diagnostics = new ScrollMatchDiagnostics();
        Match Finish(ScrollAppendStatus status, int overlap, int matchedRows, string stage) =>
            new(status, overlap, matchedRows) { Diagnostics = diagnostics with
                { Stage = stage, Status = status.ToString(), OverlapRows = overlap, MatchedRows = matchedRows } };
        var stride = width * 4;
        var identical = previousHeight == height && previous.AsSpan().SequenceEqual(current);

        var columns = Math.Min(width, MaxColumns);
        var old = Features(previous, previousHeight, width, columns);
        var next = Features(current, height, width, columns);
        if (!HasTexture(old, previousHeight, columns) || !HasTexture(next, height, columns))
            return identical ? Finish(ScrollAppendStatus.NoChange, height, height, "Identical")
                : Finish(ScrollAppendStatus.InsufficientTexture, 0, 0, "Texture");
        if (!identical && HasFixedEdge(previous, previousHeight, current, height, stride, old, next, columns))
            return Finish(ScrollAppendStatus.FixedContent, 0, 0, "FixedEdge");

        var valid = new List<(int Overlap, int Matched, bool Exact, RepaintEvidence Repaint)>();
        // Features are computed once; the bounded refinement stage never resamples/recolors output BGRA.
        var oldGray = Grays(previous, previousHeight, width);
        var newGray = Grays(current, height, width);
        var refinements = 0;
        var minimum = Math.Max(8, minimumRows);
        for (var overlap = minimum; overlap <= Math.Min(previousHeight, height); overlap++)
        {
            var oldStart = previousHeight - overlap;
            var coarse = CoarseMatches(old, next, oldStart, overlap, columns, out var bad, out var evidence);
            var score = (double)bad / Math.Min(overlap, MaxProbeRows);
            diagnostics = diagnostics with { CandidateCount = diagnostics.CandidateCount + 1 };
            if (diagnostics.BestCoarseOverlap == 0 || score < diagnostics.BestCoarseScore
                || (score == diagnostics.BestCoarseScore && evidence > diagnostics.BestCoarseEvidence))
                diagnostics = diagnostics with { BestCoarseOverlap = overlap, BestCoarseBad = bad,
                    BestCoarseEvidence = evidence, BestCoarseScore = score };
            if (!coarse && !RepaintCoarse(old, next, oldStart, overlap, columns)) continue;
            diagnostics = diagnostics with { CoarsePassedCount = diagnostics.CoarsePassedCount + 1 };
            if (++refinements > MaxRefinements)
                return Finish(ScrollAppendStatus.Ambiguous, 0, 0, "RefinementLimit");
            diagnostics = diagnostics with { RefinedCount = diagnostics.RefinedCount + 1 };
            var matched = Verify(previous, current, oldStart, overlap, width, old, next, columns);
            if (matched >= 0) valid.Add((overlap, matched, true, default));
            else if (overlap < height && VerifyRepaint(previous, current, oldGray, newGray, oldStart, overlap, width, out var repaint))
                valid.Add((overlap, overlap, false, repaint));
        }
        // All exact and repaint candidates compete; never return on the first exact suffix.
        if (valid.Count > 1) return Finish(ScrollAppendStatus.Ambiguous, 0, 0, "Ambiguous");
        if (valid.Count == 0) return identical ? Finish(ScrollAppendStatus.NoChange, height, height, "Identical")
            : Finish(ScrollAppendStatus.NoOverlap, 0, 0, diagnostics.CoarsePassedCount == 0 ? "Coarse" : "Verification");
        var best = valid[0];
        if (!best.Exact)
        {
            // Compare all other shifts, not only candidates that survived the color coarse filter.
            // Neighboring +/-1 peaks are excluded here, but still fail the absolute microtile verifier.
            var runnerUp = 0.0;
            var bestPeak = DisplacementScore(oldGray, newGray, previousHeight - best.Overlap, best.Overlap, width);
            for (var overlap = Math.Max(96, minimum); overlap <= Math.Min(previousHeight, height); overlap++)
                if (Math.Abs(overlap - best.Overlap) > 1)
                    runnerUp = Math.Max(runnerUp, DisplacementScore(oldGray, newGray, previousHeight - overlap, overlap, width));
            diagnostics = diagnostics with { RepaintPeak = bestPeak, RepaintRunnerUp = runnerUp };
            if (bestPeak < 0.94 || runnerUp > bestPeak - 0.04)
                return Finish(ScrollAppendStatus.Ambiguous, 0, 0, "RepaintUniqueness");
            diagnostics = diagnostics with { RepaintScore = best.Repaint.Score, RepaintTileCount = best.Repaint.Tiles,
                RejectedTileCount = best.Repaint.Rejected, OverlayCount = best.Repaint.OverlayWidth > 0 ? 1 : 0,
                IgnoredOverlayBounds = best.Repaint.OverlayWidth > 0
                    ? $"{best.Repaint.X},{best.Repaint.Y},{best.Repaint.OverlayWidth},{best.Repaint.OverlayHeight}" : "" };
        }
        // 相同高度下的零位移必须通过逐字节相等的早期检查。容差不能吞掉 1px 滚动。
        if (best.Overlap == height)
            return identical ? Finish(ScrollAppendStatus.NoChange, height, height, "Identical")
                : Finish(ScrollAppendStatus.NoOverlap, 0, 0, "ZeroDisplacement");
        return Finish(ScrollAppendStatus.Appended, best.Overlap, best.Matched, "Matched");
    }

    private static byte[] Features(byte[] pixels, int height, int width, int columns)
    {
        var features = new byte[height * columns * 2];
        for (var y = 0; y < height; y++)
        for (var col = 0; col < columns; col++)
        {
            var inset = MatchingInset(width);
            var x = columns == 1 ? inset : inset + col * (width - 1 - 2 * inset) / (columns - 1);
            var p = (y * width + x) * 4;
            var gray = Gray(pixels, p);
            var neighbor = Gray(pixels, (y * width + Math.Min(x + 1, width - 1)) * 4);
            features[(y * columns + col) * 2] = (byte)gray;
            features[(y * columns + col) * 2 + 1] = (byte)Math.Abs(gray - neighbor);
        }
        return features;
    }

    private static int Gray(byte[] pixels, int p) =>
        (pixels[p] * 29 + pixels[p + 1] * 150 + pixels[p + 2] * 77) >> 8;

    private static bool Textured(byte[] features, int row, int columns)
    {
        var min = 255;
        var max = 0;
        for (var x = 0; x < columns; x++)
        {
            var p = (row * columns + x) * 2;
            min = Math.Min(min, features[p]); max = Math.Max(max, features[p]);
            if (features[p + 1] > 16) return true;
            if (row > 0 && Math.Abs(features[p] - features[p - columns * 2]) > 16) return true;
        }
        return max - min > 16;
    }

    private static bool HasTexture(byte[] features, int height, int columns)
    {
        var rows = 0;
        for (var y = 0; y < height; y++)
            if (Textured(features, y, columns) && ++rows >= 8) return true;
        return false;
    }

    private static bool CoarseMatches(byte[] old, byte[] next, int start, int overlap, int columns,
        out int bad, out int evidence)
    {
        var samples = Math.Min(overlap, MaxProbeRows);
        bad = 0;
        evidence = 0;
        for (var i = 0; i < samples; i++)
        {
            var row = samples == 1 ? 0 : i * (overlap - 1) / (samples - 1);
            var a = (start + row) * columns * 2;
            var b = row * columns * 2;
            var mismatches = 0;
            for (var x = 0; x < columns * 2; x++)
                if (Math.Abs(old[a + x] - next[b + x]) > PixelTolerance) mismatches++;
            if (mismatches > Math.Max(1, columns / 100)) bad++;
            if (Textured(old, start + row, columns) || Textured(next, row, columns)) evidence++;
        }
        // 稀疏文字不能靠大量白行获得通过；精验仍会检查未采样的行与列。
        return evidence >= Math.Min(4, samples) && bad <= Math.Max(1, samples / 3);
    }

    private static int Verify(byte[] previous, byte[] current, int start, int overlap, int width,
        byte[] oldFeatures, byte[] newFeatures, int columns)
    {
        var stride = width * 4;
        var texturedRows = 0;
        for (var y = 0; y < overlap; y++)
        {
            var a = (start + y) * stride;
            var b = y * stride;
            var different = 0;
            // 只忽略极窄边缘 gutter（最多 16px）；正文仍逐像素精验，不扩大容差。
            var inset = MatchingInset(width) * 4;
            for (var x = inset; x < stride - inset; x += 4)
            {
                if (Math.Abs(previous[a + x] - current[b + x]) > PixelTolerance
                    || Math.Abs(previous[a + x + 1] - current[b + x + 1]) > PixelTolerance
                    || Math.Abs(previous[a + x + 2] - current[b + x + 2]) > PixelTolerance
                    || previous[a + x + 3] != current[b + x + 3])
                    different++;
            }
            // 不对整行或整图取均值，防止背景淹没小字；完整扫描所有 BGRA 通道。
            if (different > 0) return -1;
            if (Textured(oldFeatures, start + y, columns) || Textured(newFeatures, y, columns))
                texturedRows++;
        }
        return texturedRows >= 8 ? overlap : -1;
    }

    private readonly record struct RepaintEvidence(double Score, int Tiles, int Rejected,
        int X, int Y, int OverlayWidth, int OverlayHeight);

    private static byte[] Grays(byte[] pixels, int height, int width)
    {
        var gray = new byte[height * width];
        for (var p = 0; p < gray.Length; p++) gray[p] = (byte)Gray(pixels, p * 4);
        return gray;
    }

    private static double GradientScore(byte[] old, byte[] next, int start, int overlap, int columns)
    {
        double aa = 0, bb = 0, ab = 0;
        var samples = Math.Min(overlap, MaxProbeRows);
        var evidence = 0;
        for (var i = 0; i < samples; i++)
        {
            var y = i * (overlap - 1) / Math.Max(1, samples - 1);
            for (var x = 0; x < columns; x++)
            {
                var a = old[((start + y) * columns + x) * 2 + 1];
                var b = next[(y * columns + x) * 2 + 1];
                if (Math.Max(a, b) > 16) evidence++;
                aa += a * a; bb += b * b; ab += a * b;
            }
        }
        return evidence >= 16 && aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
    }

    private static double DisplacementScore(byte[] old, byte[] next, int start, int overlap, int width)
    {
        double aa = 0, bb = 0, ab = 0;
        var evidence = 0;
        var inset = MatchingInset(width) + 1;
        // Denser independent row sampling prevents a sparse 32-row comb alias on line/glyph periods.
        var samples = Math.Min(128, overlap - 2);
        for (var row = 0; row < samples; row++)
        {
            var y = 1 + row * (overlap - 3) / (samples - 1);
            for (var col = 0; col < MaxColumns; col++)
            {
                var x = inset + col * (width - 2 * inset - 1) / (MaxColumns - 1);
                var a = (start + y) * width + x; var b = y * width + x;
                var ax = old[a + 1] - old[a - 1]; var ay = old[a + width] - old[a - width];
                var bx = next[b + 1] - next[b - 1]; var by = next[b + width] - next[b - width];
                if (Math.Max(Math.Abs(ax), Math.Abs(ay)) > 16) evidence++;
                aa += ax * ax + ay * ay; bb += bx * bx + by * by; ab += ax * bx + ay * by;
            }
        }
        return evidence >= 24 && aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
    }

    private static bool RepaintCoarse(byte[] old, byte[] next, int start, int overlap, int columns) =>
        overlap >= 96 && GradientScore(old, next, start, overlap, columns) >= 0.94;

    // Signed central gradients remove the local background and normalize foreground contrast.
    // Every 8x8 microtile is checked: a word change cannot hide in a whole-page white average.
    // This path is intentionally unavailable for small overlaps or isolated pixel/color changes.
    private static bool VerifyRepaint(byte[] previous, byte[] current, byte[] old, byte[] next,
        int start, int overlap, int width, out RepaintEvidence result)
    {
        result = default;
        if (overlap < 96 || width < 96) return false;
        var inset = MatchingInset(width);
        var tiles = 0;
        var changed = 0;
        var bands = new HashSet<int>();
        var columnBands = new HashSet<int>();
        var changedBands = new HashSet<int>();
        var badTiles = new List<(int X, int Y)>();
        double score = 0;
        for (var y = 0; y < overlap; y++)
        for (var x = inset; x < width - inset; x++)
            if (previous[((start + y) * width + x) * 4 + 3] != current[(y * width + x) * 4 + 3]) return false;

        for (var ty = 1; ty < overlap - 1; ty += 8)
        for (var tx = inset + 1; tx < width - inset - 1; tx += 8)
        {
            var (aa, bb, ab, different) = Tile(tx, ty, -1, -1, -1, -1);
            if (Math.Max(aa, bb) < 3000) continue;
            var correlation = aa > 0 && bb > 0 ? ab / Math.Sqrt(aa * bb) : 0;
            if (correlation < (Math.Max(aa, bb) >= 30000 ? 0.97 : 0.90))
            {
                badTiles.Add((tx, ty));
                if (badTiles.Count > 12) return false;
                continue;
            }
            if (Math.Max(aa, bb) < 30000) continue;
            tiles++;
            score += correlation;
            bands.Add(ty * 4 / overlap);
            columnBands.Add((tx - inset) * 4 / Math.Max(1, width - 2 * inset));
            if (different >= 4) { changed++; changedBands.Add(ty * 4 / overlap); }
        }
        if (tiles < 24 || bands.Count < 3 || columnBands.Count < 2 || changed < 8 || changedBands.Count < 3)
            return false;

        var minX = width; var minY = overlap; var maxX = -1; var maxY = -1;
        if (badTiles.Count > 0)
        {
            // Only a single compact object appearing/disappearing in genuinely blank space is exempt.
            // A hover icon and a new isolated glyph are fundamentally indistinguishable from pixels;
            // this narrow exception is explicit in diagnostics, never a general changed-tile allowance.
            var direction = 0;
            foreach (var (tx, ty) in badTiles)
            for (var y = ty; y < Math.Min(ty + 8, overlap - 1); y++)
            for (var x = tx; x < Math.Min(tx + 8, width - inset - 1); x++)
            {
                var a = old[(start + y) * width + x]; var b = next[y * width + x];
                var d = a >= 240 && b < 180 ? 1 : b >= 240 && a < 180 ? -1 : 0;
                if (d == 0) continue;
                if (direction != 0 && direction != d) return false;
                direction = d;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (direction == 0 || maxX - minX + 1 > 16 || maxY - minY + 1 > 20) return false;
            var blank = direction == 1 ? old : next;
            var blankStart = direction == 1 ? start : 0;
            var occupied = direction == 1 ? next : old;
            var occupiedStart = direction == 1 ? 0 : start;
            for (var y = Math.Max(0, minY - 4); y <= Math.Min(overlap - 1, maxY + 4); y++)
            for (var x = Math.Max(inset, minX - 4); x <= Math.Min(width - inset - 1, maxX + 4); x++)
            {
                if (blank[(blankStart + y) * width + x] < 240) return false;
                if ((x < minX || x > maxX || y < minY || y > maxY)
                    && occupied[(occupiedStart + y) * width + x] < 180) return false;
            }
            // The foreground object must be a single 8-connected component, not several changed letters.
            var points = new HashSet<int>();
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
                if (occupied[(occupiedStart + y) * width + x] < 180) points.Add(y * width + x);
            if (points.Count < 3) return false;
            var queue = new Queue<int>(); queue.Enqueue(points.First()); points.Remove(queue.Peek());
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (points.Remove(p + dy * width + dx)) queue.Enqueue(p + dy * width + dx);
            }
            if (points.Count != 0) return false;
            foreach (var (tx, ty) in badTiles)
            {
                var (aa, bb, ab, _) = Tile(tx, ty, minX - 2, minY - 2, maxX + 2, maxY + 2);
                if (Math.Max(aa, bb) >= 3000 && (Math.Min(aa, bb) == 0 || ab / Math.Sqrt(aa * bb) < 0.97))
                    return false;
            }
        }
        var mean = score / tiles;
        if (mean < 0.985) return false;
        result = new(mean, tiles, badTiles.Count, minX, minY, maxX < 0 ? 0 : maxX - minX + 1,
            maxY < 0 ? 0 : maxY - minY + 1);
        return true;

        (double Aa, double Bb, double Ab, int Different) Tile(int tx, int ty, int left, int top, int right, int bottom)
        {
            double aa = 0, bb = 0, ab = 0;
            var different = 0;
            for (var y = ty; y < Math.Min(ty + 8, overlap - 1); y++)
            for (var x = tx; x < Math.Min(tx + 8, width - inset - 1); x++)
            {
                if (x >= left && x <= right && y >= top && y <= bottom) continue;
                var a = (start + y) * width + x; var b = y * width + x;
                var ax = old[a + 1] - old[a - 1]; var ay = old[a + width] - old[a - width];
                var bx = next[b + 1] - next[b - 1]; var by = next[b + width] - next[b - width];
                aa += ax * ax + ay * ay; bb += bx * bx + by * by; ab += ax * bx + ay * by;
                if (Math.Max(Math.Abs(ax), Math.Abs(ay)) > 16 && Math.Abs(old[a] - next[b]) > PixelTolerance)
                    different++;
            }
            return (aa, bb, ab, different);
        }
    }

    private static int MatchingInset(int width) => Math.Min(16, Math.Max(0, width / 20));

    private static bool HasInteriorHorizontalTexture(byte[] pixels, int row, int stride)
    {
        var width = stride / 4;
        var inset = Math.Min(32, Math.Max(2, width / 20));
        var min = 255;
        var max = 0;
        for (var x = inset; x < width - inset; x++)
        {
            var gray = Gray(pixels, row * stride + x * 4);
            min = Math.Min(min, gray);
            max = Math.Max(max, gray);
            if (max - min > 16) return true;
        }
        return false;
    }

    private static bool HasFixedEdge(byte[] previous, int oldHeight, byte[] current, int height,
        int stride, byte[] oldFeatures, byte[] newFeatures, int columns)
    {
        if (oldHeight != height) return false;
        return Edge(0, 1) || Edge(height - 1, -1);
        bool Edge(int start, int direction)
        {
            var equal = 0;
            var textured = 0;
            for (var y = start; y >= 0 && y < height; y += direction)
            {
                if (!previous.AsSpan(y * stride, stride).SequenceEqual(current.AsSpan(y * stride, stride))) break;
                equal++;
                // 固定头尾必须在正文内部有纹理：窄滚动条/侧边框不能把整行留白变成固定栏。
                if (HasInteriorHorizontalTexture(previous, y, stride) || HasInteriorHorizontalTexture(current, y, stride)) textured++;
            }
            return equal >= 8 && textured >= 4;
        }
    }
}
