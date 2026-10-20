using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class ScrollRegistrationDiagnosticsTests
{
    private const int Width = 80;
    private const int Height = 96;
    private const int Stride = Width * 4;

    private static byte[] Page(int seed, int rows = Height)
    {
        var pixels = new byte[rows * Stride];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    [Theory]
    [InlineData(17)]
    [InlineData(29)]
    public void DifferentNonOverlappingFramesExposeBestRejectedCandidate(int seed)
    {
        var first = Page(3);
        var stitcher = new ScrollStitcher(Width, 1000, 20);
        stitcher.Add(first, Height, Stride);
        var result = stitcher.Add(Page(seed), Height, Stride);
        Assert.Equal(ScrollAppendStatus.NoOverlap, result.Status);
        var diagnostics = Assert.IsType<ScrollMatchDiagnostics>(stitcher.LastDiagnostics);
        Assert.Equal("Coarse", diagnostics.Stage);
        Assert.Equal("NoOverlap", diagnostics.Status);
        Assert.Equal(Height - 20 + 1, diagnostics.CandidateCount);
        Assert.Equal(0, diagnostics.CoarsePassedCount);
        Assert.Equal(0, diagnostics.RefinedCount);
        Assert.InRange(diagnostics.BestCoarseOverlap, 20, Height);
        Assert.InRange(diagnostics.BestCoarseScore, 0, 1);
        Assert.True(diagnostics.BestCoarseBad > 0);
        Assert.True(diagnostics.BestCoarseEvidence > 0);
        Assert.Equal(first, stitcher.BuildBgra());
    }

    [Fact]
    public void SuccessfulDiagnosticsAreAccurateAndPreserveEveryBgraByte()
    {
        const int advance = 31;
        var page = Page(7, Height + advance);
        var first = page.AsSpan(0, Height * Stride).ToArray();
        var next = page.AsSpan(advance * Stride, Height * Stride).ToArray();
        var firstCopy = first.ToArray();
        var nextCopy = next.ToArray();
        var stitcher = new ScrollStitcher(Width, 1000, 20);
        Assert.Null(stitcher.LastDiagnostics);
        stitcher.Add(first, Height, Stride);
        Assert.Equal("First", stitcher.LastDiagnostics!.Stage);
        var result = stitcher.Add(next, Height, Stride);
        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.Equal(advance, result.AppendedRows);
        var diagnostics = Assert.IsType<ScrollMatchDiagnostics>(stitcher.LastDiagnostics);
        Assert.Equal("Matched", diagnostics.Stage);
        Assert.Equal("Appended", diagnostics.Status);
        Assert.Equal(Height - advance, diagnostics.OverlapRows);
        Assert.Equal(Height - advance, diagnostics.MatchedRows);
        Assert.Equal(Height - advance, diagnostics.BestCoarseOverlap);
        Assert.Equal(0, diagnostics.BestCoarseScore);
        Assert.Equal(1, diagnostics.CoarsePassedCount);
        Assert.Equal(1, diagnostics.RefinedCount);
        Assert.Equal(page, stitcher.BuildBgra());
        _ = diagnostics.ToString();
        Assert.Equal(page, stitcher.BuildBgra());
        Assert.Equal(firstCopy, first);
        Assert.Equal(nextCopy, next);
        Assert.Equal(ScrollAppendStatus.NoChange, stitcher.Add(next, Height, Stride).Status);
        Assert.Equal("Identical", stitcher.LastDiagnostics!.Stage);
        Assert.Equal("NoChange", stitcher.LastDiagnostics.Status);
        Assert.Equal(page, stitcher.BuildBgra());
        stitcher.Add([], 0, Stride);
        Assert.Equal("Input", stitcher.LastDiagnostics!.Stage);
        Assert.Equal(0, stitcher.LastDiagnostics.CandidateCount);
    }

    [Fact]
    public void DiagnosticsPropertiesContainOnlyNumbersOrStrings()
    {
        Assert.All(typeof(ScrollMatchDiagnostics).GetProperties(), property =>
            Assert.True(property.PropertyType == typeof(string) || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(double), property.Name));
    }
}
