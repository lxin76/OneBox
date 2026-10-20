using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

/// <summary>贴图标注的几何、颜色与撤销栈测试。全部是纯逻辑，不依赖 WPF。</summary>
public class PinAnnotationTests
{
    private static readonly AnnotationColor Red = PinAnnotationPalette.DefaultColor;

    [Fact]
    public void 矩形反向拖动会归一化()
    {
        var shape = PinAnnotationFactory.CreateShape(
            PinShapeKind.Rectangle, 100, 80, 20, 10, Red, 2, 400, 300);

        Assert.NotNull(shape);
        Assert.Equal(20, shape!.X);
        Assert.Equal(10, shape.Y);
        Assert.Equal(80, shape.Width);
        Assert.Equal(70, shape.Height);
    }

    [Fact]
    public void 椭圆形状保留类型()
    {
        var shape = PinAnnotationFactory.CreateShape(
            PinShapeKind.Ellipse, 10, 10, 90, 60, Red, 3, 100, 100);

        Assert.NotNull(shape);
        Assert.Equal(PinShapeKind.Ellipse, shape!.Kind);
        Assert.Equal(3, shape.Thickness);
    }

    [Fact]
    public void 越界拖动会夹取到图片内()
    {
        var shape = PinAnnotationFactory.CreateShape(
            PinShapeKind.Rectangle, -20, -10, 130, 80, Red, 2, 100, 100);

        Assert.NotNull(shape);
        Assert.Equal(0, shape!.X);
        Assert.Equal(0, shape.Y);
        Assert.Equal(100, shape.Width);
        Assert.Equal(80, shape.Height);
    }

    [Fact]
    public void 过小的拖动不会创建形状()
    {
        var shape = PinAnnotationFactory.CreateShape(
            PinShapeKind.Rectangle, 10, 10, 12, 12, Red, 2, 100, 100);

        Assert.Null(shape);
    }

    [Fact]
    public void 空白文字不会创建标注()
    {
        Assert.Null(PinAnnotationFactory.CreateText(
            10, 10, "   \r\n  ", Red, 20, 100, 200, 200));
    }

    [Fact]
    public void 文字坐标与宽度会夹取到图片内()
    {
        var text = PinAnnotationFactory.CreateText(
            280, -20, "说明", Red, 20, 400, 300, 200);

        Assert.NotNull(text);
        Assert.Equal(280, text!.X);
        Assert.Equal(0, text.Y);
        Assert.Equal(20, text.MaxWidth);
        Assert.Equal("说明", text.Text);
    }

    [Fact]
    public void 文字会被Trim但保留内部换行()
    {
        var text = PinAnnotationFactory.CreateText(
            10, 10, "  第一行\r\n第二行  ", Red, 20, 100, 300, 200);

        Assert.NotNull(text);
        Assert.Equal("第一行\r\n第二行", text!.Text);
    }

    [Fact]
    public void 字号与粗细会被夹取到允许范围()
    {
        var text = PinAnnotationFactory.CreateText(
            10, 10, "说明", Red, 200, 100, 300, 200);
        var shape = PinAnnotationFactory.CreateShape(
            PinShapeKind.Rectangle, 10, 10, 60, 60, Red, 99, 100, 100);

        Assert.NotNull(text);
        Assert.NotNull(shape);
        Assert.Equal(PinAnnotationLimits.MaxFontSize, text!.FontSize);
        Assert.Equal(PinAnnotationLimits.MaxThickness, shape!.Thickness);
    }

    [Fact]
    public void 箭头保留起点终点与方向()
    {
        var arrow = PinAnnotationFactory.CreateArrow(
            90, 80, 10, 20, Red, 3, 100, 100);

        Assert.NotNull(arrow);
        Assert.Equal(90, arrow!.StartX);
        Assert.Equal(80, arrow.StartY);
        Assert.Equal(10, arrow.EndX);
        Assert.Equal(20, arrow.EndY);
        Assert.Equal(3, arrow.Thickness);
    }

    [Fact]
    public void 箭头越界会夹取到图片内()
    {
        var arrow = PinAnnotationFactory.CreateArrow(
            -20, -10, 130, 80, Red, 2, 100, 100);

        Assert.NotNull(arrow);
        Assert.Equal(0, arrow!.StartX);
        Assert.Equal(0, arrow.StartY);
        Assert.Equal(100, arrow.EndX);
        Assert.Equal(80, arrow.EndY);
    }

    [Fact]
    public void 过短的箭头不会创建标注()
    {
        Assert.Null(PinAnnotationFactory.CreateArrow(
            10, 10, 12, 12, Red, 2, 100, 100));
    }

    [Fact]
    public void 箭头粗细会被夹取()
    {
        var arrow = PinAnnotationFactory.CreateArrow(
            10, 10, 80, 80, Red, 99, 100, 100);

        Assert.NotNull(arrow);
        Assert.Equal(PinAnnotationLimits.MaxThickness, arrow!.Thickness);
    }

    [Fact]
    public void 颜色ARGB往返()
    {
        var color = AnnotationColor.FromArgb(0xCC112233);

        Assert.Equal(0xCC, color.A);
        Assert.Equal(0x11, color.R);
        Assert.Equal(0x22, color.G);
        Assert.Equal(0x33, color.B);
        Assert.Equal(0xCC112233u, color.ToArgb());
        Assert.Equal("#CC112233", color.ToHex());
    }

    [Fact]
    public void 调色板提供预设颜色与默认色()
    {
        Assert.Equal(8, PinAnnotationPalette.Choices.Count);
        Assert.Equal(PinAnnotationPalette.Choices[0].Color, PinAnnotationPalette.DefaultColor);
        Assert.Contains(PinAnnotationPalette.Choices, c => c.Name == "红色");
    }

    [Fact]
    public void 撤销按后进先出顺序()
    {
        var history = new PinAnnotationHistory();
        var first = new TextAnnotation(0, 0, "一", Red, 20, 100);
        var second = new ShapeAnnotation(PinShapeKind.Rectangle, 0, 0, 20, 20, Red, 2);

        history.Add(first);
        history.Add(second);

        Assert.Equal(2, history.Count);
        Assert.True(history.CanUndo);
        Assert.Equal(second, history.Undo());
        Assert.Equal(first, history.Undo());
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void 空撤销栈返回空()
    {
        var history = new PinAnnotationHistory();

        Assert.False(history.CanUndo);
        Assert.Null(history.Undo());
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Clear会清空全部标注()
    {
        var history = new PinAnnotationHistory();
        history.Add(new TextAnnotation(0, 0, "一", Red, 20, 100));
        history.Add(new ShapeAnnotation(PinShapeKind.Ellipse, 0, 0, 20, 20, Red, 2));

        history.Clear();

        Assert.Empty(history.Items);
        Assert.False(history.CanUndo);
    }
}
