namespace ClipboardFloat.Core;

/// <summary>文本区间（起始下标 + 长度）。</summary>
public readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
    public bool IsEmpty => Length <= 0;
}

/// <summary>一次智能选中的结果：区间 + 语义标签（用于界面提示，如“字符串值”“属性对”）。</summary>
public sealed record TextSelection(TextRange Range, string Label);

/// <summary>
/// “同段文本”智能选中：把光标处的文本向外扩张成有意义的片段，替代 WPF 默认的“按词选中”。
/// 候选链（由内到外）：字符串内容 → 词元/数值 → "key": value 属性对 → 整行
/// → 逐层括号块（内容 / 含括号）→ 全文。level 越大选得越大。
/// </summary>
public static class SmartTextSelector
{
    private const string OpenBrackets = "{[(";
    private const string CloseBrackets = "}])";

    /// <summary>按层级选中；level 超出候选数量时自动取最大的一层。</summary>
    public static TextSelection? Select(string? text, int index, int level)
    {
        var all = BuildCandidates(text, index);
        if (all.Count == 0) return null;
        return all[Math.Clamp(level, 0, all.Count - 1)];
    }

    /// <summary>列出光标处由内到外的所有候选片段。</summary>
    public static IReadOnlyList<TextSelection> BuildCandidates(string? text, int index)
    {
        var list = new List<TextSelection>();
        if (string.IsNullOrEmpty(text)) return list;

        if (index < 0) index = 0;
        if (index >= text.Length) index = text.Length - 1;

        // 1) 引号内的字符串内容（JSON/CSV 最常用）；不在字符串里时退回词元/数值
        var quoted = QuotedContentAt(text, index);
        if (quoted is { } q && q.Length > 0)
            Add(list, new TextSelection(q, "字符串值"));
        else if (TokenAt(text, index) is { } tk && tk.Length > 0)
            Add(list, new TextSelection(tk, "词元"));

        // 2) "key": value 属性对（不含结尾逗号）
        if (EntryAt(text, index) is { } entry && entry.Length > 0)
            Add(list, new TextSelection(entry, "属性对"));

        // 3) 整行（去掉首尾空白，复制出来不带缩进）
        var line = Trimmed(LineAt(text, index), text);
        Add(list, new TextSelection(line, "整行"));

        // 4) 逐层括号块：先内容后含括号
        foreach (var block in BlocksAt(text, index))
        {
            var content = Trimmed(new TextRange(block.Start + 1, block.Length - 2), text);
            Add(list, new TextSelection(content, "区块内容"));
            Add(list, new TextSelection(block, "区块"));
        }

        // 5) 全文
        Add(list, new TextSelection(Trimmed(new TextRange(0, text.Length), text), "全文"));

        return KeepGrowing(list);
    }

    /// <summary>
    /// 丢掉被更早（更小）候选完全包含的项，保证候选链逐层只增不减——
    /// 否则单行嵌套 JSON 会出现“整行(外层对象) → 内层对象”这种忽大忽小的层级。
    /// </summary>
    private static List<TextSelection> KeepGrowing(List<TextSelection> candidates)
    {
        var result = new List<TextSelection>();
        foreach (var candidate in candidates)
        {
            bool covered = false;
            foreach (var kept in result)
            {
                if (kept.Range.Start <= candidate.Range.Start && candidate.Range.End <= kept.Range.End)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) result.Add(candidate);
        }
        return result;
    }

    private static void Add(List<TextSelection> list, TextSelection selection)
    {
        if (selection.Range.Length <= 0) return;
        foreach (var existing in list)
            if (existing.Range == selection.Range) return; // 区间重复则不重复入链
        list.Add(selection);
    }

    // ---- 引号字符串 ----

    /// <summary>光标处的双引号字符串内容区间（不含引号）。JSON 字符串不跨行，故按行扫描即可。</summary>
    private static TextRange? QuotedContentAt(string text, int index)
    {
        int lineStart = LineStart(text, index);
        int lineEnd = LineEnd(text, index);
        bool inString = false;
        int open = -1;

        for (int i = lineStart; i < lineEnd; i++)
        {
            char c = text[i];
            if (inString && c == '\\') { i++; continue; } // 跳过转义字符
            if (c != '"') continue;

            if (!inString)
            {
                inString = true;
                open = i;
            }
            else
            {
                if (index > open && index <= i)
                    return new TextRange(open + 1, i - open - 1);
                inString = false;
                open = -1;
            }
        }

        // 引号未闭合：光标在开引号之后时，取到行尾
        if (inString && index > open)
            return new TextRange(open + 1, Math.Max(0, lineEnd - open - 1));
        return null;
    }

    // ---- 词元 ----

    private static TextRange? TokenAt(string text, int index)
    {
        if (IsTokenDelimiter(text[index])) return null;
        int s = index;
        while (s > 0 && !IsTokenDelimiter(text[s - 1])) s--;
        int e = index;
        while (e < text.Length && !IsTokenDelimiter(text[e])) e++;
        return e > s ? new TextRange(s, e - s) : null;
    }

    /// <summary>词元分隔符：空白与括号引号逗号分号；冒号不算（URL、时间、键值都靠它连在一起）。</summary>
    private static bool IsTokenDelimiter(char c)
        => char.IsWhiteSpace(c) || c is ',' or ';' or '{' or '}' or '[' or ']' or '(' or ')' or '"' or '\'';

    // ---- "key": value 属性对 ----

    /// <summary>
    /// 光标所在行的 key: value 条目（光标落在 key 或 value 上都算），
    /// 形如 <c>"menuCode": "TMS::BaseInfo:Line"</c>，不含结尾逗号；嵌套时取最内层。
    /// </summary>
    private static TextRange? EntryAt(string text, int index)
    {
        int lineStart = LineStart(text, index);
        int lineEnd = LineEnd(text, index);
        TextRange? best = null;

        for (int i = lineStart; i < lineEnd; i++)
        {
            if (text[i] != ':') continue;
            if (IsInsideQuoted(text, i, lineStart, lineEnd)) continue; // 跳过字符串里的冒号，如 "http://a"

            var key = KeyBefore(text, i, lineStart);
            var value = ValueAfter(text, i + 1, lineEnd);
            if (key is not { } k || value is not { } v) continue;

            var range = new TextRange(k.Start, v.End - k.Start);
            if (index < range.Start || index > range.End) continue;
            // 同一行可能有多层 key: value（如 {"a": {"b": 1}}），取起点最靠右（最内层）的那个
            if (best is null || range.Start > best.Value.Start) best = range;
        }
        return best;
    }

    /// <summary>冒号左侧的键：带引号则含引号，否则取到分隔符为止。</summary>
    private static TextRange? KeyBefore(string text, int colon, int lineStart)
    {
        int i = colon - 1;
        while (i >= lineStart && char.IsWhiteSpace(text[i])) i--;
        if (i < lineStart) return null;

        if (text[i] == '"')
        {
            int open = i - 1;
            while (open >= lineStart && !(text[open] == '"' && !IsEscaped(text, open))) open--;
            if (open < lineStart) return null;
            return new TextRange(open, colon - open);
        }

        int end = i;
        while (i >= lineStart && !IsTokenDelimiter(text[i]) && text[i] != ':') i--;
        int start = i + 1;
        while (start <= end && char.IsWhiteSpace(text[start])) start++;
        return end >= start ? new TextRange(start, end - start + 1) : null;
    }

    /// <summary>冒号右侧的值：字符串含引号，对象/数组做括号配对，其余取到逗号或行尾。</summary>
    private static TextRange? ValueAfter(string text, int from, int lineEnd)
    {
        int i = from;
        while (i < lineEnd && char.IsWhiteSpace(text[i])) i++;
        if (i >= lineEnd) return null;

        int end;
        if (text[i] == '"')
        {
            int j = i + 1;
            while (j < lineEnd && !(text[j] == '"' && !IsEscaped(text, j))) j++;
            end = j < lineEnd ? j + 1 : lineEnd; // 未闭合则取到行尾
        }
        else if (OpenBrackets.Contains(text[i]))
        {
            int close = FindMatchingBracket(text, i);
            end = close >= 0 ? close + 1 : lineEnd;
        }
        else
        {
            int j = i;
            while (j < lineEnd && !char.IsWhiteSpace(text[j]) && text[j] != ',' && !CloseBrackets.Contains(text[j])) j++;
            end = j;
        }

        return end > i ? new TextRange(i, end - i) : null;
    }

    // ---- 括号块 ----

    /// <summary>光标所在的括号块，从最内层到最外层。</summary>
    private static IEnumerable<TextRange> BlocksAt(string text, int index)
    {
        var pairs = BracketPairs(text);
        return pairs
            .Where(p => p.Start <= index && index < p.End)
            .OrderByDescending(p => p.Start);
    }

    /// <summary>扫描文本得到所有配对括号区间（忽略字符串内的括号；JSON 字符串不跨行）。</summary>
    private static List<TextRange> BracketPairs(string text)
    {
        var result = new List<TextRange>();
        var stack = new Stack<(char Ch, int Pos)>();
        bool inString = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') { inString = false; continue; }
            if (inString && c == '\\') { i++; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (OpenBrackets.Contains(c)) stack.Push((c, i));
            else if (CloseBrackets.Contains(c) && stack.Count > 0 && Matches(stack.Peek().Ch, c))
            {
                var open = stack.Pop();
                result.Add(new TextRange(open.Pos, i - open.Pos + 1));
            }
        }
        return result;
    }

    private static int FindMatchingBracket(string text, int openPos)
    {
        foreach (var pair in BracketPairs(text))
            if (pair.Start == openPos) return pair.End - 1;
        return -1;
    }

    private static bool Matches(char open, char close)
        => OpenBrackets.IndexOf(open) == CloseBrackets.IndexOf(close);

    // ---- 行与工具 ----

    private static int LineStart(string text, int index)
    {
        int i = text.LastIndexOf('\n', Math.Min(index, text.Length - 1));
        return i < 0 ? 0 : i + 1;
    }

    private static int LineEnd(string text, int index)
    {
        int i = text.IndexOf('\n', index);
        return i < 0 ? text.Length : i;
    }

    private static TextRange LineAt(string text, int index)
    {
        int start = LineStart(text, index);
        return new TextRange(start, LineEnd(text, index) - start);
    }

    private static TextRange Trimmed(TextRange range, string text)
    {
        int s = Math.Max(0, range.Start);
        int e = Math.Min(text.Length, range.End);
        while (s < e && char.IsWhiteSpace(text[s])) s++;
        while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
        return new TextRange(s, e - s);
    }

    /// <summary>下标是否位于行内某个双引号字符串中（用于跳过字符串里的冒号）。</summary>
    private static bool IsInsideQuoted(string text, int index, int lineStart, int lineEnd)
    {
        bool inString = false;
        for (int i = lineStart; i < index && i < lineEnd; i++)
        {
            if (inString && text[i] == '\\') { i++; continue; }
            if (text[i] == '"') inString = !inString;
        }
        return inString;
    }

    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }
}
