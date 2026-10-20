namespace OneBox.Plugins.Screenshot.Core;

/// <summary>一次追加帧的结果；旧成员顺序保持不变。</summary>
public enum ScrollAppendStatus
{
    First,
    Appended,
    NoChange,
    NoOverlap,
    LimitReached,
    /// <summary>存在多个可信位移，不能安全拼接。</summary>
    Ambiguous,
    /// <summary>缺少可确认配准的纹理，不能把白底相似当作重叠。</summary>
    InsufficientTexture,
    /// <summary>检测到不随内容滚动的头尾，请重新裁选以排除固定区域。</summary>
    FixedContent,
    /// <summary>继续接收会超出像素内存预算；已接受内容仍可导出。</summary>
    MemoryLimitReached,
}

public readonly record struct ScrollAppendResult(
    ScrollAppendStatus Status, int AppendedRows, int OverlapRows)
{
    public bool ShouldContinue => Status == ScrollAppendStatus.First || Status == ScrollAppendStatus.Appended;
}

/// <summary>
/// 向下滚动截图拼接器。配准仅针对相邻已接受原帧，组装追加确认重叠区之后的原始 BGRA。
/// 两帧确认的极薄底边框由新帧对应原始重叠行覆盖；不修改原帧基准、不缩放、不改变行数。
/// 不根据已累积图、相似统计或“内容新颖度”推测位移。失败不会推进原帧基准或已接受帧计数。
/// 固定头尾、横向滚动、缩放、无法区分的重复纹理不受支持，应重新裁选/捕获。
/// </summary>
public sealed class ScrollStitcher
{
    public const long DefaultMemoryBudgetBytes = 256L * 1024 * 1024;
    private readonly int _width;
    private readonly int _heightLimit;
    private readonly int _minMatchRows;
    private readonly long _memoryBudgetBytes;
    private readonly List<byte[]> _chunks = [];
    private long _resultBytes;
    private byte[]? _previous;
    private int _previousHeight;

    /// <summary>兼容原构造函数，使用 256 MiB 内存预算。</summary>
    public ScrollStitcher(int width, int heightLimit, int minMatchRows = 24)
        : this(width, heightLimit, minMatchRows, DefaultMemoryBudgetBytes) { }

    /// <summary>
    /// 显式设置内存预算。预算涵盖持有结果、一次导出、前后原帧及配准特征和保守工作余量；
    /// 不包含调用者持有的输入与已返回的导出数组。超限在任何内容/帧基准变更前拒绝。
    /// </summary>
    public ScrollStitcher(int width, int heightLimit, int minMatchRows, long memoryBudgetBytes)
    {
        if (width <= 0 || width > int.MaxValue / 4) throw new ArgumentOutOfRangeException(nameof(width));
        if (memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        _width = width;
        _heightLimit = Math.Max(heightLimit, 1);
        _minMatchRows = Math.Max(minMatchRows, 8);
        _memoryBudgetBytes = memoryBudgetBytes;
    }

    public int TotalHeight { get; private set; }
    public int FrameCount { get; private set; }
    /// <summary>上次实际追加的行数；失败/无变化时为零。</summary>
    public int LastScrollDelta { get; private set; }
    public int LastMatchedRows { get; private set; }
    public int LastRequiredRows { get; private set; }
    /// <summary>确认配准的新帧顶部在累积图中的位置，未确认时为 -1。</summary>
    public int LastBestPosition { get; private set; } = -1;
    /// <summary>最近一次 Add 的纯标量诊断；首次 Add 前为 null。</summary>
    public ScrollMatchDiagnostics? LastDiagnostics { get; private set; }
    public int Stride => _width * 4;

    public ScrollAppendResult Add(byte[] pixels, int height, int bytesPerRow)
    {
        LastDiagnostics = new ScrollMatchDiagnostics { Stage = "Input", Status = ScrollAppendStatus.NoOverlap.ToString() };
        LastScrollDelta = LastMatchedRows = LastRequiredRows = 0;
        LastBestPosition = -1;
        if (pixels is null || height <= 0 || bytesPerRow < Stride
            || (long)height * bytesPerRow > pixels.LongLength)
            return new(ScrollAppendStatus.NoOverlap, 0, 0);
        if (TotalHeight >= _heightLimit)
        {
            LastDiagnostics = LastDiagnostics with { Stage = "HeightLimit", Status = ScrollAppendStatus.LimitReached.ToString() };
            return new(ScrollAppendStatus.LimitReached, 0, 0);
        }

        var frameBytes = (long)Stride * height;
        // 预留两个特征数组和工作区。所有乘法先提升为 long，拒绝不可导出的数组。
        var workspace = (long)(height + (long)_previousHeight) * (Math.Min(_width, 96) * 2L + _width) + 16384;
        if (frameBytes > int.MaxValue || !Fits(_resultBytes, frameBytes, workspace))
        {
            LastDiagnostics = LastDiagnostics with { Stage = "MemoryBudget", Status = ScrollAppendStatus.MemoryLimitReached.ToString() };
            return new(ScrollAppendStatus.MemoryLimitReached, 0, 0);
        }
        var frame = new byte[(int)frameBytes];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * bytesPerRow, frame, y * Stride, Stride);

        var overlap = 0;
        var border = 0;
        if (_previous is not null)
        {
            var match = ScrollFrameRegistration.Register(_previous, _previousHeight, frame,
                height, _width, _minMatchRows);
            LastDiagnostics = match.Diagnostics;
            if (match.Status != ScrollAppendStatus.Appended)
                return new(match.Status, 0, match.Overlap);
            overlap = match.Overlap;
            LastMatchedRows = match.MatchedRows;
            border = match.BottomBorderRows;
            LastRequiredRows = overlap - border;
            LastBestPosition = TotalHeight - overlap;
        }

        var newRows = height - overlap;
        var take = Math.Min(newRows, _heightLimit - TotalHeight);
        var nextBytes = _resultBytes + (long)take * Stride;
        if (nextBytes > int.MaxValue || !Fits(nextBytes, frameBytes, workspace))
        {
            LastDiagnostics = (LastDiagnostics ?? new ScrollMatchDiagnostics()) with
                { Stage = "MemoryBudget", Status = ScrollAppendStatus.MemoryLimitReached.ToString(), OverlapRows = overlap };
            return new(ScrollAppendStatus.MemoryLimitReached, 0, overlap);
        }
        var chunk = frame.AsSpan(overlap * Stride, take * Stride).ToArray();
        var first = _previous is null;
        _chunks.EnsureCapacity(_chunks.Count + 1); // Allocate before mutating any accepted bytes.
        // Repair only the confirmed old viewport border with corresponding raw rows from the new
        // overlap. Do this after all rejection/allocation checks; failures never mutate the prefix.
        var remaining = border * Stride;
        var sourceEnd = overlap * Stride;
        for (var i = _chunks.Count - 1; remaining > 0 && i >= 0; i--)
        {
            var target = _chunks[i];
            var count = Math.Min(remaining, target.Length);
            frame.AsSpan(sourceEnd - count, count).CopyTo(target.AsSpan(target.Length - count));
            sourceEnd -= count;
            remaining -= count;
        }
        _chunks.Add(chunk);
        _resultBytes = nextBytes;
        TotalHeight += take;
        FrameCount++;
        _previous = frame;
        _previousHeight = height;
        LastScrollDelta = first ? 0 : take;
        // 达到上限（包括第一帧）明确告知停止，而不是再捕获一帧才发现。
        var status = TotalHeight >= _heightLimit ? ScrollAppendStatus.LimitReached
            : first ? ScrollAppendStatus.First : ScrollAppendStatus.Appended;
        LastDiagnostics = (LastDiagnostics ?? new ScrollMatchDiagnostics()) with
            { Stage = first ? "First" : "Matched", Status = status.ToString(), OverlapRows = overlap };
        return new(status, take, overlap);
    }

    private bool Fits(long resultBytes, long frameBytes, long workspace) =>
        // resultBytes 的第二份是导出预留；旧基准在换帧成功前仍存在。
        resultBytes * 2 + frameBytes + (_previous?.LongLength ?? 0) + workspace
            + ((long)_chunks.Count + 1) * 64 <= _memoryBudgetBytes;

    /// <summary>按原样导出紧凑 BGRA；不含输入行尾 padding。</summary>
    public byte[] BuildBgra()
    {
        var buffer = new byte[(int)_resultBytes];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            Buffer.BlockCopy(chunk, 0, buffer, offset, chunk.Length);
            offset += chunk.Length;
        }
        return buffer;
    }
}
