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

    [Fact]
    public void 版本文本保留换行()
    {
        Assert.Equal("第一行\n第二行", OcrText.NormalizeDocument("第一行\n第二行", stripCjkSpaces: false));
    }

    [Fact]
    public void 版本文本保留段落空行()
    {
        // 这正是与 Normalize 的关键差别：Normalize 会把空行折叠掉
        Assert.Equal("第一段\n\n第二段", OcrText.NormalizeDocument("第一段\n\n第二段", stripCjkSpaces: false));
        Assert.Equal("第一段\n第二段", OcrText.Normalize("第一段\n\n第二段", stripCjkSpaces: false));
    }

    [Fact]
    public void 版本文本折叠连续空行并去掉首尾空行()
    {
        Assert.Equal("A\n\nB", OcrText.NormalizeDocument("\n\nA\n\n\n\nB\n\n\n", stripCjkSpaces: false));
    }

    [Fact]
    public void 版本文本逐行去掉中文之间的空格()
    {
        Assert.Equal("截图\n识别", OcrText.NormalizeDocument("截 图 \n 识 别", stripCjkSpaces: true));
    }

    [Fact]
    public void 版本文本空输入返回空串()
    {
        Assert.Equal("", OcrText.NormalizeDocument(null, true));
        Assert.Equal("", OcrText.NormalizeDocument("   \n  ", true));
    }
}
