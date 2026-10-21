using System.IO;
using OneBox.Plugins.Recorder.Core;

namespace OneBox.Tests;

/// <summary>停止录制后的压缩档位计算与文件名。</summary>
public class RecordingSavePlanTests
{
    [Fact]
    public void 原始画面可压缩到三个更小的档位()
    {
        var targets = RecordingSavePlan.CompressionTargets(1920, 1080, 10);

        Assert.Equal(3, targets.Count);
        Assert.Equal([960, 1280, 1600], targets.Select(t => t.Width));
        Assert.Equal([540, 720, 900], targets.Select(t => t.Height));
        Assert.All(targets, t => Assert.True(t.Width < 1920 && t.Height < 1080));
    }

    [Fact]
    public void 标准画面只提供省空间档位()
    {
        var targets = RecordingSavePlan.CompressionTargets(1280, 720, 10);

        var target = Assert.Single(targets);
        Assert.Equal(960, target.Width);
        Assert.Equal(540, target.Height);
        Assert.Equal(RecordingQuality.Compact, target.Quality);
    }

    [Fact]
    public void 已经是最小档位时没有压缩选项()
    {
        Assert.Empty(RecordingSavePlan.CompressionTargets(960, 540, 10));
        Assert.Empty(RecordingSavePlan.CompressionTargets(640, 360, 10));
    }

    [Fact]
    public void 非标准比例选区也按最大宽度等比缩放()
    {
        var target = Assert.Single(RecordingSavePlan.CompressionTargets(1000, 600, 10));

        Assert.Equal(960, target.Width);
        Assert.Equal(576, target.Height);
    }

    [Fact]
    public void 压缩文件名带目标宽度后缀()
    {
        var path = RecordingSavePlan.BuildCompressedFileName(@"D:\Recordings\录屏_20261021_101013.mp4", 960);

        Assert.Equal(@"D:\Recordings\录屏_20261021_101013_960w.mp4", path);
    }

    [Fact]
    public void 压缩体积估算随时长增长()
    {
        var target = RecordingSavePlan.CompressionTargets(1920, 1080, 10)[0];

        var shortClip = RecordingSavePlan.EstimateCompressedBytes(target, TimeSpan.FromSeconds(10), true);
        var longClip = RecordingSavePlan.EstimateCompressedBytes(target, TimeSpan.FromSeconds(60), true);

        Assert.True(shortClip > 0);
        Assert.True(longClip > shortClip * 5);
    }

    [Fact]
    public void 档位说明包含目标尺寸与码率()
    {
        var target = RecordingSavePlan.CompressionTargets(1920, 1080, 10)[0];

        Assert.Contains("960×540", target.Description);
        Assert.Contains("kbps", target.Description);
    }
}
