using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Plugins.Screenshot.Services;

/// <summary>
/// 贴图标注的绘制器。屏幕显示（AnnotationView）与导出合成图（PinWindow.ComposeImage）
/// 共用这一套绘制逻辑，保证所见即所得。
/// </summary>
internal static class PinAnnotationRenderer
{
    /// <summary>编辑框与最终绘制使用同一字体，避免提交时文字位置/宽度跳变。</summary>
    public static FontFamily AnnotationFontFamily { get; } = SystemFonts.MessageFontFamily;

    private static readonly Typeface AnnotationTypeface = new(
        AnnotationFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static void Draw(
        DrawingContext drawingContext,
        IReadOnlyList<PinAnnotation> annotations,
        double scaleX,
        double scaleY,
        double pixelsPerDip)
    {
        foreach (var annotation in annotations)
            DrawOne(drawingContext, annotation, scaleX, scaleY, pixelsPerDip, draft: false);
    }

    public static void DrawDraft(
        DrawingContext drawingContext,
        PinAnnotation annotation,
        double scaleX,
        double scaleY,
        double pixelsPerDip)
        => DrawOne(drawingContext, annotation, scaleX, scaleY, pixelsPerDip, draft: true);

    private static void DrawOne(
        DrawingContext drawingContext,
        PinAnnotation annotation,
        double scaleX,
        double scaleY,
        double pixelsPerDip,
        bool draft)
    {
        switch (annotation)
        {
            case ShapeAnnotation shape:
                DrawShape(drawingContext, shape, scaleX, scaleY, draft);
                break;

            case TextAnnotation text:
                DrawText(drawingContext, text, scaleX, scaleY, pixelsPerDip, draft);
                break;

            case ArrowAnnotation arrow:
                DrawArrow(drawingContext, arrow, scaleX, scaleY, draft);
                break;
        }
    }

    private static void DrawShape(
        DrawingContext drawingContext,
        ShapeAnnotation shape,
        double scaleX,
        double scaleY,
        bool draft)
    {
        var scale = Math.Max(0.01, Math.Min(scaleX, scaleY));
        var pen = CreatePen(shape.Color, shape.Thickness * scale, draft);
        var rect = new Rect(
            shape.X * scaleX,
            shape.Y * scaleY,
            Math.Max(0, shape.Width * scaleX),
            Math.Max(0, shape.Height * scaleY));

        if (shape.Kind == PinShapeKind.Ellipse)
        {
            // DrawingContext.DrawEllipse 没有 Rect 重载，需要中心点 + 两个半径。
            var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            drawingContext.DrawEllipse(null, pen, center, rect.Width / 2, rect.Height / 2);
        }
        else
        {
            drawingContext.DrawRectangle(null, pen, rect);
        }
    }

    private static void DrawText(
        DrawingContext drawingContext,
        TextAnnotation text,
        double scaleX,
        double scaleY,
        double pixelsPerDip,
        bool draft)
    {
        var brush = CreateBrush(text.Color, draft ? 0.65 : 1.0);
        var formatted = new FormattedText(
            text.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            AnnotationTypeface,
            Math.Max(1, text.FontSize * scaleY),
            brush,
            Math.Max(0.1, pixelsPerDip))
        {
            MaxTextWidth = Math.Max(1, text.MaxWidth * scaleX),
            Trimming = TextTrimming.None,
        };

        drawingContext.DrawText(
            formatted,
            new Point(text.X * scaleX, text.Y * scaleY));
    }

    private static void DrawArrow(
        DrawingContext drawingContext,
        ArrowAnnotation arrow,
        double scaleX,
        double scaleY,
        bool draft)
    {
        var start = new Point(arrow.StartX * scaleX, arrow.StartY * scaleY);
        var end = new Point(arrow.EndX * scaleX, arrow.EndY * scaleY);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5) return;

        var scale = Math.Max(0.01, Math.Min(scaleX, scaleY));
        var thickness = arrow.Thickness * scale;
        var pen = CreatePen(arrow.Color, thickness, draft);
        var brush = CreateBrush(arrow.Color, draft ? 0.65 : 1.0);

        // 箭头头部按图片像素尺寸计算，并限制在整条线长度以内；短线也能看到尖头。
        var headLength = Math.Min(length * 0.9, Math.Max(10, arrow.Thickness * 5) * scale);
        var headWidth = headLength * 0.62;

        var ux = dx / length;
        var uy = dy / length;
        var baseX = end.X - ux * headLength;
        var baseY = end.Y - uy * headLength;
        var perpX = -uy;
        var perpY = ux;

        var left = new Point(baseX + perpX * headWidth / 2, baseY + perpY * headWidth / 2);
        var right = new Point(baseX - perpX * headWidth / 2, baseY - perpY * headWidth / 2);

        drawingContext.DrawLine(pen, start, new Point(baseX, baseY));

        var head = new StreamGeometry();
        using (var context = head.Open())
        {
            context.BeginFigure(end, isFilled: true, isClosed: true);
            context.LineTo(left, isStroked: true, isSmoothJoin: false);
            context.LineTo(right, isStroked: true, isSmoothJoin: false);
        }
        head.Freeze();
        drawingContext.DrawGeometry(brush, null, head);
    }

    private static Pen CreatePen(AnnotationColor color, double thickness, bool draft)
    {
        var pen = new Pen(CreateBrush(color, draft ? 0.65 : 1.0), Math.Max(1, thickness))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashStyle = draft ? DashStyles.Dash : DashStyles.Solid,
        };
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush CreateBrush(AnnotationColor color, double opacity)
    {
        var alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
