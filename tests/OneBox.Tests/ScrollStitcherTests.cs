using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

/// <summary>
/// 滚动截长图拼接器的测试。
/// 用“每行一个亮度”的合成画面模拟可滚动页面：行内容由行号决定，
/// 这样滚动后帧与帧之间的重叠区能被精确识别，结果也能逐行断言。
/// </summary>
public class ScrollStitcherTests
{
    private const int Width = 40;

    /// <summary>
    /// 构造一帧：帧内第 i 行显示页面第 (startRow + i) 行的内容。
    /// 行内容用哈希生成而非线性公式：线性公式会产生周期化的行，
    /// 让不相邻的页面区域出现“巧合匹配”，无法验证真实的拼接语义。
    /// </summary>
    private static byte[] BuildFrame(int startRow, int height, int width = Width)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            var pageRow = startRow + y;
            var baseValue = PageRowShade(pageRow);
            for (int x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var value = (byte)Math.Clamp(baseValue + x % 8, 0, 255);
                buffer[offset] = value;       // B
                buffer[offset + 1] = value;   // G
                buffer[offset + 2] = value;   // R
                buffer[offset + 3] = 255;     // A
            }
        }
        return buffer;
    }

    /// <summary>页面某一行的基准亮度：由行号哈希得到，互不相关。</summary>
    private static int PageRowShade(int pageRow)
    {
        unchecked
        {
            var hash = (uint)pageRow * 2654435761u;
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            // 落在 20..235，避免纯黑/纯白带来的量化边界问题
            return 20 + (int)(hash % 216);
        }
    }

    /// <summary>读取结果里第 row 行的颜色，用于断言拼接顺序。</summary>
    private static byte RowShade(byte[] bgra, int row, int width = Width)
    {
        var stride = width * 4;
        return bgra[row * stride + 2]; // R 通道
    }

    [Fact]
    public void 第一帧整幅纳入()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);
        var result = stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        Assert.Equal(ScrollAppendStatus.First, result.Status);
        Assert.Equal(100, result.AppendedRows);
        Assert.Equal(100, stitcher.TotalHeight);
    }

    [Fact]
    public void 第二帧重叠一半_只追加新增部分()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);

        // 第一帧：页面第 0-99 行
        stitcher.Add(BuildFrame(0, 100), 100, Width * 4);
        // 第二帧：页面向下滚 50 行，显示页面第 50-149 行
        var result = stitcher.Add(BuildFrame(50, 100), 100, Width * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.Equal(50, result.AppendedRows);
        Assert.Equal(150, stitcher.TotalHeight);
        Assert.Equal(50, stitcher.LastScrollDelta);
    }

    [Fact]
    public void 连续滚动_按页面顺序拼成完整长图()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 10000, minMatchRows: 8);

        // 页面共 500 行，每帧显示 100 行，每次滚动 60 行
        const int pageHeight = 500;
        const int frameHeight = 100;
        const int step = 60;

        for (int start = 0; start < pageHeight; start += step)
        {
            var height = Math.Min(frameHeight, pageHeight - start);
            stitcher.Add(BuildFrame(start, height), height, Width * 4);
        }

        // 累积高度不该超过页面总高（重复内容必须被识别出来）
        Assert.True(stitcher.TotalHeight <= pageHeight,
            $"拼接高度 {stitcher.TotalHeight} 超过了页面总高 {pageHeight}");

        // 结果逐行等于页面内容，顺序不能错位
        var buffer = stitcher.BuildBgra();
        for (int row = 0; row < stitcher.TotalHeight; row++)
        {
            var expected = BuildFrame(row, 1);
            Assert.Equal(expected[2], RowShade(buffer, row));
        }
    }

    [Fact]
    public void 亚像素抖动_仍能识别为同一行()
    {
        // 模拟浏览器平滑滚动造成的轻微重绘差异：整幅亮度统一偏移几级
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        var shifted = BuildFrame(50, 100);
        for (int i = 0; i < shifted.Length; i += 4)
        {
            // 只调 R 通道，制造 ±3 的差异
            shifted[i + 2] = (byte)Math.Clamp(shifted[i + 2] + 3, 0, 255);
        }

        var result = stitcher.Add(shifted, 100, Width * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.Equal(150, stitcher.TotalHeight);
    }

    [Fact]
    public void 画面无变化_报告到底()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        // 再喂一帧完全相同的画面：说明页面没滚动，已经到底
        var result = stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        Assert.Equal(ScrollAppendStatus.NoChange, result.Status);
        Assert.Equal(0, result.AppendedRows);
        Assert.Equal(100, stitcher.TotalHeight);
    }

    [Fact]
    public void 滚动距离超过一帧高度_报告找不到重叠区()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        // 直接跳到第 300 行：与第 0-99 行毫无重叠
        var result = stitcher.Add(BuildFrame(300, 100), 100, Width * 4);

        Assert.Equal(ScrollAppendStatus.NoOverlap, result.Status);
        Assert.Equal(100, stitcher.TotalHeight);
    }

    [Fact]
    public void 纯色画面_报告到底而非拼接错误()
    {
        // 整屏纯白时锚点没有区分度，必须停下而不是随便接一段
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 40), 40, Width * 4);

        var solid = new byte[Width * 4 * 100];
        for (int i = 0; i < solid.Length; i += 4)
        {
            solid[i] = 255; solid[i + 1] = 255; solid[i + 2] = 255; solid[i + 3] = 255;
        }

        var result = stitcher.Add(solid, 100, Width * 4);
        Assert.Equal(ScrollAppendStatus.NoChange, result.Status);
    }

    [Fact]
    public void 达到高度上限_停止拼接()
    {
        // 上限 140：第一帧已占 100 行，第二帧需要 50 行但只剩 40 行空间
        var stitcher = new ScrollStitcher(Width, heightLimit: 140, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        var result = stitcher.Add(BuildFrame(50, 100), 100, Width * 4);

        Assert.Equal(140, stitcher.TotalHeight);
        Assert.Equal(40, result.AppendedRows);
        Assert.Equal(ScrollAppendStatus.LimitReached, result.Status);
    }

    [Fact]
    public void 恰好填满上限时仍报告追加成功()
    {
        // 上限 150，第二帧新增 50 行刚好用尽空间：这一帧是完整的，状态应为 Appended
        var stitcher = new ScrollStitcher(Width, heightLimit: 150, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 100), 100, Width * 4);

        var result = stitcher.Add(BuildFrame(50, 100), 100, Width * 4);

        Assert.Equal(150, stitcher.TotalHeight);
        Assert.Equal(ScrollAppendStatus.Appended, result.Status);

        // 下一帧才会因为已满而被拒绝
        var next = stitcher.Add(BuildFrame(150, 100), 100, Width * 4);
        Assert.Equal(ScrollAppendStatus.LimitReached, next.Status);
    }

    [Fact]
    public void 导出缓冲的行数与尺寸一致()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);
        stitcher.Add(BuildFrame(0, 30), 30, Width * 4);

        var buffer = stitcher.BuildBgra();
        Assert.Equal(Width * 4 * 30, buffer.Length);
    }

    [Fact]
    public void 宽度或高度非法时_安全返回()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);

        Assert.Equal(ScrollAppendStatus.NoOverlap, stitcher.Add([], 0, Width * 4).Status);
        // 行字节数不足（窄于声明宽度）时同样拒绝
        Assert.Equal(ScrollAppendStatus.NoOverlap, stitcher.Add(new byte[10], 2, 8).Status);
    }
}
