using OneBox.Plugins.Screenshot.Core;
using Xunit;

namespace OneBox.Tests;

/// <summary>
/// 裁剪 + 双线性放大。用手工构造的小像素块验证几何与取值，不依赖真实图片。
/// </summary>
public class OcrImageScalerTests
{
    /// <summary>造一张 BGRA 图：每像素设为给定灰度，Alpha 固定 255。</summary>
    private static byte[] MakeGray(int width, int height, Func<int, int, byte> value)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var v = value(x, y);
                pixels[o] = v;
                pixels[o + 1] = v;
                pixels[o + 2] = v;
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    [Fact]
    public void 倍率为一时原样裁出()
    {
        var pixels = MakeGray(4, 4, (x, y) => (byte)(x * 10 + y));

        var (outPixels, w, h) = OcrImageScaler.CropAndScale(
            pixels, 4, 4, new OcrRect(1, 1, 2, 2), 1);

        Assert.Equal(2, w);
        Assert.Equal(2, h);
        // 左上角应等于原图 (1,1) 的灰度值 11
        Assert.Equal(11, outPixels[0]);
        // 右下角应等于原图 (2,2) 的灰度值 22
        Assert.Equal(22, outPixels[(1 * 2 + 1) * 4]);
    }

    [Fact]
    public void 放大两倍后尺寸翻倍()
    {
        var pixels = MakeGray(4, 4, (x, y) => 100);

        var (outPixels, w, h) = OcrImageScaler.CropAndScale(
            pixels, 4, 4, new OcrRect(0, 0, 2, 2), 2);

        Assert.Equal(4, w);
        Assert.Equal(4, h);
        Assert.Equal(4 * 4 * 4, outPixels.Length);
    }

    [Fact]
    public void 纯色区域放大后颜色不变()
    {
        var pixels = MakeGray(4, 4, (x, y) => 200);

        var (outPixels, _, _) = OcrImageScaler.CropAndScale(
            pixels, 4, 4, new OcrRect(0, 0, 2, 2), 2);

        for (int i = 0; i < outPixels.Length; i += 4)
            Assert.Equal(200, outPixels[i]);
    }

    [Fact]
    public void 放大在明暗之间做插值而不是取最近点()
    {
        // 左半 0、右半 200 的两列图。最近邻放大后只会出现 0 或 200；
        // 双线性会出现中间值，这里验证确实产生了中间值。
        var pixels = MakeGray(2, 1, (x, y) => x == 0 ? (byte)0 : (byte)200);

        var (outPixels, w, h) = OcrImageScaler.CropAndScale(
            pixels, 2, 1, new OcrRect(0, 0, 2, 1), 4);

        Assert.Equal(8, w);
        Assert.Equal(4, h);

        // 只看第一行即可：横向应出现 0 与 200 之间的中间灰度
        var values = Enumerable.Range(0, w).Select(i => outPixels[i * 4]).ToArray();
        Assert.Contains(values, v => v > 0 && v < 200);
    }

    [Fact]
    public void 区域超出图片时被钳到边界内()
    {
        var pixels = MakeGray(4, 4, (x, y) => 50);

        var (_, w, h) = OcrImageScaler.CropAndScale(
            pixels, 4, 4, new OcrRect(3, 3, 100, 100), 1);

        Assert.Equal(1, w);
        Assert.Equal(1, h);
    }

    [Fact]
    public void 负坐标区域被钳到零()
    {
        var pixels = MakeGray(4, 4, (x, y) => 50);

        var (_, w, h) = OcrImageScaler.CropAndScale(
            pixels, 4, 4, new OcrRect(-5, -5, 4, 4), 2);

        // 从 (0,0) 起可用的 4×4 区域，放大 2 倍
        Assert.Equal(8, w);
        Assert.Equal(8, h);
    }

    [Fact]
    public void 倍率小于一时按一处理()
    {
        var pixels = MakeGray(4, 4, (x, y) => 50);

        var (_, w, h) = OcrImageScaler.CropAndScale(
            pixels, 4, 4, new OcrRect(0, 0, 2, 2), 0);

        Assert.Equal(2, w);
        Assert.Equal(2, h);
    }

    [Fact]
    public void 空输入返回空结果()
    {
        var (pixels, w, h) = OcrImageScaler.CropAndScale([], 0, 0, new OcrRect(0, 0, 1, 1), 2);

        Assert.Empty(pixels);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void Alpha通道一并插值()
    {
        var pixels = MakeGray(2, 1, (x, y) => 100);
        // 把右列改成半透明，验证 Alpha 也参与插值而不是被丢弃
        pixels[1 * 4 + 3] = 100;

        var (outPixels, w, _) = OcrImageScaler.CropAndScale(
            pixels, 2, 1, new OcrRect(0, 0, 2, 1), 4);

        var alphas = Enumerable.Range(0, w).Select(i => outPixels[i * 4 + 3]).ToArray();
        Assert.Contains(alphas, a => a > 100 && a < 255);
    }
}
