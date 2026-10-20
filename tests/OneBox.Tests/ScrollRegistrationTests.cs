using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class ScrollRegistrationTests
{
    private const int Width = 320;
    private const int Height = 312;
    private const int Stride = Width * 4;

    // 白底的确定性字形：左 1/8 留白；每个字符的位置与位图随页面行变化。
    private static byte[] TextPage(int rows, int seed = 7)
    {
        var pixels = Enumerable.Repeat((byte)255, rows * Stride).ToArray();
        var random = new Random(seed);
        for (var line = 0; line * 16 + 10 < rows; line++)
        for (var c = 0; c < 30; c++)
        {
            var glyph = random.Next();
            for (var y = 0; y < 9; y++)
            for (var x = 0; x < 5; x++)
            {
                if (((glyph >> ((y * 5 + x) % 30)) & 1) == 0) continue;
                var p = ((line * 16 + y + 2) * Width + 48 + c * 8 + x) * 4;
                pixels[p] = 19; pixels[p + 1] = 27; pixels[p + 2] = 35;
            }
        }
        return pixels;
    }

    private static byte[] Slice(byte[] page, int start, int height = Height) =>
        page.AsSpan(start * Stride, height * Stride).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(150)]
    [InlineData(292)]
    public void WhiteTextBlankLeftMarginPreservesEveryBgraPixel(int advance)
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(Slice(page, 0), Height, Stride);
        var result = st.Add(Slice(page, advance), Height, Stride);
        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.Equal(advance, result.AppendedRows);
        Assert.Equal(Height - advance, result.OverlapRows);
        Assert.Equal(page.AsSpan(0, (Height + advance) * Stride).ToArray(), st.BuildBgra());
    }

    private static void AddBottomWindowBorder(byte[] frame, int rows = 3)
    {
        for (var y = Height - rows; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = y * Stride + x * 4;
            var gray = y == Height - rows ? (byte)201 : y == Height - 1 ? (byte)255 : (byte)247;
            frame[p] = frame[p + 1] = frame[p + 2] = gray;
            frame[p + 3] = 255;
        }
    }

    [Fact]
    public void ThinWindowBottomBorderDoesNotRejectScrollOrLeaveBorderAtSeams()
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        AddBottomWindowBorder(first);
        var st = new ScrollStitcher(Width, 2000, 24);
        st.Add(first, Height, Stride);
        // Include 1px steps: replacing a border can span several previously appended chunks.
        foreach (var start in new[] { 100, 101, 102, 202 })
        {
            var frame = Slice(page, start);
            AddBottomWindowBorder(frame);
            var previousHeight = st.TotalHeight;
            var result = st.Add(frame, Height, Stride);
            Assert.Equal(ScrollAppendStatus.Appended, result.Status);
            Assert.Equal(start + Height - previousHeight, result.AppendedRows);
            Assert.Equal(3, st.LastDiagnostics!.BottomBorderRows);
            var expected = page.AsSpan(0, (start + Height) * Stride).ToArray();
            frame.AsSpan((Height - 3) * Stride).CopyTo(expected.AsSpan((start + Height - 3) * Stride));
            Assert.Equal(expected, st.BuildBgra());
        }
    }

    [Theory]
    [InlineData("body")]
    [InlineData("alpha")]
    [InlineData("thick")]
    [InlineData("different-border")]
    public void BorderExceptionCannotHideContentChangesOrUnsupportedEdges(string change)
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var next = Slice(page, 100);
        AddBottomWindowBorder(first, change == "thick" ? 8 : 3);
        AddBottomWindowBorder(next, change == "thick" ? 8 : 3);
        if (change == "body") next[100 * Stride + 100 * 4] ^= 127;
        if (change == "alpha") next[(Height - 2) * Stride + 100 * 4 + 3] = 128;
        if (change == "different-border") next[(Height - 3) * Stride + 100 * 4] = 0;
        var st = new ScrollStitcher(Width, 2000, 24);
        st.Add(first, Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.Appended, st.Add(next, Height, Stride).Status);
        Assert.Equal(first, st.BuildBgra());
        Assert.Equal(1, st.FrameCount);
    }

    [Fact]
    public void BorderRepairHonorsHeightLimitAndKeepsExactPrefix()
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var next = Slice(page, 100);
        AddBottomWindowBorder(first);
        AddBottomWindowBorder(next);
        var st = new ScrollStitcher(Width, Height + 50, 24);
        st.Add(first, Height, Stride);
        var result = st.Add(next, Height, Stride);
        Assert.Equal(ScrollAppendStatus.LimitReached, result.Status);
        Assert.Equal(50, result.AppendedRows);
        Assert.Equal(page.AsSpan(0, (Height + 50) * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void BorderRepairBudgetFailureDoesNotMutateAcceptedBytes()
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var next = Slice(page, 150);
        AddBottomWindowBorder(first);
        AddBottomWindowBorder(next);
        var st = new ScrollStitcher(Width, 2000, 24, 2_000_000);
        st.Add(first, Height, Stride);
        Assert.Equal(ScrollAppendStatus.MemoryLimitReached, st.Add(next, Height, Stride).Status);
        Assert.Equal(first, st.BuildBgra());
        Assert.Equal(1, st.FrameCount);
        var small = Slice(page, 1);
        AddBottomWindowBorder(small);
        Assert.Equal(ScrollAppendStatus.Appended, st.Add(small, Height, Stride).Status);
    }

    [Fact]
    public void BorderCannotMakeInsufficientBodyOverlapPassMinimum()
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var next = Slice(page, Height - 24);
        AddBottomWindowBorder(first);
        AddBottomWindowBorder(next);
        var st = new ScrollStitcher(Width, 2000, 24);
        st.Add(first, Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.Appended, st.Add(next, Height, Stride).Status);
        Assert.Equal(first, st.BuildBgra());
    }

    [Fact]
    public void FailedFrameMustNotBecomeRegistrationBaseline()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(Slice(page, 0), Height, Stride);
        var rejected = st.Add(Slice(TextPage(1400, 99), 0), Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.Appended, rejected.Status);
        Assert.Equal(1, st.FrameCount);
        var accepted = st.Add(Slice(page, 100), Height, Stride);
        Assert.Equal(ScrollAppendStatus.Appended, accepted.Status);
        Assert.Equal(2, st.FrameCount);
        Assert.Equal(page.AsSpan(0, 412 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void EqualEightBlockMeansAreNotEqualPixels()
    {
        var first = TextPage(Height);
        var next = first.ToArray();
        // 每个 1/8 宽块内部左右翻转：八块的均值完全不变。
        for (var y = 0; y < Height; y++)
        for (var block = 0; block < 8; block++)
        for (var x = 0; x < Width / 8; x++)
            first.AsSpan((y * Width + block * 40 + x) * 4, 4)
                .CopyTo(next.AsSpan((y * Width + block * 40 + 39 - x) * 4, 4));
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(first, Height, Stride);
        var r = st.Add(next, Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.NoChange, r.Status);
        Assert.NotEqual(ScrollAppendStatus.Appended, r.Status);
        Assert.Equal(first, st.BuildBgra());
        Assert.Equal(1, st.FrameCount);
    }

    [Fact]
    public void NoOverlapTextMustNotAppendWhiteBackground()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000, 20);
        var first = Slice(page, 0);
        st.Add(first, Height, Stride);
        var r = st.Add(Slice(page, 700), Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.Appended, r.Status);
        Assert.Equal(first, st.BuildBgra());
    }

    [Fact]
    public void OnePixelStepsAcrossManyAcceptedFramesAreLossless()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000, 20);
        for (var y = 0; y <= 25; y++)
        {
            var r = st.Add(Slice(page, y), Height, Stride);
            Assert.Equal(y == 0 ? ScrollAppendStatus.First : ScrollAppendStatus.Appended, r.Status);
        }
        Assert.Equal(26, st.FrameCount);
        Assert.Equal(page.AsSpan(0, 337 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void CallerBufferMutationCannotChangeAcceptedFrame()
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(first, Height, Stride);
        Array.Fill(first, (byte)0);
        Assert.Equal(ScrollAppendStatus.Appended, st.Add(Slice(page, 100), Height, Stride).Status);
        Assert.Equal(page.AsSpan(0, 412 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void CorrectTwentyRowOverlapMustNotBecome292ByNovelty()
    {
        var random = new Random(145);
        var page = new byte[Stride * 604];
        for (var y = 0; y < 604; y++)
        {
            var shade = random.Next(20, 225);
            for (var x = 0; x < Width; x++)
            {
                var p = (y * Width + x) * 4;
                page[p] = page[p + 1] = page[p + 2] = (byte)(shade + x % 8);
                page[p + 3] = 255;
            }
        }
        // 错误的 overlap=292 在前 200 行近乎满分，真实 overlap=20 完全匹配。
        // 旧 PickByNovelty 只检查前 200 行，并按大 overlap 选择；余下 92 行其实不匹配。
        page.AsSpan(292 * Stride, 20 * Stride).CopyTo(page.AsSpan(20 * Stride));
        page.AsSpan(40 * Stride, 180 * Stride).CopyTo(page.AsSpan(312 * Stride));
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(Slice(page, 0), Height, Stride);
        var r = st.Add(Slice(page, 292), Height, Stride);
        Assert.Equal(ScrollAppendStatus.Appended, r.Status);
        Assert.Equal(20, r.OverlapRows);
        Assert.Equal(292, r.AppendedRows);
        Assert.Equal(page, st.BuildBgra());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedTexturedHeaderOrFooterIsRejectedWithoutDiscardingRows(bool footer)
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var second = Slice(page, 100);
        var edge = footer ? Height - 24 : 0;
        first.AsSpan(edge * Stride, 24 * Stride).CopyTo(second.AsSpan(edge * Stride));
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(first, Height, Stride);
        Assert.Equal(ScrollAppendStatus.FixedContent, st.Add(second, Height, Stride).Status);
        Assert.Equal(first, st.BuildBgra());
        Assert.Equal(1, st.FrameCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NarrowScrollbarMustNotTurnBlankTopMarginIntoFixedHeader(bool movingThumb)
    {
        var page = TextPage(1400);
        // 正文顶部留白，但右侧窄滚动条使每行被旧逻辑误算成有纹理。
        Array.Fill(page, (byte)255, 0, 110 * Stride);
        var first = Slice(page, 0);
        var second = Slice(page, 50);
        for (var y = 0; y < Height; y++)
        for (var x = Width - 10; x < Width; x++)
        {
            var p = y * Stride + x * 4;
            first[p] = first[p + 1] = first[p + 2] = 170;
            second[p] = second[p + 1] = second[p + 2] = 170;
        }
        if (movingThumb)
        {
            for (var y = 120; y < 150; y++)
            for (var x = Width - 8; x < Width - 2; x++)
            {
                var p = y * Stride + x * 4;
                second[p] = second[p + 1] = second[p + 2] = 80;
            }
        }
        var stitcher = new ScrollStitcher(Width, 2000, 20);
        stitcher.Add(first, Height, Stride);
        var result = stitcher.Add(second, Height, Stride);
        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.Equal(50, result.AppendedRows);
        Assert.Equal(Height - 50, result.OverlapRows);
        var expected = first.Concat(second.AsSpan((Height - 50) * Stride).ToArray()).ToArray();
        Assert.Equal(expected, stitcher.BuildBgra());
    }

    [Fact]
    public void PeriodicTextRequiresRejectingAmbiguity()
    {
        var unit = TextPage(64);
        var page = new byte[Stride * 768];
        for (var y = 0; y < 768; y++)
            unit.AsSpan(y % 64 * Stride, Stride).CopyTo(page.AsSpan(y * Stride));
        var first = Slice(page, 0);
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(first, Height, Stride);
        Assert.Equal(ScrollAppendStatus.Ambiguous, st.Add(Slice(page, 100), Height, Stride).Status);
        Assert.Equal(first, st.BuildBgra());
        Assert.Equal(1, st.FrameCount);
    }

    [Fact]
    public void HeightLimitExportsExactPrefixAndStops()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 350);
        st.Add(Slice(page, 0), Height, Stride);
        var r = st.Add(Slice(page, 100), Height, Stride);
        Assert.Equal(ScrollAppendStatus.LimitReached, r.Status);
        Assert.False(r.ShouldContinue);
        Assert.Equal(38, r.AppendedRows);
        Assert.Equal(page.AsSpan(0, 350 * Stride).ToArray(), st.BuildBgra());
        Assert.Equal(ScrollAppendStatus.LimitReached, st.Add(Slice(page, 200), Height, Stride).Status);
        Assert.Equal(2, st.FrameCount);
    }

    [Fact]
    public void FirstFrameClippedByHeightLimitAlsoStops()
    {
        var page = TextPage(Height);
        var st = new ScrollStitcher(Width, 150);
        var r = st.Add(page, Height, Stride);
        Assert.Equal(ScrollAppendStatus.LimitReached, r.Status);
        Assert.False(r.ShouldContinue);
        Assert.Equal(150, r.AppendedRows);
        Assert.Equal(page.AsSpan(0, 150 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void BudgetRejectionPreservesExportAndRegistrationBaseline()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000, 24, 2_000_000);
        Assert.Equal(ScrollAppendStatus.First, st.Add(Slice(page, 0), Height, Stride).Status);
        Assert.Equal(ScrollAppendStatus.MemoryLimitReached, st.Add(Slice(page, 150), Height, Stride).Status);
        Assert.Equal(1, st.FrameCount);
        Assert.Equal(Slice(page, 0), st.BuildBgra());
        Assert.Equal(ScrollAppendStatus.Appended, st.Add(Slice(page, 1), Height, Stride).Status);
        Assert.Equal(page.AsSpan(0, 313 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void TinyBudgetAndTruncatedInputNeverPartiallyAccept()
    {
        var st = new ScrollStitcher(Width, 2000, 24, 100);
        Assert.Equal(ScrollAppendStatus.MemoryLimitReached, st.Add(TextPage(Height), Height, Stride).Status);
        Assert.Equal(ScrollAppendStatus.NoOverlap, st.Add(new byte[Stride * Height - 1], Height, Stride).Status);
        Assert.Equal(0, st.FrameCount);
        Assert.Empty(st.BuildBgra());
    }

    [Fact]
    public void BgraChannelsAndPaddedStrideArePreservedAcrossFrames()
    {
        var page = new byte[Stride * 600];
        new Random(71).NextBytes(page);
        var st = new ScrollStitcher(Width, 2000, 20);
        byte[] Padded(int start)
        {
            var padded = Enumerable.Repeat((byte)77, (Stride + 12) * Height).ToArray();
            for (var y = 0; y < Height; y++)
                page.AsSpan((start + y) * Stride, Stride).CopyTo(padded.AsSpan(y * (Stride + 12)));
            return padded;
        }
        st.Add(Padded(0), Height, Stride + 12);
        Assert.Equal(ScrollAppendStatus.Appended, st.Add(Padded(100), Height, Stride + 12).Status);
        Assert.Equal(page.AsSpan(0, 412 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void NoChangeDoesNotCountOrAdvanceBaseline()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000, 20);
        st.Add(Slice(page, 0), Height, Stride);
        Assert.Equal(ScrollAppendStatus.NoChange, st.Add(Slice(page, 0), Height, Stride).Status);
        Assert.Equal(1, st.FrameCount);
        Assert.Equal(ScrollAppendStatus.Appended, st.Add(Slice(page, 1), Height, Stride).Status);
        Assert.Equal(page.AsSpan(0, 313 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void MinimumMatchRowsIsAnActualSafetyThreshold()
    {
        var page = TextPage(1400);
        var st = new ScrollStitcher(Width, 2000); // 默认 24 行，不应接受 20 行重叠。
        var first = Slice(page, 0);
        st.Add(first, Height, Stride);
        Assert.Equal(ScrollAppendStatus.NoOverlap, st.Add(Slice(page, 292), Height, Stride).Status);
        Assert.Equal(1, st.FrameCount);
        Assert.Equal(first, st.BuildBgra());
    }

    [Fact]
    public void ActualRenderedWhiteTextWithBlankLeftEighthIsLossless()
    {
        byte[]? page = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new System.Windows.Media.DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(System.Windows.Media.Brushes.White, null,
                        new System.Windows.Rect(0, 0, Width, 1000));
                    for (var y = 0; y < 980; y += 18)
                    {
                        var text = new System.Windows.Media.FormattedText(
                            $"Line {y:D4} document item {(y * 7919) % 100003}",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight,
                            new System.Windows.Media.Typeface("Segoe UI"), 12,
                            System.Windows.Media.Brushes.Black, 1);
                        drawing.DrawText(text, new System.Windows.Point(48, y));
                    }
                }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Width, 1000,
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);
                page = new byte[Stride * 1000];
                bitmap.CopyPixels(page, Stride, 0);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.NotNull(page);
        var st = new ScrollStitcher(Width, 2000);
        st.Add(Slice(page!, 0), Height, Stride);
        Assert.Equal(ScrollAppendStatus.Appended, st.Add(Slice(page!, 101), Height, Stride).Status);
        Assert.Equal(page!.AsSpan(0, 413 * Stride).ToArray(), st.BuildBgra());
    }

    [Fact]
    public void AlphaOnlyChangesCannotMasqueradeAsUnchangedPixels()
    {
        var page = TextPage(1400);
        var first = Slice(page, 0);
        var changed = first.ToArray();
        for (var p = 3; p < changed.Length; p += 4) changed[p] = 128;
        var st = new ScrollStitcher(Width, 2000);
        st.Add(first, Height, Stride);
        Assert.Equal(ScrollAppendStatus.NoOverlap, st.Add(changed, Height, Stride).Status);
        Assert.Equal(first, st.BuildBgra());
        Assert.Equal(1, st.FrameCount);
    }
}
