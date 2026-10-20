namespace OneBox.Plugins.Screenshot.Core;

/// <summary>标注的可输入范围。UI 的步进按钮与工厂共用这一份边界。</summary>
public static class PinAnnotationLimits
{
    /// <summary>小于该尺寸的拖动视为误触，不产生形状。</summary>
    public const double MinShapeSize = 4;

    public const double MinThickness = 1;
    public const double MaxThickness = 8;
    public const double DefaultThickness = 2;

    public const double MinFontSize = 10;
    public const double MaxFontSize = 72;
    public const double DefaultFontSize = 20;

    /// <summary>文字标注的最小换行宽度。</summary>
    public const double MinTextWidth = 40;

    /// <summary>小于该长度的拖动视为误触，不产生箭头。</summary>
    public const double MinArrowLength = 8;

    public static double ClampThickness(double value)
        => Math.Clamp(value, MinThickness, MaxThickness);

    public static double ClampFontSize(double value)
        => Math.Clamp(value, MinFontSize, MaxFontSize);
}

/// <summary>调色板中的一项；Name 用于工具条 ToolTip。</summary>
public sealed record PinAnnotationColorChoice(string Name, AnnotationColor Color);

/// <summary>贴图标注的预设颜色。只做固定调色板，不引入取色器依赖。</summary>
public static class PinAnnotationPalette
{
    public static IReadOnlyList<PinAnnotationColorChoice> Choices { get; } =
    [
        new("红色", AnnotationColor.FromRgb(0xFF, 0x3B, 0x30)),
        new("橙色", AnnotationColor.FromRgb(0xFF, 0x95, 0x00)),
        new("黄色", AnnotationColor.FromRgb(0xFF, 0xCC, 0x00)),
        new("绿色", AnnotationColor.FromRgb(0x34, 0xC7, 0x59)),
        new("蓝色", AnnotationColor.FromRgb(0x0A, 0x84, 0xFF)),
        new("紫色", AnnotationColor.FromRgb(0xAF, 0x52, 0xDE)),
        new("白色", AnnotationColor.FromRgb(0xFF, 0xFF, 0xFF)),
        new("黑色", AnnotationColor.FromRgb(0x00, 0x00, 0x00)),
    ];

    public static AnnotationColor DefaultColor => Choices[0].Color;
}

/// <summary>
/// 把鼠标拖动/点击换算成可提交的标注。
/// 这里集中处理反向拖动、越界、空文本与小尺寸误触，UI 只负责收集输入。
/// </summary>
public static class PinAnnotationFactory
{
    /// <summary>
    /// 由一次拖动创建形状。反向拖动会自动归一化；越界坐标夹取到图片内；
    /// 小于 <paramref name="minSize"/> 时返回 null。
    /// </summary>
    public static ShapeAnnotation? CreateShape(
        PinShapeKind kind,
        double startX,
        double startY,
        double endX,
        double endY,
        AnnotationColor color,
        double thickness,
        double imageWidth,
        double imageHeight,
        double minSize = PinAnnotationLimits.MinShapeSize)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return null;

        var left = Math.Clamp(Math.Min(startX, endX), 0, imageWidth);
        var top = Math.Clamp(Math.Min(startY, endY), 0, imageHeight);
        var right = Math.Clamp(Math.Max(startX, endX), 0, imageWidth);
        var bottom = Math.Clamp(Math.Max(startY, endY), 0, imageHeight);

        var width = right - left;
        var height = bottom - top;
        if (width < minSize || height < minSize) return null;

        return new ShapeAnnotation(
            kind, left, top, width, height, color, PinAnnotationLimits.ClampThickness(thickness));
    }

    /// <summary>
    /// 创建文字标注。空白文本返回 null；起点与最大宽度夹取到图片内。
    /// </summary>
    public static TextAnnotation? CreateText(
        double x,
        double y,
        string? text,
        AnnotationColor color,
        double fontSize,
        double maxWidth,
        double imageWidth,
        double imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var left = Math.Clamp(x, 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp(y, 0, Math.Max(0, imageHeight - 1));

        var available = Math.Max(1, imageWidth - left);
        var width = Math.Clamp(maxWidth, 1, available);
        if (available >= PinAnnotationLimits.MinTextWidth)
            width = Math.Max(PinAnnotationLimits.MinTextWidth, width);

        return new TextAnnotation(
            left,
            top,
            text.Trim(),
            color,
            PinAnnotationLimits.ClampFontSize(fontSize),
            width);
    }

    /// <summary>
    /// 创建箭头标注。起点与终点都夹取到图片内，保留从起点到终点的方向；
    /// 长度小于 <paramref name="minLength"/> 时返回 null。
    /// </summary>
    public static ArrowAnnotation? CreateArrow(
        double startX,
        double startY,
        double endX,
        double endY,
        AnnotationColor color,
        double thickness,
        double imageWidth,
        double imageHeight,
        double minLength = PinAnnotationLimits.MinArrowLength)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return null;

        var sx = Math.Clamp(startX, 0, imageWidth);
        var sy = Math.Clamp(startY, 0, imageHeight);
        var ex = Math.Clamp(endX, 0, imageWidth);
        var ey = Math.Clamp(endY, 0, imageHeight);

        var dx = ex - sx;
        var dy = ey - sy;
        if (Math.Sqrt(dx * dx + dy * dy) < minLength) return null;

        return new ArrowAnnotation(
            sx, sy, ex, ey, color, PinAnnotationLimits.ClampThickness(thickness));
    }
}
