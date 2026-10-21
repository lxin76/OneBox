using OneBox.Plugins.Recorder.Core;

namespace OneBox.Tests;

/// <summary>画质档位的码率/宽度策略与文件体积估算。</summary>
public class RecordingQualityProfileTests
{
    [Fact]
    public void 档位越高码率与宽度越大()
    {
        var compact = RecordingQualityProfile.For(RecordingQuality.Compact);
        var standard = RecordingQualityProfile.For(RecordingQuality.Standard);
        var sharp = RecordingQualityProfile.For(RecordingQuality.Sharp);
        var original = RecordingQualityProfile.For(RecordingQuality.Original);

        Assert.True(compact.VideoBitrate < standard.VideoBitrate);
        Assert.True(standard.VideoBitrate < sharp.VideoBitrate);
        Assert.True(compact.MaxWidth < standard.MaxWidth);
        Assert.True(standard.MaxWidth < sharp.MaxWidth);
        Assert.True(sharp.MaxWidth < original.MaxWidth);
        Assert.True(original.AutoBitrate);
    }

    [Fact]
    public void 原始分辨率码率随像素与帧率增长()
    {
        var profile = RecordingQualityProfile.For(RecordingQuality.Original);

        var hd = profile.ResolveBitrate(1920, 1080, 10);
        var uhd = profile.ResolveBitrate(3840, 2160, 10);
        var hd15 = profile.ResolveBitrate(1920, 1080, 15);

        Assert.InRange(hd, RecordingQualityProfile.MinOriginalBitrate, RecordingQualityProfile.MaxOriginalBitrate);
        Assert.True(uhd > hd);
        Assert.True(hd15 > hd);
        Assert.Equal(hd, profile.ResolveBitrate(1920, 1080, 10));
    }

    [Fact]
    public void 原始分辨率没有固定体积估算()
    {
        Assert.Null(RecordingQualityProfile.TryEstimateBytesPerMinute(RecordingQuality.Original, true));
        Assert.Contains("原始像素", RecordingQualityProfile.DescribeEstimate(RecordingQuality.Original, true));
    }

    [Fact]
    public void 估算包含音频时更大()
    {
        var withAudio = RecordingQualityProfile.TryEstimateBytesPerMinute(RecordingQuality.Standard, true)!.Value;
        var videoOnly = RecordingQualityProfile.TryEstimateBytesPerMinute(RecordingQuality.Standard, false)!.Value;

        Assert.True(withAudio > videoOnly);
        Assert.Equal(
            RecordingQualityProfile.For(RecordingQuality.Standard).VideoBitrate / 8.0 * 60 * 1.02,
            videoOnly, 2);
    }

    [Fact]
    public void 默认档位是省空间()
    {
        Assert.Equal(RecordingQuality.Compact, new RecorderSettings().Quality);
    }

    [Fact]
    public void 描述文字包含体积量级()
    {
        var text = RecordingQualityProfile.DescribeEstimate(RecordingQuality.Compact, true);

        Assert.Contains("MB/分钟", text);
        Assert.Contains("含声音", text);
    }
}