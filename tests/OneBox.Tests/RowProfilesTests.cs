using OneBox.Plugins.Screenshot.Core;

namespace OneBox.Tests;

public class RowProfilesTests
{
    private const int Width = 32;

    /// <summary>构造纯色的一行。</summary>
    private static byte[] UniformRow(byte value, int rows = 1, int width = Width)
    {
        var stride = width * 4;
        var buffer = new byte[stride * rows];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = value;
            buffer[i + 1] = value;
            buffer[i + 2] = value;
            buffer[i + 3] = 255;
        }
        return buffer;
    }

    [Fact]
    public void 相同内容的行剖面一致()
    {
        var profiles = RowProfiles.Compute(UniformRow(120, 4), Width, 4, Width * 4);

        Assert.Equal(4, profiles.Length);
        for (int i = 1; i < profiles.Length; i++)
            Assert.Equal(0, RowProfiles.Distance(profiles[0], profiles[i]));
    }

    [Fact]
    public void 纯色行的标准差为零()
    {
        var profiles = RowProfiles.Compute(UniformRow(128), Width, 1, Width * 4);
        Assert.All(profiles[0].Variances, v => Assert.Equal(0, v));
    }

    [Fact]
    public void 明暗交替的行有较大标准差()
    {
        // 逐像素黑白交替：每个块内部都有跳变，标准差应显著大于 0
        var stride = Width * 4;
        var buffer = new byte[stride];
        for (int x = 0; x < Width; x++)
        {
            var value = x % 2 == 0 ? (byte)0 : (byte)255;
            buffer[x * 4] = value;
            buffer[x * 4 + 1] = value;
            buffer[x * 4 + 2] = value;
            buffer[x * 4 + 3] = 255;
        }

        var profiles = RowProfiles.Compute(buffer, Width, 1, stride);
        Assert.All(profiles[0].Variances, v => Assert.True(v > 40, $"标准差 {v} 偏小"));
    }

    [Fact]
    public void 左右半黑的纯色块标准差为零()
    {
        // 左半黑右半白：每块内部仍是纯色，因此块内标准差为 0（差异体现在块间均值上）
        var stride = Width * 4;
        var buffer = new byte[stride];
        for (int x = 0; x < Width; x++)
        {
            var value = x < Width / 2 ? (byte)0 : (byte)255;
            buffer[x * 4] = value;
            buffer[x * 4 + 1] = value;
            buffer[x * 4 + 2] = value;
            buffer[x * 4 + 3] = 255;
        }

        var profiles = RowProfiles.Compute(buffer, Width, 1, stride);
        Assert.All(profiles[0].Variances, v => Assert.Equal(0, v));
        // 但均值差异明显：首块接近 0，末块接近 255
        Assert.True(profiles[0].Means[0] < 10);
        Assert.True(profiles[0].Means[^1] > 245);
    }

    [Fact]
    public void 亮度相近的行判定为相似()
    {
        var a = RowProfiles.Compute(UniformRow(100), Width, 1, Width * 4)[0];
        var b = RowProfiles.Compute(UniformRow(105), Width, 1, Width * 4)[0];

        Assert.True(RowProfiles.IsSimilar(a, b));
    }

    [Fact]
    public void 亮度差异明显的行判定为不相似()
    {
        var a = RowProfiles.Compute(UniformRow(30), Width, 1, Width * 4)[0];
        var b = RowProfiles.Compute(UniformRow(220), Width, 1, Width * 4)[0];

        Assert.False(RowProfiles.IsSimilar(a, b));
    }

    [Fact]
    public void 纯色画面缺少变化()
    {
        var profiles = RowProfiles.Compute(UniformRow(128, 20), Width, 20, Width * 4);
        Assert.False(RowProfiles.HasVariance(profiles, 0, 20));
    }

    [Fact]
    public void 逐行不同亮度的画面有变化()
    {
        var stride = Width * 4;
        var buffer = new byte[stride * 20];
        for (int y = 0; y < 20; y++)
        for (int x = 0; x < Width; x++)
        {
            var offset = y * stride + x * 4;
            var value = (byte)(y * 12);
            buffer[offset] = value;
            buffer[offset + 1] = value;
            buffer[offset + 2] = value;
            buffer[offset + 3] = 255;
        }

        var profiles = RowProfiles.Compute(buffer, Width, 20, stride);
        Assert.True(RowProfiles.HasVariance(profiles, 0, 20));
    }

    [Fact]
    public void 尺寸非法时返回空数组()
    {
        Assert.Empty(RowProfiles.Compute([], 0, 10, 0));
        Assert.Empty(RowProfiles.Compute([], 10, 0, 40));
    }

    [Fact]
    public void 分块数可配置且不越界()
    {
        var profiles = RowProfiles.Compute(UniformRow(90), Width, 1, Width * 4, blocks: 4);
        Assert.Equal(4, profiles[0].BlockCount);
    }
}
