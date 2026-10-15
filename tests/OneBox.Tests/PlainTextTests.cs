using OneBox.Plugins.TextPin.Core;

namespace OneBox.Tests;

/// <summary>纯文本处理的纯逻辑：换行统一、截断与预览。</summary>
public class PlainTextTests
{
    [Fact]
    public void NormalizeNewlines_UnifiesCrlfAndCr()
    {
        Assert.Equal("a\nb\nc", PlainText.NormalizeNewlines("a\r\nb\r\nc"));
        Assert.Equal("a\nb", PlainText.NormalizeNewlines("a\rb"));
        Assert.Equal("", PlainText.NormalizeNewlines(null));
    }

    [Fact]
    public void Truncate_KeepsShortTextUntouched()
    {
        Assert.Equal("abc", PlainText.Truncate("abc", 10));
        Assert.Equal("abc", PlainText.Truncate("abc", 3));
    }

    [Fact]
    public void Truncate_AddsNoticeAndStaysWithinLimit()
    {
        var text = new string('x', 5000);
        var result = PlainText.Truncate(text, 100);

        Assert.True(result.Length <= 100, $"截断后长度 {result.Length} 超过了上限 100");
        Assert.EndsWith("…（内容过长，已截断）", result);
    }

    [Fact]
    public void Truncate_NonPositiveLimit_ReturnsOriginal()
    {
        // 上限 <= 0 视为"不限制"，而不是把内容清空
        Assert.Equal("abc", PlainText.Truncate("abc", 0));
        Assert.Equal("abc", PlainText.Truncate("abc", -1));
    }

    [Fact]
    public void Sanitize_NormalizesThenTruncates()
    {
        var result = PlainText.Sanitize("a\r\nb\r\nc", 100);
        Assert.Equal("a\nb\nc", result);
    }

    [Fact]
    public void IsBlank_DetectsWhitespaceOnly()
    {
        Assert.True(PlainText.IsBlank(null));
        Assert.True(PlainText.IsBlank(""));
        Assert.True(PlainText.IsBlank("  \n\t "));
        Assert.False(PlainText.IsBlank(" x "));
    }

    [Fact]
    public void Preview_CollapsesNewlinesAndSpaces()
    {
        Assert.Equal("a b c", PlainText.Preview("  a\r\n   b \t c  "));
    }

    [Fact]
    public void Preview_TruncatesWithEllipsis()
    {
        var preview = PlainText.Preview(new string('y', 200), 20);
        Assert.Equal(21, preview.Length); // 20 个字 + 省略号
        Assert.EndsWith("…", preview);
    }
}
