namespace OneBox.Plugins.Recorder.Core;

/// <summary>
/// 画质档位对应的编码参数。码率是 CBR，宽度上限用于等比缩小输出，
/// 不放大源画面（小区域录制保持原始像素）。
///
/// <see cref="RecordingQuality.Original"/> 不限定宽度，码率按「像素数 × 帧率」
/// 估算：屏幕越大、帧率越高，码率自动升高。它适合先录原始画质，停止后再选择压缩保存。
/// </summary>
public readonly record struct RecordingQualityProfile(int VideoBitrate, int MaxWidth, bool AutoBitrate = false)
{
    /// <summary>音频码率。ScreenRecorderLib 的 AAC 档位从 96 kbps 起，取最小档即可。</summary>
    public const int AudioBitrate = 96_000;

    /// <summary>原始分辨率档位的码率估算系数：每像素每帧约 0.12 bit。</summary>
    private const double OriginalBitsPerPixelPerFrame = 0.12;

    /// <summary>原始分辨率档位的最低/最高码率，避免小区域过低、4K 过高。</summary>
    public const int MinOriginalBitrate = 800_000;
    public const int MaxOriginalBitrate = 20_000_000;

    /// <summary>容器与编码开销的粗略补偿（百分比）。</summary>
    private const double OverheadRatio = 0.02;

    public static RecordingQualityProfile For(RecordingQuality quality) => quality switch
    {
        RecordingQuality.Compact => new RecordingQualityProfile(550_000, 960),
        RecordingQuality.Standard => new RecordingQualityProfile(850_000, 1280),
        RecordingQuality.Sharp => new RecordingQualityProfile(1_400_000, 1600),
        RecordingQuality.Original => new RecordingQualityProfile(0, int.MaxValue, AutoBitrate: true),
        _ => new RecordingQualityProfile(550_000, 960),
    };

    public static string Label(RecordingQuality quality) => quality switch
    {
        RecordingQuality.Compact => "省空间",
        RecordingQuality.Standard => "标准",
        RecordingQuality.Sharp => "清晰",
        RecordingQuality.Original => "原始分辨率",
        _ => "省空间",
    };

    /// <summary>
    /// 取实际使用的视频码率。固定档位直接返回预设值；
    /// 原始分辨率档位按输出分辨率与帧率动态计算。
    /// </summary>
    public int ResolveBitrate(int width, int height, int fps)
    {
        if (!AutoBitrate) return VideoBitrate;

        var pixels = (long)Math.Max(1, width) * Math.Max(1, height);
        var raw = pixels * Math.Max(1, fps) * OriginalBitsPerPixelPerFrame;
        return (int)Math.Clamp(raw, MinOriginalBitrate, MaxOriginalBitrate);
    }

    /// <summary>估算每分钟文件大小（字节）；原始分辨率档位需要具体分辨率，返回 null。</summary>
    public static long? TryEstimateBytesPerMinute(RecordingQuality quality, bool withAudio)
    {
        var profile = For(quality);
        if (profile.AutoBitrate) return null;

        return EstimateFromBitrate(profile.VideoBitrate, withAudio);
    }

    /// <summary>把估算结果格式化成一句说明。</summary>
    public static string DescribeEstimate(RecordingQuality quality, bool withAudio)
    {
        var bytes = TryEstimateBytesPerMinute(quality, withAudio);
        if (bytes is null) return "按屏幕原始像素录制，体积取决于分辨率";

        var mb = bytes.Value / 1024.0 / 1024.0;
        return $"约 {mb:F1} MB/分钟" + (withAudio ? "（含声音）" : "（仅画面）");
    }

    /// <summary>按码率与时长估算文件大小（字节）。</summary>
    public static long EstimateBytes(int videoBitrate, int audioBitrate, TimeSpan duration)
    {
        var bitsPerSecond = videoBitrate + Math.Max(0, audioBitrate);
        var seconds = Math.Max(0, duration.TotalSeconds);
        return (long)Math.Round(bitsPerSecond / 8.0 * seconds * (1 + OverheadRatio));
    }

    private static long EstimateFromBitrate(int videoBitrate, bool withAudio)
        => EstimateBytes(videoBitrate, withAudio ? AudioBitrate : 0, TimeSpan.FromMinutes(1));
}
