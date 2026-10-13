using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class OcrTextTests
{
    [Fact]
    public void 去掉中文之间的空格()
    {
        Assert.Equal("截图识别", OcrText.StripCjkSpaces("截 图 识 别"));
    }

    [Fact]
    public void 保留中英文之间的空格()
    {
        Assert.Equal("OneBox 截图", OcrText.StripCjkSpaces("OneBox 截 图"));
    }

    [Fact]
    public void 保留纯英文里的空格()
    {
        Assert.Equal("hello world", OcrText.StripCjkSpaces("hello world"));
    }

    [Fact]
    public void 多行结果去掉首尾空白并折叠空行()
    {
        var raw = "  第一行  \r\n\r\n\r\n  第二行\r\n";
        Assert.Equal("第一行\n第二行", OcrText.Normalize(raw, stripCjkSpaces: true));
    }

    [Fact]
    public void 关闭去空格时保留原文()
    {
        Assert.Equal("截 图", OcrText.Normalize(" 截 图 ", stripCjkSpaces: false));
    }

    [Fact]
    public void 空输入返回空串()
    {
        Assert.Equal("", OcrText.Normalize(null, true));
        Assert.Equal("", OcrText.Normalize("   ", true));
        Assert.Equal("", OcrText.StripCjkSpaces(null));
    }

    [Fact]
    public void 识别CJK字符范围()
    {
        Assert.True(OcrText.IsCjk('中'));
        Assert.True(OcrText.IsCjk('あ'));
        Assert.True(OcrText.IsCjk('한'));
        Assert.False(OcrText.IsCjk('A'));
        Assert.False(OcrText.IsCjk('1'));
    }
}
