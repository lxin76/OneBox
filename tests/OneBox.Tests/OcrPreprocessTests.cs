using OneBox.Plugins.Screenshot.Core;
using Xunit;

namespace OneBox.Tests;

/// <summary>
/// 小字图像增强的决策逻辑。全部用合成行框，不依赖真实 OCR。
/// 判据里那些数字（行高阈值、倍率、采纳比例）都来自实测，见 OcrPreprocess 的注释。
/// </summary>
public class OcrPreprocessTests
{
    private static OcrRect R(double x, double y, double w, double h) => new(x, y, w, h);

    // ---- 行高统计 ----

    [Fact]
    public void 空输入的中位行高为零()
    {
        Assert.Equal(0, OcrPreprocess.MedianLineHeight([]));
    }

    [Fact]
    public void 奇数行取中间那个高度()
    {
        var heights = new[] { R(0, 0, 10, 30), R(0, 50, 10, 12), R(0, 80, 10, 20) };

        Assert.Equal(20, OcrPreprocess.MedianLineHeight(heights));
    }

    [Fact]
    public void 偶数行取中间两个的平均()
    {
        var heights = new[] { R(0, 0, 10, 10), R(0, 50, 10, 20), R(0, 80, 10, 30), R(0, 120, 10, 40) };

        Assert.Equal(25, OcrPreprocess.MedianLineHeight(heights));
    }

    [Fact]
    public void 空框不参与中位数计算()
    {
        var heights = new[] { R(0, 0, 0, 0), R(0, 10, 10, 18) };

        Assert.Equal(18, OcrPreprocess.MedianLineHeight(heights));
    }

    // ---- 文字区域 ----

    [Fact]
    public void 文字区域包住所有行并向外留边距()
    {
        var lines = new[] { R(10, 20, 30, 20), R(10, 100, 30, 20) };

        var bounds = OcrPreprocess.EstimateTextBounds(lines, 500, 500, padding: 10);

        Assert.NotNull(bounds);
        Assert.Equal(0, bounds!.Value.X);      // 10 - 10
        Assert.Equal(10, bounds.Value.Y);      // 20 - 10
        Assert.Equal(50, bounds.Value.Right);  // 40 + 10
        Assert.Equal(130, bounds.Value.Bottom); // 120 + 10
    }

    [Fact]
    public void 文字区域被夹到图片范围内()
    {
        var lines = new[] { R(0, 0, 100, 100) };

        var bounds = OcrPreprocess.EstimateTextBounds(lines, 60, 60, padding: 50);

        Assert.NotNull(bounds);
        Assert.Equal(0, bounds!.Value.X);
        Assert.Equal(0, bounds.Value.Y);
        Assert.Equal(60, bounds.Value.Right);
        Assert.Equal(60, bounds.Value.Bottom);
    }

    [Fact]
    public void 没有有效行框时得不到文字区域()
    {
        Assert.Null(OcrPreprocess.EstimateTextBounds([], 100, 100, 10));
        Assert.Null(OcrPreprocess.EstimateTextBounds([R(0, 0, 0, 0)], 100, 100, 10));
    }

    // ---- 放大倍率 ----

    [Fact]
    public void 行高足够时不放大()
    {
        Assert.Equal(1, OcrPreprocess.ChooseScaleFactor(30, 800, 600));
    }

    [Fact]
    public void 中等小字放大两倍()
    {
        // 行高 15-19px：放大 2× 后已超过引擎可靠阈值
        Assert.Equal(2, OcrPreprocess.ChooseScaleFactor(15, 800, 600));
        Assert.Equal(2, OcrPreprocess.ChooseScaleFactor(19, 800, 600));
    }

    [Fact]
    public void 极小字放大三倍()
    {
        Assert.Equal(3, OcrPreprocess.ChooseScaleFactor(8, 800, 600));
    }

    [Fact]
    public void 十二三px小字放大三倍()
    {
        // 实测：文件资源管理器行高 12.5px 时 2× 不够（"搜索→喂素、文件夹→牛夹"），
        // 2× 放大后仅 25px，仍在引擎的不可靠区；3× 后 37px 才稳。
        Assert.Equal(3, OcrPreprocess.ChooseScaleFactor(12.5, 800, 600));
        Assert.Equal(3, OcrPreprocess.ChooseScaleFactor(13, 800, 600));
        Assert.Equal(3, OcrPreprocess.ChooseScaleFactor(14, 800, 600));
    }

    [Fact]
    public void 超出像素预算时回退倍率()
    {
        // 3000×2000 放大 2× 是 2400 万像素，恰好不超；再大就回退到 1×
        // （行高 19px 走 2× 档，避免与 3× 档的预算纠缠）
        Assert.Equal(2, OcrPreprocess.ChooseScaleFactor(19, 3000, 2000));

        Assert.Equal(1, OcrPreprocess.ChooseScaleFactor(19, 4000, 3000));
    }

    [Fact]
    public void 超出引擎边长上限时回退倍率()
    {
        // 6000×100 放大到 12000 会超过引擎的 10000 边长上限
        Assert.Equal(1, OcrPreprocess.ChooseScaleFactor(8, 6000, 100, maxScaledPixels: long.MaxValue, maxDimension: 10000));
    }

    // ---- 放大方案 ----

    [Fact]
    public void 小字排版产出可用的放大方案()
    {
        var layout = OcrLayout.Build([new OcrWord("中文小字", R(10, 10, 60, 13))]);

        var plan = OcrPreprocess.Plan(layout, 800, 600);

        Assert.True(plan.IsUseful);
        Assert.Equal(3, plan.Factor);
        Assert.Equal(13, plan.MedianLineHeight, 1);
    }

    [Fact]
    public void 大字排版不放大()
    {
        var layout = OcrLayout.Build([new OcrWord("大号文字", R(10, 10, 200, 40))]);

        var plan = OcrPreprocess.Plan(layout, 800, 600);

        Assert.False(plan.IsUseful);
        Assert.Equal(1, plan.Factor);
    }

    [Fact]
    public void 空版面没有放大方案()
    {
        var plan = OcrPreprocess.Plan(OcrLayout.Build([]), 800, 600);

        Assert.False(plan.IsUseful);
        Assert.Equal(1, plan.Factor);
    }

    // ---- 采纳判据 ----

    [Fact]
    public void 行数不变且字符不减时采纳放大结果()
    {
        // 实测的小字图场景：6 行 102 字符，放大后仍是 6 行 102 字符，但错字被纠正
        Assert.True(OcrPreprocess.ShouldAcceptEnhanced(6, 102, 6, 102));
    }

    [Fact]
    public void 行被大量拆散时拒绝放大结果()
    {
        // 实测的 40 行全屏图场景：放大后行数 40 → 77，字符数也降了
        Assert.False(OcrPreprocess.ShouldAcceptEnhanced(40, 1591, 77, 1473));
    }

    [Fact]
    public void 字符明显变少时拒绝放大结果()
    {
        Assert.False(OcrPreprocess.ShouldAcceptEnhanced(6, 102, 6, 80));
    }

    [Fact]
    public void 放大后识别出更多内容时采纳()
    {
        Assert.True(OcrPreprocess.ShouldAcceptEnhanced(6, 80, 6, 102));
    }

    [Fact]
    public void 放大后什么都没识别出来时拒绝()
    {
        Assert.False(OcrPreprocess.ShouldAcceptEnhanced(6, 102, 0, 0));
    }

    [Fact]
    public void 原图没有文字时采纳放大结果()
    {
        Assert.True(OcrPreprocess.ShouldAcceptEnhanced(0, 0, 3, 20));
    }
}
