namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 图片像素坐标系里的矩形。
/// Core 是纯逻辑工程（net8.0，不引用 WPF），因此不复用 System.Windows.Rect。
/// </summary>
public readonly record struct OcrRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>与另一个矩形是否有重叠（边接触不算）。</summary>
    public bool IntersectsWith(OcrRect other)
        => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>包住两个矩形的最小矩形。</summary>
    public OcrRect Union(OcrRect other)
    {
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(Right, other.Right);
        var bottom = Math.Max(Bottom, other.Bottom);
        return new OcrRect(left, top, right - left, bottom - top);
    }
}
