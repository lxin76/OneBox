namespace OneBox.Plugins.Screenshot.Core;

/// <summary>OCR 识别出的一个词，以及它在原图里的像素框。</summary>
/// <param name="Text">识别出的文字。中文场景下常常是单个字。</param>
/// <param name="Bounds">在原图像素坐标系里的位置。</param>
public sealed record OcrWord(string Text, OcrRect Bounds);

/// <summary>
/// 版面中的一行：若干按 x 排好序的词，外加一份"字符槽"表。
///
/// 之所以要有字符槽，是因为 OCR 只给整词的框，而用户想按字符选。
/// 这里把每个词的框按字符数均分，得到每个字符的 x 区间；词与词之间
/// 足够大的空隙插入一个空格槽。命中测试和高亮都读同一张表，
/// 因此"点哪里"和"哪里亮"永远自洽 —— 不依赖任何文本引擎的排版。
/// </summary>
public sealed class OcrLine
{
    private readonly record struct Slot(char Ch, double X0, double X1, int WordIndex);

    private readonly Slot[] _slots;
    private readonly (int Start, int End)[] _wordRanges;

    /// <summary>本行的词，按 x 升序。</summary>
    public IReadOnlyList<OcrWord> Words { get; }

    /// <summary>本行的文本，词间按需插入单个空格。</summary>
    public string Text { get; }

    /// <summary>本行的外接框（各词的并集）。</summary>
    public OcrRect Bounds { get; }

    public double Height => Bounds.Height;

    /// <param name="words">本行的词，顺序无所谓，构造时会按 x 排序。</param>
    /// <param name="spaceGapRatio">
    /// 词间空隙超过"行高 × 该比例"就插入一个空格。中文 OCR 会把
    /// "截图"拆成两个词但间隙很小，阈值取小了会到处插空格。
    /// </param>
    public OcrLine(IReadOnlyList<OcrWord> words, double spaceGapRatio = 0.22)
    {
        var ordered = words
            .Where(w => w is not null && !string.IsNullOrEmpty(w.Text) && !w.Bounds.IsEmpty)
            .OrderBy(w => w.Bounds.X)
            .ToList();

        Words = ordered;

        if (ordered.Count == 0)
        {
            Text = "";
            Bounds = default;
            _slots = [];
            _wordRanges = [];
            return;
        }

        var bounds = ordered[0].Bounds;
        foreach (var w in ordered.Skip(1)) bounds = bounds.Union(w.Bounds);
        Bounds = bounds;

        var gapThreshold = Math.Max(1.0, bounds.Height * spaceGapRatio);
        var builder = new System.Text.StringBuilder();
        var slots = new List<Slot>();
        var ranges = new List<(int, int)>();
        var prevRight = double.NaN;

        for (int wi = 0; wi < ordered.Count; wi++)
        {
            var word = ordered[wi];

            if (!double.IsNaN(prevRight))
            {
                var gap = word.Bounds.X - prevRight;
                if (gap > gapThreshold)
                {
                    builder.Append(' ');
                    slots.Add(new Slot(' ', prevRight, word.Bounds.X, -1));
                }
            }

            var wordStart = builder.Length;
            var count = word.Text.Length;
            for (int i = 0; i < count; i++)
            {
                var x0 = word.Bounds.X + word.Bounds.Width * i / count;
                var x1 = word.Bounds.X + word.Bounds.Width * (i + 1) / count;
                builder.Append(word.Text[i]);
                slots.Add(new Slot(word.Text[i], x0, x1, wi));
            }
            ranges.Add((wordStart, builder.Length));

            prevRight = word.Bounds.Right;
        }

        Text = builder.ToString();
        _slots = slots.ToArray();
        _wordRanges = ranges.ToArray();
    }

    /// <summary>
    /// 给定横向坐标，算出最接近的字符偏移（0 到 Text.Length）。
    /// 落在字符左半边取该字符之前，右半边取之后。
    /// </summary>
    public int OffsetAt(double x)
    {
        if (_slots.Length == 0) return 0;

        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            if (x < slot.X0) return i;
            if (x <= slot.X1)
                return x < (slot.X0 + slot.X1) / 2 ? i : i + 1;
        }
        return _slots.Length;
    }

    /// <summary>给定字符偏移，算出它的插入点 x 坐标（用于画光标或高亮边界）。</summary>
    public double OffsetX(int offset)
    {
        if (_slots.Length == 0) return Bounds.X;
        if (offset <= 0) return _slots[0].X0;
        if (offset >= _slots.Length) return _slots[^1].X1;
        return _slots[offset].X0;
    }

    /// <summary>把偏移吸附到最近的词边界，用于"整词选择"（双击）。</summary>
    public int SnapToWordBoundary(int offset)
    {
        if (_wordRanges.Length == 0) return 0;

        var best = 0;
        var bestDistance = int.MaxValue;
        foreach (var (start, end) in _wordRanges)
        {
            foreach (var boundary in stackalloc[] { start, end })
            {
                var distance = Math.Abs(boundary - offset);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = boundary;
                }
            }
        }
        return best;
    }

    /// <summary>返回包含指定偏移的那个词的范围（用于双击选词）。</summary>
    public (int Start, int End) WordRangeAt(int offset)
    {
        foreach (var range in _wordRanges)
        {
            if (offset >= range.Start && offset <= range.End) return range;
        }
        return _wordRanges.Length > 0 ? _wordRanges[^1] : (0, 0);
    }

    /// <summary>本行末端的字符偏移，等价于 Text.Length。</summary>
    public int Length => Text.Length;

    public override string ToString() => $"{Words.Count} 词 / {Text.Length} 字符";
}
