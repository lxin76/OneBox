using ClipboardFloat.Core;

namespace ClipboardFloat.Tests;

public class ClipboardTextTests
{
    [Fact]
    public void NormalizeNewlines_ConvertsCrLf()
    {
        Assert.Equal("a\nb\nc", ClipboardText.NormalizeNewlines("a\r\nb\r\nc"));
    }

    [Fact]
    public void NormalizeNewlines_ConvertsLoneCr()
    {
        Assert.Equal("a\nb", ClipboardText.NormalizeNewlines("a\rb"));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("http://localhost:8080/path", true)]
    [InlineData("不是链接", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("", false)]
    public void IsProbablyUrl_Works(string text, bool expected)
    {
        Assert.Equal(expected, ClipboardText.IsProbablyUrl(text));
    }

    [Theory]
    [InlineData("<p>Hello <b>world</b></p>", "Hello world")]
    [InlineData("a<br>b", "a\nb")]
    [InlineData("<div>1</div><div>2</div>", "1\n2")]
    [InlineData("&lt;tag&gt; &amp; more", "<tag> & more")]
    [InlineData("<script>evil()</script>ok", "ok")]
    public void HtmlToPlainText_Works(string html, string expected)
    {
        Assert.Equal(expected, ClipboardText.HtmlToPlainText(html));
    }
}
