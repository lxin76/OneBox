namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 文本插入点：第 <paramref name="Line"/> 行的第 <paramref name="Offset"/> 个字符之前。
/// 偏移量允许等于该行长度，表示行尾。
/// </summary>
public readonly record struct OcrCaret(int Line, int Offset) : IComparable<OcrCaret>
{
    public int CompareTo(OcrCaret other)
        => Line != other.Line ? Line.CompareTo(other.Line) : Offset.CompareTo(other.Offset);

    public static bool operator <(OcrCaret a, OcrCaret b) => a.CompareTo(b) < 0;
    public static bool operator >(OcrCaret a, OcrCaret b) => a.CompareTo(b) > 0;
    public static bool operator <=(OcrCaret a, OcrCaret b) => a.CompareTo(b) <= 0;
    public static bool operator >=(OcrCaret a, OcrCaret b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// 一段选区。Anchor 是按下鼠标的位置，Focus 是当前鼠标位置；
/// 两者顺序可以颠倒（往上/往左拖），<see cref="Start"/> 与 <see cref="End"/>
/// 是规范化后的阅读顺序。
/// </summary>
public readonly record struct OcrSelection(OcrCaret Anchor, OcrCaret Focus)
{
    public OcrCaret Start => Anchor.CompareTo(Focus) <= 0 ? Anchor : Focus;

    public OcrCaret End => Anchor.CompareTo(Focus) <= 0 ? Focus : Anchor;

    public bool IsEmpty => Anchor.Equals(Focus);

    public static OcrSelection At(OcrCaret caret) => new(caret, caret);
}
