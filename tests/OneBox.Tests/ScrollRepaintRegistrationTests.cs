using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class ScrollRepaintRegistrationTests
{
    private const int Width = 640;
    private const int Height = 609;
    private const int Stride = Width * 4;

    private static byte[] Page(int rows, int seed = 17)
    {
        var result = Enumerable.Repeat((byte)255, rows * Stride).ToArray();
        var random = new Random(seed);
        for (var line = 0; line * 20 + 14 < rows; line++)
        for (var c = 0; c < 55; c++)
        {
            var bits = random.Next();
            for (var y = 0; y < 10; y++)
            for (var x = 0; x < 6; x++)
            {
                if (((bits >> ((y * 6 + x) % 30)) & 1) == 0) continue;
                var p = ((line * 20 + y + 3) * Width + 80 + c * 9 + x) * 4;
                result[p] = 35; result[p + 1] = 48; result[p + 2] = 65;
            }
        }
        return result;
    }

    private static byte[] Slice(byte[] page, int y) => page.AsSpan(y * Stride, Height * Stride).ToArray();

    // Same geometry, changed ink color plus a small contrast/AA change, never image registration output.
    private static void Repaint(byte[] frame)
    {
        for (var p = 0; p < frame.Length; p += 4)
        {
            if (frame[p] == 255) continue;
            frame[p] = (byte)(255 - (255 - frame[p]) * 0.75);
            frame[p + 1] = (byte)(255 - (255 - frame[p + 1]) * 0.84);
            frame[p + 2] = (byte)(255 - (255 - frame[p + 2]) * 0.67);
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public void DistributedInkRepaintKeepsExactAdvanceAndRawBgraStrip(int advance)
    {
        var page = Page(1600);
        var first = Slice(page, 0);
        var next = Slice(page, advance);
        Repaint(next);
        var stitcher = new ScrollStitcher(Width, 3000, 24);
        stitcher.Add(first, Height, Stride);
        var result = stitcher.Add(next, Height, Stride);
        Assert.True(result.Status == ScrollAppendStatus.Appended, System.Text.Json.JsonSerializer.Serialize(stitcher.LastDiagnostics));
        Assert.Equal(advance, result.AppendedRows);
        Assert.Equal(Height - advance, result.OverlapRows);
        Assert.Equal(first.Concat(next.AsSpan((Height - advance) * Stride).ToArray()).ToArray(), stitcher.BuildBgra());
    }

    [Theory]
    [InlineData("word")]
    [InlineData("line")]
    [InlineData("different")]
    [InlineData("horizontal")]
    [InlineData("scale")]
    [InlineData("alpha")]
    public void StructuralChangesAndAlphaCannotHideBehindRepaint(string change)
    {
        var page = Page(1600);
        var first = Slice(page, 0);
        var next = Slice(page, 100);
        Repaint(next);
        if (change == "word")
            for (var y = 103; y < 113; y++)
            for (var x = 80; x < 86; x++)
            {
                var p = y * Stride + x * 4;
                next[p] = next[p + 1] = next[p + 2] = next[p] < 200 ? (byte)255 : (byte)50;
            }
        if (change == "line")
            next.AsSpan(220 * Stride, (Height - 240) * Stride).CopyTo(next.AsSpan(200 * Stride));
        if (change == "different") { next = Slice(Page(1600, 99), 100); Repaint(next); }
        if (change == "horizontal")
            for (var y = 0; y < Height; y++) next.AsSpan(y * Stride, Stride - 4).CopyTo(next.AsSpan(y * Stride + 4));
        if (change == "scale")
        {
            var copy = next.ToArray();
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++) copy.AsSpan((y * Width + (int)(x / 1.01)) * 4, 4).CopyTo(next.AsSpan((y * Width + x) * 4));
        }
        if (change == "alpha") next[(120 * Width + 100) * 4 + 3] = 128;
        var stitcher = new ScrollStitcher(Width, 3000, 24);
        stitcher.Add(first, Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.Appended, stitcher.Add(next, Height, Stride).Status);
        Assert.Equal(first, stitcher.BuildBgra());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyOneCompactIsolatedOverlayMayAccompanyDistributedRepaint(bool twoObjects)
    {
        var page = Page(1600);
        var first = Slice(page, 0);
        var next = Slice(page, 100); Repaint(next);
        void Cursor(int x, int y)
        {
            for (var yy = 0; yy < 12; yy++)
            for (var xx = 0; xx < 5; xx++)
            {
                var p = ((y + yy) * Width + x + xx) * 4;
                next[p] = next[p + 1] = next[p + 2] = 50;
            }
        }
        Cursor(36, 141);
        if (twoObjects) Cursor(36, 201);
        var stitcher = new ScrollStitcher(Width, 3000, 24);
        stitcher.Add(first, Height, Stride);
        var result = stitcher.Add(next, Height, Stride);
        if (twoObjects) Assert.NotEqual(ScrollAppendStatus.Appended, result.Status);
        else
        {
            Assert.Equal(ScrollAppendStatus.Appended, result.Status);
            Assert.Equal(100, result.AppendedRows);
            Assert.Equal(first.Concat(next.AsSpan(509 * Stride).ToArray()).ToArray(), stitcher.BuildBgra());
        }
    }

    [Fact]
    public void IsolatedPixelChangeIsNotEvidenceOfAaRepaint()
    {
        var page = Page(1600);
        var first = Slice(page, 0);
        var next = Slice(page, 100);
        next[(123 * Width + 82) * 4] ^= 127;
        var stitcher = new ScrollStitcher(Width, 3000, 24);
        stitcher.Add(first, Height, Stride);
        Assert.NotEqual(ScrollAppendStatus.Appended, stitcher.Add(next, Height, Stride).Status);
    }

    [Fact]
    public void PeriodicRepaintRemainsAmbiguous()
    {
        var unit = Page(80);
        var page = new byte[1600 * Stride];
        for (var y = 0; y < 1600; y++) unit.AsSpan(y % 80 * Stride, Stride).CopyTo(page.AsSpan(y * Stride));
        var stitcher = new ScrollStitcher(Width, 3000, 24);
        stitcher.Add(Slice(page, 0), Height, Stride);
        var next = Slice(page, 100); Repaint(next);
        Assert.Equal(ScrollAppendStatus.Ambiguous, stitcher.Add(next, Height, Stride).Status);
    }
}
