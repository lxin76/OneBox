namespace OneBox.Plugins.Recorder.Core;

/// <summary>一个可选的压缩档位，已经按录制画面的宽高算好目标尺寸。</summary>
public sealed record CompressionTarget(
    RecordingQuality Quality,
    string Label,
    int Width,
    int Height,
    int Bitrate)
{
    /// <summary>给界面显示的尺寸与码率说明。</summary>
    public string Description => $"{Width}×{Height} · {Bitrate / 1000} kbps";
}

/// <summary>
/// 停止录制后的保存计划：根据原始录制尺寸，算出可以压缩到哪些档位。
///
/// 只提供比源画面更小的档位，避免把已经压缩过的录像再"放大"一遍；
/// 目标宽度等于源宽度时不作为压缩选项（没有意义）。
/// </summary>
public static class RecordingSavePlan
{
    private static readonly RecordingQuality[] CompressibleQualities =
    [
        RecordingQuality.Compact,
        RecordingQuality.Standard,
        RecordingQuality.Sharp,
    ];

    public static IReadOnlyList<CompressionTarget> CompressionTargets(int sourceWidth, int sourceHeight, int fps)
    {
        var targets = new List<CompressionTarget>();
        if (sourceWidth <= 0 || sourceHeight <= 0) return targets;

        foreach (var quality in CompressibleQualities)
        {
            var profile = RecordingQualityProfile.For(quality);
            if (profile.MaxWidth >= sourceWidth) continue;

            var (width, height) = MonitorLayout.FitOutputSize(sourceWidth, sourceHeight, profile.MaxWidth);
            if (width >= sourceWidth || height >= sourceHeight) continue;

            targets.Add(new CompressionTarget(
                quality,
                RecordingQualityProfile.Label(quality),
                width,
                height,
                profile.VideoBitrate));
        }

        return targets;
    }

    /// <summary>压缩结果文件名：在原文件名后加目标宽度，例如 录屏_...._960w.mp4。</summary>
    public static string BuildCompressedFileName(string originalPath, int width)
    {
        var directory = Path.GetDirectoryName(originalPath);
        var baseName = Path.GetFileNameWithoutExtension(originalPath);
        var name = $"{baseName}_{width}w{RecordingFileName.Extension}";
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>估算压缩后的文件大小（字节），用于保存对话框给用户一个直观量级。</summary>
    public static long EstimateCompressedBytes(CompressionTarget target, TimeSpan duration, bool withAudio)
        => RecordingQualityProfile.EstimateBytes(
            target.Bitrate,
            withAudio ? RecordingQualityProfile.AudioBitrate : 0,
            duration);
}
