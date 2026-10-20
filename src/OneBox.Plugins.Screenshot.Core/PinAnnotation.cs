namespace OneBox.Plugins.Screenshot.Core;

/// <summary>贴图标注的形状类型。</summary>
public enum PinShapeKind
{
    Rectangle,
    Ellipse,
}

/// <summary>
/// 标注颜色（ARGB）。Core 是纯逻辑工程（net8.0，不引用 WPF），
/// 因此不使用 System.Windows.Media.Color，只保存四个字节。
/// </summary>
public readonly record struct AnnotationColor(byte A, byte R, byte G, byte B)
{
    public static AnnotationColor FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    public static AnnotationColor FromArgb(uint argb) => new(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>形如 #AARRGGBB，便于日志与测试断言。</summary>
    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>贴图上的一条标注。坐标全部使用原图像素坐标系。</summary>
public abstract record PinAnnotation;

/// <summary>矩形或椭圆边框标注。Width/Height 始终为正。</summary>
public sealed record ShapeAnnotation(
    PinShapeKind Kind,
    double X,
    double Y,
    double Width,
    double Height,
    AnnotationColor Color,
    double Thickness) : PinAnnotation;

/// <summary>文字标注。MaxWidth 是原图像素坐标下的自动换行宽度。</summary>
public sealed record TextAnnotation(
    double X,
    double Y,
    string Text,
    AnnotationColor Color,
    double FontSize,
    double MaxWidth) : PinAnnotation;

/// <summary>
/// 箭头标注。Start 是起点，End 是箭头尖端；方向由起点指向终点，不做归一化。
/// </summary>
public sealed record ArrowAnnotation(
    double StartX,
    double StartY,
    double EndX,
    double EndY,
    AnnotationColor Color,
    double Thickness) : PinAnnotation;
