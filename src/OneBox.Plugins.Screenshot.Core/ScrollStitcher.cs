namespace OneBox.Plugins.Screenshot.Core;

/// <summary>一次追加帧的结果。</summary>
public enum ScrollAppendStatus
{
    /// <summary>第一帧，整幅纳入。</summary>
    First,
    /// <summary>找到了重叠区，只追加了新出现的部分。</summary>
    Appended,
    /// <summary>画面与已累积内容完全一致：通常已经滚到底。</summary>
    NoChange,
    /// <summary>找不到重叠区：滚动距离超出单帧高度，或页面内容变化太剧烈。</summary>
    NoOverlap,
    /// <summary>已达到高度上限，停止拼接。</summary>
    LimitReached,
}

/// <summary>追加结果。</summary>
public readonly record struct ScrollAppendResult(
    ScrollAppendStatus Status, int AppendedRows, int OverlapRows)
{
    /// <summary>是否应继续捕获下一帧。</summary>
    public bool ShouldContinue => Status == ScrollAppendStatus.First || Status == ScrollAppendStatus.Appended;
}

/// <summary>
/// 滚动截长图的拼接器：逐帧喂入屏幕像素，靠“锚点行块”在下一帧里定位滚动距离，
/// 只把新出现的行追加到结果里。
///
/// 匹配用的是带容差的行剖面而非量化签名：浏览器/应用的平滑滚动会产生亚像素位移与
/// 重绘抖动，量化后的精确比对会让每一行都失配（实测匹配率常年为 0）。
/// 纯逻辑（不依赖 WPF），便于单元测试。
/// </summary>
public sealed class ScrollStitcher
{
    /// <summary>行相似度容差：平均亮度差的均值低于该值即视为同一行。</summary>
    private const int SimilarityTolerance = 12;

    /// <summary>锚点窗口中允许有多大比例的行不匹配（滚动重绘噪声）。</summary>
    private const double MatchRatio = 0.85;

    private readonly int _width;
    private readonly int _heightLimit;
    private readonly int _minMatchRows;
    private readonly List<byte[]> _rows = [];
    private readonly List<RowProfile> _profiles = [];

    /// <summary>
    /// 构造拼接器。
    /// </summary>
    /// <param name="width">画面宽度（像素）。</param>
    /// <param name="heightLimit">结果高度上限，超过即停止。</param>
    /// <param name="minMatchRows">锚点行数：用于在下一帧中定位滚动距离的最小匹配块高度。</param>
    public ScrollStitcher(int width, int heightLimit, int minMatchRows = 24)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _width = width;
        _heightLimit = Math.Max(heightLimit, 1);
        _minMatchRows = Math.Max(minMatchRows, 4);
    }

    /// <summary>当前累积高度（行数）。</summary>
    public int TotalHeight => _rows.Count;

    /// <summary>已接受的帧数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>每次滚动实际移动的行数（用于给用户显示进度）。</summary>
    public int LastScrollDelta { get; private set; }

    /// <summary>最近一次锚点匹配命中的行数（诊断用）。</summary>
    public int LastMatchedRows { get; private set; }

    /// <summary>最近一次锚点匹配要求的命中行数（诊断用）。</summary>
    public int LastRequiredRows { get; private set; }

    /// <summary>最近一次锚点匹配的最佳起始位置（-1 表示未命中，诊断用）。</summary>
    public int LastBestPosition { get; private set; } = -1;

    /// <summary>
    /// 追加一帧。
    /// </summary>
    /// <param name="pixels">BGRA 像素数据，长度至少为 height * bytesPerRow。</param>
    /// <param name="height">帧高度（行数）。</param>
    /// <param name="bytesPerRow">每行字节数（BGRA 时为 width * 4）。</param>
    public ScrollAppendResult Add(byte[] pixels, int height, int bytesPerRow)
    {
        if (height <= 0 || bytesPerRow < _width * 4)
            return new ScrollAppendResult(ScrollAppendStatus.NoOverlap, 0, 0);
        if (_rows.Count >= _heightLimit)
            return new ScrollAppendResult(ScrollAppendStatus.LimitReached, 0, 0);

        var profiles = RowProfiles.Compute(pixels, _width, height, bytesPerRow);
        if (profiles.Length == 0)
            return new ScrollAppendResult(ScrollAppendStatus.NoOverlap, 0, 0);

        FrameCount++;

        // 第一帧：整幅纳入
        if (_rows.Count == 0)
        {
            var take = Math.Min(height, _heightLimit);
            AppendRows(pixels, bytesPerRow, 0, take, profiles);
            LastScrollDelta = 0;
            LastBestPosition = -1;
            LastMatchedRows = 0;
            LastRequiredRows = 0;
            return new ScrollAppendResult(ScrollAppendStatus.First, take, 0);
        }

        // 锚点 = 已累积内容的最后 minMatchRows 行
        var anchorSize = Math.Min(_minMatchRows, _rows.Count);
        var anchorStart = _rows.Count - anchorSize;

        // 整帧都是纯色（抓取失败、页面全白）时不可能带来新内容，
        // 直接判为“无变化”而不是尝试匹配一个没有区分度的锚点。
        if (!RowProfiles.HasVariance(profiles, 0, profiles.Length))
        {
            LastBestPosition = -1;
            LastMatchedRows = 0;
            LastRequiredRows = 0;
            return new ScrollAppendResult(ScrollAppendStatus.NoChange, 0, height);
        }

        // 锚点本身太“平”（大段纯色）时匹配结果不可靠，宁可停下也不拼错
        if (!RowProfiles.HasVariance(_profiles.ToArray(), anchorStart, anchorSize))
        {
            LastBestPosition = -1;
            LastMatchedRows = 0;
            LastRequiredRows = 0;
            return new ScrollAppendResult(ScrollAppendStatus.NoChange, 0, height);
        }

        if (!TryFindAnchor(profiles, anchorSize, anchorStart, out var position, out _))
            return new ScrollAppendResult(ScrollAppendStatus.NoOverlap, 0, 0);

        // 帧内位置 position 对应当前累积高度的 anchorStart：
        // 于是帧中已存在的行数 = position + anchorSize
        var overlap = position + anchorSize;
        if (overlap >= height)
        {
            // 整帧都是已有内容 → 没有滚动
            LastScrollDelta = 0;
            return new ScrollAppendResult(ScrollAppendStatus.NoChange, 0, height);
        }

        var newRows = height - overlap;
        var room = _heightLimit - _rows.Count;
        var taken = Math.Min(newRows, room);
        AppendRows(pixels, bytesPerRow, overlap, taken, profiles);

        LastScrollDelta = newRows;
        var status = taken < newRows ? ScrollAppendStatus.LimitReached : ScrollAppendStatus.Appended;
        return new ScrollAppendResult(status, taken, overlap);

        // 在候选帧里找锚点：用带容差的行比较，允许少量行因重绘而不一致。
        // 页面里普遍存在重复行（空白行、表格边框），因此不能只看“命中行数”：
        // 优先选总距离最小的位置，平局时取更靠后（滚动量更小）的位置——
        // 这样宁可多留重叠，也不会把没见过的内容当成重复而跳过去。
        bool TryFindAnchor(RowProfile[] frameProfiles, int size, int anchorFrom, out int found, out int matchedRows)
        {
            found = -1;
            matchedRows = 0;

            var maxStart = frameProfiles.Length - size;
            if (maxStart < 0)
            {
                LastRequiredRows = 0;
                LastBestPosition = -1;
                return false;
            }

            var required = (int)Math.Ceiling(size * MatchRatio);
            LastRequiredRows = required;

            var bestMatched = -1;
            var bestDistance = long.MaxValue;
            var bestStart = -1;

            for (int start = 0; start <= maxStart; start++)
            {
                var matches = 0;
                long totalDistance = 0;
                for (int i = 0; i < size; i++)
                {
                    var distance = RowProfiles.Distance(frameProfiles[start + i], _profiles[anchorFrom + i]);
                    totalDistance += distance;
                    if (distance <= SimilarityTolerance) matches++;
                }

                if (matches < required) continue;

                // 命中行数更多者胜出；相同则比总距离；再相同则取更靠后的位置
                var better = matches > bestMatched
                             || (matches == bestMatched && totalDistance < bestDistance)
                             || (matches == bestMatched && totalDistance == bestDistance && start > bestStart);

                if (better)
                {
                    bestMatched = matches;
                    bestDistance = totalDistance;
                    bestStart = start;
                }
            }

            LastBestPosition = bestStart;
            LastMatchedRows = bestMatched < 0 ? 0 : bestMatched;
            found = bestStart;
            matchedRows = LastMatchedRows;
            return found >= 0;
        }
    }

    /// <summary>把累积的行拼成一块连续的 BGRA 缓冲。</summary>
    public byte[] BuildBgra()
    {
        var stride = _width * 4;
        var total = (long)stride * _rows.Count;
        if (total > int.MaxValue) throw new InvalidOperationException("拼接结果过大，无法一次性导出。");

        var buffer = new byte[(int)total];
        for (int i = 0; i < _rows.Count; i++)
            Buffer.BlockCopy(_rows[i], 0, buffer, i * stride, stride);
        return buffer;
    }

    /// <summary>结果每行字节数。</summary>
    public int Stride => _width * 4;

    private void AppendRows(byte[] pixels, int bytesPerRow, int fromRow, int count, RowProfile[] profiles)
    {
        var stride = _width * 4;
        for (int i = 0; i < count; i++)
        {
            var row = new byte[stride];
            var sourceOffset = (fromRow + i) * bytesPerRow;
            if (sourceOffset + stride <= pixels.Length)
                Buffer.BlockCopy(pixels, sourceOffset, row, 0, stride);
            _rows.Add(row);
            _profiles.Add(profiles[fromRow + i]);
        }
    }
}
