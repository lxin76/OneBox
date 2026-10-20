using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

/// <summary>
/// 滚动截长图拼接器的测试。
///
/// 用“每行一个亮度”的合成画面模拟可滚动页面：行内容由**页面行号**决定，
/// 于是每一帧就是页面的一段连续内容，拼接结果可以逐行断言。
///
/// 相邻帧按页面坐标切片；起点差才是真实滚动量，不能把 overlap 与 advance 混为一谈。
/// </summary>
public class ScrollStitcherTests
{
    private const int Width = 40;


    /// <summary>页面总行数（供重复结构用例断言）。</summary>
    private const int totalPageRows = 3000;

    /// <summary>
    /// 构造一整页像素：前若干行按固定周期重复，最后 120 行用独有花纹，保证衔接点唯一。
    /// </summary>
    private static PageBuffer BuildRepetitivePage(int totalRows, int period)
    {
        var stride = Width * 4;
        var buffer = new byte[stride * totalRows];
        for (var y = 0; y < totalRows; y++)
        {
            var shade = y >= totalRows - 120
                ? PageRowShade(100000 + y)          // 末尾独有
                : PageRowShade(y % period);         // 周期重复
            for (var x = 0; x < Width; x++)
            {
                var offset = y * stride + x * 4;
                var value = (byte)Math.Clamp(shade + x % 8, 0, 255);
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
                buffer[offset + 3] = 255;
            }
        }
        return new PageBuffer(buffer, Width, totalRows);
    }

    /// <summary>可切片取帧的整页像素缓冲。</summary>
    private sealed class PageBuffer(byte[] pixels, int width, int totalRows)
    {
        private readonly int _stride = width * 4;

        public byte[] Slice(int startRow, int height)
        {
            startRow = Math.Clamp(startRow, 0, Math.Max(0, totalRows - height));
            var slice = new byte[_stride * height];
            Buffer.BlockCopy(pixels, startRow * _stride, slice, 0, slice.Length);
            return slice;
        }
    }

    private static byte[] BuildFrame(int startRow, int height, int width = Width)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var baseValue = PageRowShade(startRow + y);
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var value = (byte)Math.Clamp(baseValue + x % 8, 0, 255);
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
                buffer[offset + 3] = 255;
            }
        }
        return buffer;
    }

    /// <summary>页面某一行的基准亮度：由行号哈希得到，互不相关（不会出现巧合的重复行）。</summary>
    private static int PageRowShade(int pageRow)
    {
        unchecked
        {
            var hash = (uint)pageRow * 2654435761u;
            hash ^= hash >> 15;
            hash *= 2246822519u;
            hash ^= hash >> 13;
            return 20 + (int)(hash % 216);
        }
    }

    /// <summary>结果前 expectedRows 行必须逐行等于页面前 expectedRows 行。</summary>
    private static bool ContentMatches(ScrollStitcher stitcher, int expectedRows)
    {
        if (stitcher.TotalHeight != expectedRows) return false;
        return stitcher.BuildBgra().AsSpan().SequenceEqual(BuildFrame(0, expectedRows));
    }

    [Fact]
    public void 第一帧整幅纳入()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        var result = stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.First, result.Status);
        Assert.Equal(200, stitcher.TotalHeight);
    }

    [Fact]
    public void 两帧之间_只追加新内容并逐行正确()
    {
        // 帧1 = 页面 0..199；帧2 = 页面 150..349（滚了 150 行）
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var result = stitcher.Add(BuildFrame(150, 200), 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.True(ContentMatches(stitcher, 350), $"结果应为页面前 350 行，实际 {stitcher.TotalHeight}");
    }

    [Fact]
    public void 小步滚动_只追加少量行()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var result = stitcher.Add(BuildFrame(20, 200), 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.Equal(20, result.AppendedRows);
        Assert.True(ContentMatches(stitcher, 220), $"结果应为页面前 220 行，实际 {stitcher.TotalHeight}");
    }

    [Fact]
    public void 连续滚动到底_逐行等于页面()
    {
        // 页面 1000 行，帧高 200，每帧滚动 150 行（相对已见内容），最后一帧顶到页面底部。
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        const int page = 1000, frameHeight = 200, scrollStep = 150;

        stitcher.Add(BuildFrame(0, frameHeight), frameHeight, Width * 4);
        var seen = frameHeight;

        for (var guard = 0; seen < page && guard < 100; guard++)
        {
            var start = Math.Max(0, seen - scrollStep);
            if (start + frameHeight > page) start = page - frameHeight;

            var result = stitcher.Add(BuildFrame(start, frameHeight), frameHeight, Width * 4);
            Assert.Equal(ScrollAppendStatus.Appended, result.Status);

            seen = start + frameHeight;
            Assert.Equal(seen, stitcher.TotalHeight);
        }

        Assert.True(ContentMatches(stitcher, page), $"结果应为页面全部 {page} 行，实际 {stitcher.TotalHeight}");
    }

    [Fact]
    public void 大步滚动_也能正确拼接()
    {
        // 滚一屏的 90%：重叠只剩 20 行，远小于探针长度，但仍应靠“帧顶部对结果末尾”定位成功
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 20);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var result = stitcher.Add(BuildFrame(180, 200), 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.True(ContentMatches(stitcher, 380), $"结果应为页面前 380 行，实际 {stitcher.TotalHeight}");
    }

    [Fact]
    public void 中途停止_结果等于已见部分()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);
        stitcher.Add(BuildFrame(100, 200), 200, Width * 4);
        stitcher.Add(BuildFrame(200, 200), 200, Width * 4);

        Assert.True(ContentMatches(stitcher, 400), $"结果应为页面前 400 行，实际 {stitcher.TotalHeight}");
    }

    [Fact]
    public void 画面未滚动_报告画面无变化()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var result = stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.NoChange, result.Status);
        Assert.Equal(200, stitcher.TotalHeight);
    }

    [Fact]
    public void 内容完全不相关_报告找不到重叠区()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var result = stitcher.Add(BuildFrame(5000, 200), 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.NoOverlap, result.Status);
        Assert.Equal(200, stitcher.TotalHeight);
    }

    [Fact]
    public void 新纯色画面_拒绝缺少纹理而非误报无变化()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var solid = new byte[Width * 4 * 200];
        for (var i = 0; i < solid.Length; i += 4)
        {
            solid[i] = 255; solid[i + 1] = 255; solid[i + 2] = 255; solid[i + 3] = 255;
        }

        Assert.Equal(ScrollAppendStatus.InsufficientTexture, stitcher.Add(solid, 200, Width * 4).Status);
        Assert.Equal(1, stitcher.FrameCount);
        Assert.Equal(BuildFrame(0, 200), stitcher.BuildBgra());
    }

    [Fact]
    public void 达到高度上限_停止拼接()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 250, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var result = stitcher.Add(BuildFrame(150, 200), 200, Width * 4);

        Assert.Equal(250, stitcher.TotalHeight);
        Assert.Equal(ScrollAppendStatus.LimitReached, result.Status);
    }

    [Fact]
    public void 亚像素抖动_仍能识别并追加()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 100000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, 200), 200, Width * 4);

        var shifted = BuildFrame(150, 200);
        for (var i = 0; i < shifted.Length; i += 4)
            shifted[i + 2] = (byte)Math.Clamp(shifted[i + 2] + 3, 0, 255);

        var result = stitcher.Add(shifted, 200, Width * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);
        Assert.True(stitcher.TotalHeight > 200);
    }

    [Fact]
    public void 宽度或高度非法时_安全返回()
    {
        var stitcher = new ScrollStitcher(Width, heightLimit: 1000, minMatchRows: 8);

        Assert.Equal(ScrollAppendStatus.NoOverlap, stitcher.Add([], 0, Width * 4).Status);
        Assert.Equal(ScrollAppendStatus.NoOverlap, stitcher.Add(new byte[10], 2, 8).Status);
    }

    /// <summary>
    /// 按真实日志的参数复现：1920×1032 的选区，每次滚轮推进 918 行，重叠应为 114 行。
    ///
    /// 这段数字直接来自用户实机日志：
    ///   "滚动帧 #2: Appended 新增=918 重叠=0 匹配=114/87 位置=0 滚动=918"
    /// 帧高 1032、新增 918 时重叠必须恰好是 114。若拼接器把它算成 0，
    /// 每帧都会整幅重复追加，长图就会成倍变长——这正是用户看到的"很多重复、超长图"。
    /// </summary>
    [Fact]
    public void 真实尺寸下_每帧重叠等于帧高减前进量()
    {
        const int frameHeight = 1032;
        const int advance = 918;
        var expectedOverlap = frameHeight - advance;   // 114

        var stitcher = new ScrollStitcher(1920, heightLimit: 200000, minMatchRows: 24);
        stitcher.Add(BuildFrame(0, frameHeight, 1920), frameHeight, 1920 * 4);

        var result = stitcher.Add(BuildFrame(advance, frameHeight, 1920), frameHeight, 1920 * 4);

        Assert.Equal(ScrollAppendStatus.Appended, result.Status);

        // 先确认真正用于拼接的量是对的：新增行数必须等于前进量
        Assert.Equal(advance, result.AppendedRows);
        Assert.Equal(frameHeight + advance, stitcher.TotalHeight);

        // 再确认诊断字段：重叠长度必须是 帧高 - 前进量
        Assert.Equal(expectedOverlap, result.OverlapRows);
    }

    /// <summary>
    /// 页面含大量重复结构（例如多个字段完全相同的 JSON 代码块）时，探针可能命中多处。
    /// 只要真正的衔接点唯一，拼接结果就必须逐行正确，不能因为"别处也有相似内容"而错位。
    /// </summary>
    [Fact]
    public void 完全周期重复区_拒绝歧义且保持原始内容()
    {
        // 构造一页周期性重复的内容：每 40 行是一个与其他块完全相同的花纹，
        // 末尾再放一段独有内容，用来确保"哪一帧接哪一帧"有唯一解。
        var page = BuildRepetitivePage(totalRows: 3000, period: 40);

        var stitcher = new ScrollStitcher(Width, heightLimit: 200000, minMatchRows: 24);
        const int frameHeight = 900, advance = 760;

        stitcher.Add(page.Slice(0, frameHeight), frameHeight, Width * 4);
        var seen = frameHeight;

        for (var guard = 0; guard < 10 && seen < totalPageRows; guard++)
        {
            var start = seen - (frameHeight - advance);
            var result = stitcher.Add(page.Slice(start, frameHeight), frameHeight, Width * 4);
            if (result.Status != ScrollAppendStatus.Appended) break;
            seen = start + frameHeight;
        }

        // 前两帧均处在完整周期区；页尾独有内容尚不可见，数学上不能猜中周期。
        Assert.Equal(ScrollAppendStatus.Ambiguous, stitcher.Add(page.Slice(760, frameHeight), frameHeight, Width * 4).Status);
        Assert.Equal(frameHeight, stitcher.TotalHeight);
        Assert.Equal(1, stitcher.FrameCount);
        Assert.Equal(page.Slice(0, frameHeight), stitcher.BuildBgra());
    }


    /// <summary>
    /// 整行内容变化不是抗锯齿误差。宁可拒绝，也不能缩短重叠后重复追加；
    /// 后续清洁帧必须仍相对上一张已接受原帧配准，并逐字节恢复完整内容。
    /// </summary>
    [Fact]
    public void 重叠区整行内容变化_保守拒绝且不污染后续基准()
    {
        const int frameHeight = 1032;
        const int advance = 918;
        const int overlap = frameHeight - advance;   // 114

        var stride = Width * 4;
        var page = new byte[stride * 4000];
        for (var y = 0; y < 4000; y++)
        {
            var shade = PageRowShade(y);
            for (var x = 0; x < Width; x++)
            {
                var o = y * stride + x * 4;
                var v = (byte)Math.Clamp(shade + x % 8, 0, 255);
                page[o] = v; page[o + 1] = v; page[o + 2] = v; page[o + 3] = 255;
            }
        }

        byte[] Slice(int start)
        {
            var s = new byte[stride * frameHeight];
            System.Buffer.BlockCopy(page, start * stride, s, 0, s.Length);
            return s;
        }

        var first = Slice(0);
        // 在新帧的重叠区内制造 2 行"脏行"（模拟代码块边框/反色行）
        var second = Slice(advance);
        foreach (var row in new[] { 40, 41 })
        {
            for (var x = 0; x < Width; x++)
            {
                var o = row * stride + x * 4;
                second[o] = 255; second[o + 1] = 255; second[o + 2] = 255;
            }
        }

        var stitcher = new ScrollStitcher(Width, heightLimit: 200000, minMatchRows: 24);
        stitcher.Add(first, frameHeight, stride);
        var result = stitcher.Add(second, frameHeight, stride);

        // 原帧内容发生整行变更，不能把它解释成渲染容差后强行拼接。
        Assert.Equal(ScrollAppendStatus.NoOverlap, result.Status);
        Assert.Equal(1, stitcher.FrameCount);
        Assert.Equal(first, stitcher.BuildBgra());
        var clean = stitcher.Add(Slice(advance), frameHeight, stride);
        Assert.Equal(ScrollAppendStatus.Appended, clean.Status);
        Assert.Equal(advance, clean.AppendedRows);
        Assert.Equal(overlap, clean.OverlapRows);
        Assert.Equal(page.AsSpan(0, (frameHeight + advance) * stride).ToArray(), stitcher.BuildBgra());
    }


    /// <summary>
    /// 诊断：连续多帧按真实参数滚动时，总高是否等于页面实际高度。
    /// 页面 1032 高、每帧前进 918，若拼接正确，N 帧后总高 = 1032 + (N-1)*918。
    /// </summary>
    [Fact]
    public void 诊断_连续九帧真实参数()
    {
        const int frameHeight = 1032, advance = 918, frames = 10;
        var stride = Width * 4;
        const int totalRows = 20000;
        var page = new byte[stride * totalRows];
        for (var y = 0; y < totalRows; y++)
        {
            var shade = PageRowShade(y);
            for (var x = 0; x < Width; x++)
            {
                var o = y * stride + x * 4;
                var v = (byte)Math.Clamp(shade + x % 8, 0, 255);
                page[o] = v; page[o + 1] = v; page[o + 2] = v; page[o + 3] = 255;
            }
        }
        byte[] Slice(int s)
        {
            var b = new byte[stride * frameHeight];
            System.Buffer.BlockCopy(page, s * stride, b, 0, b.Length);
            return b;
        }

        var st = new ScrollStitcher(Width, heightLimit: 200000, minMatchRows: 24);
        st.Add(Slice(0), frameHeight, stride);

        var log = new System.Text.StringBuilder();
        for (var i = 1; i < frames; i++)
        {
            var start = i * advance;
            var r = st.Add(Slice(start), frameHeight, stride);
            log.AppendLine($"frame {i}: start={start} status={r.Status} added={r.AppendedRows} overlap={r.OverlapRows} total={st.TotalHeight}");
        }

        var expected = frameHeight + (frames - 1) * advance;
        Assert.True(st.TotalHeight == expected,
            $"期望总高 {expected}，实际 {st.TotalHeight}\n{log}");
    }


    /// <summary>
    /// 页面含大量**局部**重复结构（本文档里反复出现的 JSON 代码块、相同表格头）时，
    /// 拼接仍必须选对衔接点。真实文档是"局部重复 + 其余内容各不相同"，
    /// 而不是整页周期性逐像素重复——后者在数学上无法区分相邻周期，不适合作为用例。
    /// </summary>
    [Fact]
    public void 页面含局部重复结构时_仍按正确位置衔接()
    {
        const int frameHeight = 1032, advance = 918, frames = 8;
        const int totalRows = 20000;
        var stride = Width * 4;

        var page = new byte[stride * totalRows];
        for (var y = 0; y < totalRows; y++)
        {
            // 每 6 行里有 5 行是"重复花纹"（模拟文档里雷同的代码块），
            // 每第 6 行用与行号相关的独有花纹打断周期性，保证衔接点唯一。
            var shade = (y % 6 == 5) ? PageRowShade(y) : PageRowShade(y % 40);
            for (var x = 0; x < Width; x++)
            {
                var o = y * stride + x * 4;
                var v = (byte)Math.Clamp(shade + x % 8, 0, 255);
                page[o] = v; page[o + 1] = v; page[o + 2] = v; page[o + 3] = 255;
            }
        }
        byte[] Slice(int s)
        {
            var b = new byte[stride * frameHeight];
            System.Buffer.BlockCopy(page, s * stride, b, 0, b.Length);
            return b;
        }

        var st = new ScrollStitcher(Width, heightLimit: 500000, minMatchRows: 24);
        st.Add(Slice(0), frameHeight, stride);

        var log = new System.Text.StringBuilder();
        for (var i = 1; i < frames; i++)
        {
            var s = i * advance;
            var r = st.Add(Slice(s), frameHeight, stride);
            log.AppendLine($"frame {i}: start={s} status={r.Status} added={r.AppendedRows} overlap={r.OverlapRows} total={st.TotalHeight}");
        }

        var expected = frameHeight + (frames - 1) * advance;
        Assert.True(st.TotalHeight == expected, $"期望总高 {expected}，实际 {st.TotalHeight}\n{log}");
    }

    /// <summary>
    /// 复现实机日志里的失败场景：结果高与帧高相同（第一帧后立刻跟进第二帧），
    /// 重叠只有 114 行而探针上限是 200。
    ///
    /// 这组参数曾经让合格线被抬到 200（超过实际能比对的 114 行），
    /// 导致每一帧都判 NoOverlap，滚动截图退化成"只截一屏"。
    /// </summary>
    [Fact]
    public void 结果高与帧高相同时_小幅重叠仍能正确拼接()
    {
        const int frameHeight = 1032, advance = 918, expectedOverlap = 114;
        var stride = Width * 4;
        const int totalRows = 20000;

        var page = new byte[stride * totalRows];
        for (var y = 0; y < totalRows; y++)
        {
            var shade = PageRowShade(y);
            for (var x = 0; x < Width; x++)
            {
                var o = y * stride + x * 4;
                var v = (byte)Math.Clamp(shade + x % 8, 0, 255);
                page[o] = v; page[o + 1] = v; page[o + 2] = v; page[o + 3] = 255;
            }
        }
        byte[] Slice(int s)
        {
            var b = new byte[stride * frameHeight];
            System.Buffer.BlockCopy(page, s * stride, b, 0, b.Length);
            return b;
        }

        var st = new ScrollStitcher(Width, heightLimit: 200000, minMatchRows: 24);

        // 第一帧：结果高 == 帧高（正是触发缺陷的状态）
        st.Add(Slice(0), frameHeight, stride);
        Assert.Equal(frameHeight, st.TotalHeight);

        var r = st.Add(Slice(advance), frameHeight, stride);

        Assert.Equal(ScrollAppendStatus.Appended, r.Status);
        Assert.Equal(advance, r.AppendedRows);
        Assert.Equal(expectedOverlap, r.OverlapRows);
        Assert.Equal(frameHeight + advance, st.TotalHeight);
    }

    /// <summary>
    /// 复现实机故障：结果高 == 帧高，且本帧只前进了很少（页面刚滚一点点）。
    ///
    /// 此时若允许"重叠长度 == 帧高"，整帧都会被判成已有内容 → NoChange → 不追加任何行，
    /// 实机表现就是：每帧都 NoChange、总高永远停在第一屏，长图只有首屏。
    /// 日志特征：NoChange 重叠=1032（帧高）匹配=168/120 总高=1032 滚动=0。
    /// </summary>
    [Theory]
    [InlineData(918)]   // 正常一屏
    [InlineData(600)]   // 前进 6 成
    [InlineData(200)]   // 只前进 200 行
    [InlineData(30)]    // 只前进 30 行（最容易触发"整帧当重叠"）
    public void 小步前进时_不得把整帧判成已有内容(int advance)
    {
        const int frameHeight = 1032;
        var stride = Width * 4;
        const int totalRows = 20000;

        var page = new byte[stride * totalRows];
        for (var y = 0; y < totalRows; y++)
        {
            var shade = PageRowShade(y);
            for (var x = 0; x < Width; x++)
            {
                var o = y * stride + x * 4;
                var v = (byte)Math.Clamp(shade + x % 8, 0, 255);
                page[o] = v; page[o + 1] = v; page[o + 2] = v; page[o + 3] = 255;
            }
        }
        byte[] Slice(int s)
        {
            var b = new byte[stride * frameHeight];
            System.Buffer.BlockCopy(page, s * stride, b, 0, b.Length);
            return b;
        }

        var st = new ScrollStitcher(Width, heightLimit: 200000, minMatchRows: 24);
        st.Add(Slice(0), frameHeight, stride);          // 结果高 == 帧高

        var r = st.Add(Slice(advance), frameHeight, stride);

        Assert.Equal(ScrollAppendStatus.Appended, r.Status);
        Assert.Equal(advance, r.AppendedRows);
        Assert.Equal(frameHeight - advance, r.OverlapRows);
        Assert.Equal(frameHeight + advance, st.TotalHeight);
    }

}
