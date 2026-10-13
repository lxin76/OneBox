namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 一张图上的 OCR 版面：把词框聚成行，并提供"像文档一样划选"所需的全部几何计算。
///
/// 设计要点：命中测试、选区文本、高亮矩形三者读的是同一套 <see cref="OcrLine"/>
/// 字符槽坐标，因此"点哪里"和"哪里亮"天然一致，不需要让任何文本引擎的排版
/// 去迁就 OCR 坐标（WPF 的 FlowDocument 做不到这点，实测会逐行累积漂移）。
///
/// 纯逻辑，不依赖 WPF，可完整单元测试。
/// </summary>
public sealed class OcrLayout
{
    /// <summary>
    /// 把词聚成行时，两词中心 y 的允许偏差系数：
    /// 容差 = max(MinRowTolerance, 词高 × 该系数)。
    /// </summary>
    private const double RowClusterRatio = 0.6;

    private const double MinRowTolerance = 6.0;

    private readonly OcrLine[] _lines;

    /// <summary>版面里的行，按从上到下排序。</summary>
    public IReadOnlyList<OcrLine> Lines => _lines;

    /// <summary>所有词的并集，即文字的总体范围。</summary>
    public OcrRect Bounds { get; }

    /// <summary>整篇文本，行间用 \n 连接。</summary>
    public string Text { get; }

    public bool IsEmpty => _lines.Length == 0;

    private OcrLayout(OcrLine[] lines)
    {
        _lines = lines;
        Text = string.Join("\n", lines.Select(l => l.Text));

        if (lines.Length == 0)
        {
            Bounds = default;
            return;
        }

        var bounds = lines[0].Bounds;
        foreach (var line in lines.Skip(1)) bounds = bounds.Union(line.Bounds);
        Bounds = bounds;
    }

    /// <summary>
    /// 从词框构建版面。
    /// </summary>
    /// <param name="words">OCR 输出的词。可以来自引擎的任意行划分，这里会按几何重新聚类。</param>
    /// <param name="spaceGapRatio">词间空隙超过"行高 × 该比例"时插入空格。</param>
    public static OcrLayout Build(IEnumerable<OcrWord> words, double spaceGapRatio = 0.22)
    {
        var valid = (words ?? [])
            .Where(w => w is not null && !string.IsNullOrEmpty(w.Text) && !w.Bounds.IsEmpty)
            .OrderBy(w => w.Bounds.CenterY)
            .ThenBy(w => w.Bounds.X)
            .ToList();

        if (valid.Count == 0) return new OcrLayout([]);

        // 按 y 中心聚类成行。引擎自己的行划分在渲染文档上并不可靠
        // （实测 21 行被引擎拆成 14 行、几何聚类成 16 行），必须自己算。
        var rows = new List<List<OcrWord>>();
        var rowCenters = new List<double>();

        foreach (var word in valid)
        {
            var center = word.Bounds.CenterY;
            var tolerance = Math.Max(MinRowTolerance, word.Bounds.Height * RowClusterRatio);

            var placed = false;
            for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 3; i--)
            {
                if (Math.Abs(center - rowCenters[i]) > tolerance) continue;

                rows[i].Add(word);
                // 行中心取均值，让行内后续词有更稳的参照
                rowCenters[i] = (rowCenters[i] * (rows[i].Count - 1) + center) / rows[i].Count;
                placed = true;
                break;
            }

            if (placed) continue;

            rows.Add([word]);
            rowCenters.Add(center);
        }

        var lines = rows
            .Select(r => new OcrLine(r, spaceGapRatio))
            .Where(l => l.Words.Count > 0)
            .OrderBy(l => l.Bounds.CenterY)
            .ThenBy(l => l.Bounds.X)
            .ToArray();

        return new OcrLayout(lines);
    }

    /// <summary>
    /// 把一个图片坐标点解析成插入点。
    /// 落在文字行之间的空隙时，取纵向最近的行。
    /// </summary>
    public OcrCaret CaretAt(double x, double y)
    {
        if (_lines.Length == 0) return new OcrCaret(0, 0);

        var lineIndex = NearestLineIndex(y);
        var line = _lines[lineIndex];

        // 横向超出本行范围时，钳到行首或行尾（不跨行，避免拖到空白处跳到别的行）
        var offset = line.OffsetAt(x);
        return new OcrCaret(lineIndex, Math.Clamp(offset, 0, line.Length));
    }

    /// <summary>
    /// 取一段选区对应的文本。
    /// 首行从 Start.Offset 到行尾，中间整行，末行从行首到 End.Offset。
    /// </summary>
    public string GetText(OcrSelection selection)
    {
        if (_lines.Length == 0) return "";

        var start = Clamp(selection.Start);
        var end = Clamp(selection.End);

        if (start.Line == end.Line)
        {
            var line = _lines[start.Line];
            var from = Math.Min(start.Offset, end.Offset);
            var to = Math.Max(start.Offset, end.Offset);
            return line.Text[from..to];
        }

        var builder = new System.Text.StringBuilder();
        for (int i = start.Line; i <= end.Line; i++)
        {
            var line = _lines[i];
            var from = i == start.Line ? start.Offset : 0;
            var to = i == end.Line ? end.Offset : line.Length;
            from = Math.Clamp(from, 0, line.Length);
            to = Math.Clamp(to, 0, line.Length);
            if (to > from) builder.Append(line.Text, from, to - from);
            if (i < end.Line) builder.Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>
    /// 取一段选区需要绘制的高亮矩形。
    /// 每行返回一个矩形：首行从起始字符到行尾，中间整行，末行从行首到结束字符。
    /// 同一行只画一个矩形（把相邻词的框并起来），因此不会出现词间断开的方格。
    /// </summary>
    public IReadOnlyList<OcrRect> GetHighlightRects(OcrSelection selection)
    {
        if (_lines.Length == 0) return [];

        var start = Clamp(selection.Start);
        var end = Clamp(selection.End);
        var result = new List<OcrRect>();

        for (int i = start.Line; i <= end.Line; i++)
        {
            var line = _lines[i];
            var from = i == start.Line ? start.Offset : 0;
            var to = i == end.Line ? end.Offset : line.Length;
            from = Math.Clamp(from, 0, line.Length);
            to = Math.Clamp(to, 0, line.Length);
            if (to <= from) continue;

            var left = line.OffsetX(from);
            var right = line.OffsetX(to);
            if (right <= left) continue;

            result.Add(new OcrRect(left, line.Bounds.Y, right - left, line.Bounds.Height));
        }

        return result;
    }

    /// <summary>双击选词：返回覆盖该插入点所在词的选区。</summary>
    public OcrSelection SelectWordAt(double x, double y)
    {
        if (_lines.Length == 0) return OcrSelection.At(new OcrCaret(0, 0));

        var lineIndex = NearestLineIndex(y);
        var line = _lines[lineIndex];
        var offset = Math.Clamp(line.OffsetAt(x), 0, line.Length);
        var (from, to) = line.WordRangeAt(offset);
        return new OcrSelection(new OcrCaret(lineIndex, from), new OcrCaret(lineIndex, to));
    }

    /// <summary>全选。</summary>
    public OcrSelection SelectAll()
    {
        if (_lines.Length == 0) return OcrSelection.At(new OcrCaret(0, 0));
        var last = _lines.Length - 1;
        return new OcrSelection(new OcrCaret(0, 0), new OcrCaret(last, _lines[last].Length));
    }

    /// <summary>纵向最近的行号。</summary>
    private int NearestLineIndex(double y)
    {
        var best = 0;
        var bestDistance = double.MaxValue;

        for (int i = 0; i < _lines.Length; i++)
        {
            var b = _lines[i].Bounds;
            // 落在行内直接命中；否则算到行的纵向距离
            var distance = y < b.Y ? b.Y - y : y > b.Bottom ? y - b.Bottom : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
            if (distance == 0) break;
        }

        return best;
    }

    /// <summary>把越界的插入点钳回合法范围。</summary>
    private OcrCaret Clamp(OcrCaret caret)
    {
        var line = Math.Clamp(caret.Line, 0, _lines.Length - 1);
        var offset = Math.Clamp(caret.Offset, 0, _lines[line].Length);
        return new OcrCaret(line, offset);
    }
}
