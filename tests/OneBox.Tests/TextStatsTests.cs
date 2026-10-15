using OneBox.Plugins.TextPin.Core;

namespace OneBox.Tests;

public class TextStatsTests
{
    [Fact]
    public void EmptyText_IsZeroLinesNotOne()
    {
        // 空窗口不该显示"1 行"——那看起来像有一行空内容
        var stats = TextStats.Measure("");
        Assert.Equal(0, stats.Characters);
        Assert.Equal(0, stats.Lines);
        Assert.Equal(0, stats.Bytes);
        Assert.True(stats.IsEmpty);
        Assert.Equal("空", stats.Describe());
    }

    [Fact]
    public void SingleLine_CountsOneLine()
    {
        var stats = TextStats.Measure("hello");
        Assert.Equal(5, stats.Characters);
        Assert.Equal(1, stats.Lines);
        Assert.Equal(5, stats.Bytes);
    }

    [Fact]
    public void TrailingNewline_CountsExtraLine()
    {
        Assert.Equal(2, TextStats.Measure("a\n").Lines);
        Assert.Equal(3, TextStats.Measure("a\nb\nc").Lines);
    }

    [Fact]
    public void Bytes_AreUtf8Encoded()
    {
        // 一个汉字在 UTF-8 下占 3 字节
        Assert.Equal(3, TextStats.Measure("汉").Bytes);
        Assert.Equal(1, TextStats.Measure("a").Bytes);
    }

    [Fact]
    public void Describe_OmitsLineCountForSingleLine()
    {
        var text = TextStats.Measure("hello").Describe();
        Assert.DoesNotContain("行", text);
        Assert.Contains("5 字", text);
    }
}
