using OneBox.Plugins.Screenshot.Core;
using Xunit;

namespace OneBox.Tests;

/// <summary>
/// OcrLayout 的几何与选择逻辑。全部用合成词框，不依赖真实 OCR 与 UI。
/// </summary>
public class OcrLayoutTests
{
    /// <summary>造一个词：给定左上角、宽高与文字。</summary>
    private static OcrWord W(string text, double x, double y, double w = 40, double h = 20)
        => new(text, new OcrRect(x, y, w, h));

    // ---- 行聚类 ----

    [Fact]
    public void 同一水平线上的词聚成一行()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0), W("def", 60, 0), W("ghi", 120, 0)]);

        Assert.Single(layout.Lines);
        Assert.Equal(3, layout.Lines[0].Words.Count);
    }

    [Fact]
    public void 纵向错开的词分成两行()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0), W("def", 0, 40)]);

        Assert.Equal(2, layout.Lines.Count);
    }

    [Fact]
    public void 轻微高低不平的词仍算同一行()
    {
        // 同一行的词常有 1-2 像素的基线抖动，不应被拆成两行
        var layout = OcrLayout.Build([W("abc", 0, 0), W("def", 60, 3), W("ghi", 120, -2)]);

        Assert.Single(layout.Lines);
    }

    [Fact]
    public void 行按从上到下排序()
    {
        var layout = OcrLayout.Build([W("bottom", 0, 100), W("top", 0, 0)]);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal("top", layout.Lines[0].Text);
        Assert.Equal("bottom", layout.Lines[1].Text);
    }

    [Fact]
    public void 行内的词按从左到右排序()
    {
        // 故意乱序传入
        var layout = OcrLayout.Build([W("ccc", 120, 0), W("aaa", 0, 0), W("bbb", 60, 0)]);

        Assert.Single(layout.Lines);
        Assert.Equal("aaabbbccc", layout.Lines[0].Text.Replace(" ", ""));
    }

    [Fact]
    public void 空输入得到空版面()
    {
        var layout = OcrLayout.Build([]);

        Assert.True(layout.IsEmpty);
        Assert.Equal("", layout.Text);
    }

    [Fact]
    public void 空文字与空框的词被忽略()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0), W("", 50, 0), new OcrWord("x", new OcrRect(0, 0, 0, 0))]);

        Assert.Single(layout.Lines);
        Assert.Single(layout.Lines[0].Words);
    }

    // ---- 空格插入 ----

    [Fact]
    public void 词间大空隙插入空格()
    {
        // 行高 20，空隙 60-40=20 > 20*0.22
        var layout = OcrLayout.Build([W("abc", 0, 0), W("def", 60, 0)]);

        Assert.Equal("abc def", layout.Lines[0].Text);
    }

    [Fact]
    public void 词间紧贴不插入空格()
    {
        // 中文常被拆成单字词，间隙很小，不应到处插空格
        var layout = OcrLayout.Build([W("截", 0, 0, 20, 20), W("图", 20, 0, 20, 20)]);

        Assert.Equal("截图", layout.Lines[0].Text);
    }

    [Fact]
    public void 空隙阈值随行高缩放()
    {
        // 同样的 10 像素空隙：行高小算“大空隙”，行高大算“紧贴”
        var small = OcrLayout.Build([W("ab", 0, 0, 40, 10), W("cd", 50, 0, 40, 10)]);
        var large = OcrLayout.Build([W("ab", 0, 0, 40, 100), W("cd", 50, 0, 40, 100)]);

        Assert.Equal("ab cd", small.Lines[0].Text);
        Assert.Equal("abcd", large.Lines[0].Text);
    }

    // ---- 命中测试 ----

    [Fact]
    public void 点在某字符左半边取该字符之前()
    {
        var layout = OcrLayout.Build([W("abcd", 0, 0, 40, 20)]); // 每字符 10 宽
        var caret = layout.CaretAt(2, 10); // 第 0 个字符 [0,10)，2 在左半边

        Assert.Equal(0, caret.Line);
        Assert.Equal(0, caret.Offset);
    }

    [Fact]
    public void 点在某字符右半边取该字符之后()
    {
        var layout = OcrLayout.Build([W("abcd", 0, 0, 40, 20)]);
        var caret = layout.CaretAt(8, 10); // 仍在第 0 个字符 [0,10)，8 在右半边

        Assert.Equal(1, caret.Offset);
    }

    [Fact]
    public void 点在行尾之后钳到行尾()
    {
        var layout = OcrLayout.Build([W("abcd", 0, 0, 40, 20)]);
        var caret = layout.CaretAt(9999, 10);

        Assert.Equal(4, caret.Offset);
    }

    [Fact]
    public void 点在行首之前取行首()
    {
        var layout = OcrLayout.Build([W("abcd", 10, 0, 40, 20)]);
        var caret = layout.CaretAt(-50, 10);

        Assert.Equal(0, caret.Offset);
    }

    [Fact]
    public void 点在两行之间的空隙时吸附到最近的行()
    {
        // 行1 y=[0,20]，行2 y=[100,120]，点在 y=55 更靠近行1
        var layout = OcrLayout.Build([W("top", 0, 0), W("bottom", 0, 100)]);
        var near = layout.CaretAt(5, 55);
        var far = layout.CaretAt(5, 95);

        Assert.Equal(0, near.Line);
        Assert.Equal(1, far.Line);
    }

    [Fact]
    public void 点在文字下方时取最后一行()
    {
        var layout = OcrLayout.Build([W("top", 0, 0), W("bottom", 0, 100)]);
        var caret = layout.CaretAt(5, 5000);

        Assert.Equal(1, caret.Line);
    }

    // ---- 选区取文本 ----

    [Fact]
    public void 同一行内选择取子串()
    {
        var layout = OcrLayout.Build([W("abcdef", 0, 0, 60, 20)]);
        var selection = new OcrSelection(new OcrCaret(0, 1), new OcrCaret(0, 4));

        Assert.Equal("bcd", layout.GetText(selection));
    }

    [Fact]
    public void 反向拖动得到相同文本()
    {
        var layout = OcrLayout.Build([W("abcdef", 0, 0, 60, 20)]);
        var forward = new OcrSelection(new OcrCaret(0, 1), new OcrCaret(0, 4));
        var backward = new OcrSelection(new OcrCaret(0, 4), new OcrCaret(0, 1));

        Assert.Equal(layout.GetText(forward), layout.GetText(backward));
    }

    [Fact]
    public void 跨行选择按阅读顺序拼接()
    {
        var layout = OcrLayout.Build([W("first", 0, 0), W("second", 0, 40), W("third", 0, 80)]);
        var selection = new OcrSelection(new OcrCaret(0, 2), new OcrCaret(2, 3));

        Assert.Equal("rst\nsecond\nthi", layout.GetText(selection));
    }

    [Fact]
    public void 跨行选择中间整行完整包含()
    {
        var layout = OcrLayout.Build([W("aaa", 0, 0), W("bbb", 0, 40), W("ccc", 0, 80)]);
        var selection = new OcrSelection(new OcrCaret(0, 0), new OcrCaret(2, 3));

        Assert.Equal("aaa\nbbb\nccc", layout.GetText(selection));
    }

    [Fact]
    public void 空选区取不到文本()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0)]);
        var selection = OcrSelection.At(new OcrCaret(0, 1));

        Assert.True(selection.IsEmpty);
        Assert.Equal("", layout.GetText(selection));
    }

    [Fact]
    public void 全选取到所有文本()
    {
        var layout = OcrLayout.Build([W("aaa", 0, 0), W("bbb", 0, 40)]);

        Assert.Equal("aaa\nbbb", layout.GetText(layout.SelectAll()));
    }

    [Fact]
    public void 越界的选区被钳住不抛异常()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0)]);
        var selection = new OcrSelection(new OcrCaret(-5, -5), new OcrCaret(99, 99));

        Assert.Equal("abc", layout.GetText(selection));
    }

    [Fact]
    public void 带空格的跨行选择保留词间空格()
    {
        var layout = OcrLayout.Build([W("hello", 0, 0), W("world", 80, 0, 50, 20), W("bye", 0, 40)]);
        var selection = new OcrSelection(new OcrCaret(0, 0), new OcrCaret(1, 3));

        Assert.Equal("hello world\nbye", layout.GetText(selection));
    }

    // ---- 高亮矩形 ----

    [Fact]
    public void 高亮矩形按行合并成一个()
    {
        // 同一行两个词，整体选中应只得到一个矩形，而不是每词一个方格
        var layout = OcrLayout.Build([W("abc", 0, 0, 30, 20), W("def", 60, 0, 30, 20)]);
        var rects = layout.GetHighlightRects(layout.SelectAll());

        Assert.Single(rects);
        Assert.Equal(0, rects[0].X);
        Assert.Equal(90, rects[0].Right);
        Assert.Equal(20, rects[0].Height);
    }

    [Fact]
    public void 跨行高亮每行一个矩形()
    {
        var layout = OcrLayout.Build([W("aaa", 0, 0), W("bbb", 0, 40), W("ccc", 0, 80)]);
        var rects = layout.GetHighlightRects(layout.SelectAll());

        Assert.Equal(3, rects.Count);
        Assert.Equal(0, rects[0].Y);
        Assert.Equal(40, rects[1].Y);
        Assert.Equal(80, rects[2].Y);
    }

    [Fact]
    public void 高亮矩形的左右边界与字符槽一致()
    {
        var layout = OcrLayout.Build([W("abcdef", 0, 0, 60, 20)]); // 每字符 10 宽
        var selection = new OcrSelection(new OcrCaret(0, 1), new OcrCaret(0, 4));
        var rects = layout.GetHighlightRects(selection);

        Assert.Single(rects);
        Assert.Equal(10, rects[0].X);
        Assert.Equal(40, rects[0].Right);
    }

    [Fact]
    public void 空选区没有高亮矩形()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0)]);

        Assert.Empty(layout.GetHighlightRects(OcrSelection.At(new OcrCaret(0, 1))));
    }

    [Fact]
    public void 高亮的纵向范围取整行高度()
    {
        var layout = OcrLayout.Build([W("abc", 0, 5, 30, 20)]);
        var rects = layout.GetHighlightRects(layout.SelectAll());

        Assert.Single(rects);
        Assert.Equal(5, rects[0].Y);
        Assert.Equal(20, rects[0].Height);
    }

    // ---- 双击选词 ----

    [Fact]
    public void 双击选中所在词()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0, 30, 20), W("def", 60, 0, 30, 20)]);
        var selection = layout.SelectWordAt(70, 10); // 落在第二个词

        Assert.Equal("def", layout.GetText(selection));
    }

    [Fact]
    public void 双击第一个词选中第一个()
    {
        var layout = OcrLayout.Build([W("abc", 0, 0, 30, 20), W("def", 60, 0, 30, 20)]);
        var selection = layout.SelectWordAt(10, 10);

        Assert.Equal("abc", layout.GetText(selection));
    }

    // ---- 按版式输出文本 ----

    [Fact]
    public void 版本文本保留每行换行()
    {
        var layout = OcrLayout.Build([W("第一行", 0, 0), W("第二行", 0, 40)]);

        Assert.Equal("第一行\n第二行", layout.DocumentText());
    }

    [Fact]
    public void 行距大的地方插入空行分段()
    {
        // 行高 20，前两行紧邻（行距 20），第三行远在 200 处（行距 160）→ 分段
        var layout = OcrLayout.Build([W("段一", 0, 0), W("仍在段一", 0, 20), W("段二", 0, 200)]);

        Assert.Equal("段一\n仍在段一\n\n段二", layout.DocumentText());
    }

    [Fact]
    public void 空版面的版本文本为空串()
    {
        Assert.Equal("", OcrLayout.Build([]).DocumentText());
    }

    [Fact]
    public void 单行版面没有多余空行()
    {
        var layout = OcrLayout.Build([W("只有一行", 0, 0)]);

        Assert.Equal("只有一行", layout.DocumentText());
    }

    // ---- 文本与边界 ----

    [Fact]
    public void 整篇文本按行拼接()
    {
        var layout = OcrLayout.Build([W("aaa", 0, 0), W("bbb", 0, 40)]);

        Assert.Equal("aaa\nbbb", layout.Text);
    }

    [Fact]
    public void 版面边界包住所有词()
    {
        var layout = OcrLayout.Build([W("aaa", 10, 20, 30, 20), W("bbb", 100, 60, 30, 20)]);

        Assert.Equal(10, layout.Bounds.X);
        Assert.Equal(20, layout.Bounds.Y);
        Assert.Equal(130, layout.Bounds.Right);
        Assert.Equal(80, layout.Bounds.Bottom);
    }

    [Fact]
    public void 字符级偏移可以落在词中间()
    {
        // 这是相对“整词选择”的关键差异：偏移量不限于词边界
        var layout = OcrLayout.Build([W("hello", 0, 0, 50, 20)]);
        var line = layout.Lines[0];

        var offset = line.OffsetAt(15); // 第 1 个字符 [10,20) 的右半边

        Assert.Equal(2, offset);
        Assert.NotEqual(line.SnapToWordBoundary(offset), offset); // 词边界上不该有这个位置
    }

    [Fact]
    public void 行内偏移与x坐标可以互相换算()
    {
        var layout = OcrLayout.Build([W("abcdef", 0, 0, 60, 20)]);
        var line = layout.Lines[0];

        Assert.Equal(10, line.OffsetX(1));
        Assert.Equal(40, line.OffsetX(4));
        Assert.Equal(0, line.OffsetX(0));
        Assert.Equal(60, line.OffsetX(6));
    }
}
