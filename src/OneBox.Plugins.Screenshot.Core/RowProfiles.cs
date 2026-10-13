namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 一行的横向剖面：每块的平均亮度与标准差。
/// 不做量化，保留完整数值，供滚动拼接做带容差的匹配。
/// 量化后的精确比对对浏览器平滑滚动完全无效——亚像素位移会让每一行都不相等。
/// </summary>
public readonly struct RowProfile
{
    /// <summary>每块的平均亮度（0-255）。</summary>
    public byte[] Means { get; init; }

    /// <summary>每块的标准差（0-255），反映块内是否有文字轮廓等细节。</summary>
    public byte[] Variances { get; init; }

    /// <summary>块数。</summary>
    public int BlockCount => Means.Length;
}

/// <summary>
/// 行剖面计算与带容差的比较。纯逻辑，便于单元测试。
/// </summary>
public static class RowProfiles
{
    /// <summary>横向分块数。块数越多横向分辨力越强，但也越容易被轻微错位干扰。</summary>
    public const int DefaultBlocks = 8;

    /// <summary>
    /// 计算每行的剖面。<paramref name="pixels"/> 为 BGRA 排列的像素数据。
    /// </summary>
    public static RowProfile[] Compute(
        ReadOnlySpan<byte> pixels, int width, int height, int bytesPerRow, int blocks = DefaultBlocks)
    {
        if (width <= 0 || height <= 0) return [];
        if (blocks <= 0) blocks = DefaultBlocks;

        var result = new RowProfile[height];
        for (int y = 0; y < height; y++)
        {
            var rowStart = y * bytesPerRow;
            var means = new byte[blocks];
            var variances = new byte[blocks];

            for (int b = 0; b < blocks; b++)
            {
                int x0 = (int)((long)b * width / blocks);
                int x1 = (int)((long)(b + 1) * width / blocks);
                if (x1 <= x0) x1 = Math.Min(width, x0 + 1);

                // 每块最多采样 64 个像素，长行也不会拖慢速度
                int step = Math.Max(1, (x1 - x0) / 64);
                long sum = 0;
                long sumSquares = 0;
                int count = 0;
                for (int x = x0; x < x1; x += step)
                {
                    var offset = rowStart + x * 4;
                    if (offset + 2 >= pixels.Length) break;
                    var luminance = (pixels[offset + 2] * 77 + pixels[offset + 1] * 151 + pixels[offset] * 28) >> 8;
                    sum += luminance;
                    sumSquares += (long)luminance * luminance;
                    count++;
                }

                if (count == 0) continue;

                var average = (int)(sum / count);
                var variance = Math.Max(0, sumSquares / count - (long)average * average);
                means[b] = (byte)Math.Clamp(average, 0, 255);
                variances[b] = (byte)Math.Clamp((int)Math.Sqrt(variance), 0, 255);
            }

            result[y] = new RowProfile { Means = means, Variances = variances };
        }
        return result;
    }

    /// <summary>
    /// 两行的差异：平均亮度差 + 标准差差的一半（标准差噪声更大，权重低一些）。
    /// 返回 0 表示几乎相同。
    /// </summary>
    public static int Distance(in RowProfile a, in RowProfile b)
    {
        var count = Math.Min(a.BlockCount, b.BlockCount);
        if (count == 0) return int.MaxValue;

        var total = 0;
        for (int i = 0; i < count; i++)
        {
            total += Math.Abs(a.Means[i] - b.Means[i]);
            total += Math.Abs(a.Variances[i] - b.Variances[i]) / 2;
        }
        return total / count;
    }

    /// <summary>两行是否可视为同一行（容差内）。</summary>
    public static bool IsSimilar(in RowProfile a, in RowProfile b, int tolerance = 12)
        => Distance(a, b) <= tolerance;

    /// <summary>某段行里是否有足够的变化，否则无法判断滚动位置（例如整屏纯白）。</summary>
    public static bool HasVariance(RowProfile[] profiles, int start, int count, int minSpread = 6)
    {
        if (profiles.Length == 0 || count <= 1) return false;
        start = Math.Clamp(start, 0, profiles.Length - 1);
        count = Math.Min(count, profiles.Length - start);
        if (count <= 1) return false;

        var min = int.MaxValue;
        var max = int.MinValue;
        for (int i = 0; i < count; i++)
        {
            if (profiles[start + i].BlockCount == 0) continue;
            var first = profiles[start + i].Means[0];
            if (first < min) min = first;
            if (first > max) max = first;
        }
        return max - min >= minSpread;
    }
}
