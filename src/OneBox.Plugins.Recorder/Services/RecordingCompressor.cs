using System.IO;
using OneBox.Plugins.Recorder.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OneBox.Plugins.Recorder.Services;

/// <summary>一次压缩转码请求。</summary>
public sealed record CompressionRequest(
    string InputPath,
    string OutputPath,
    int Width,
    int Height,
    int Fps,
    int Bitrate);

/// <summary>
/// 停止录制后的压缩：用 Windows 自带的 MediaTranscoder 把原始 MP4 按目标
/// 分辨率与码率重编码一遍。需要 Windows 10 19041+ 的 Windows SDK 投影，
/// 底层仍然走系统 H.264/AAC 编码器，不额外引入 ffmpeg。
/// </summary>
public static class RecordingCompressor
{
    public static async Task<string> CompressAsync(
        CompressionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.InputPath))
            throw new FileNotFoundException("找不到原始录制文件。", request.InputPath);

        var directory = Path.GetDirectoryName(request.OutputPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("输出目录无效。");
        Directory.CreateDirectory(directory);

        var source = await StorageFile.GetFileFromPathAsync(request.InputPath);
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var output = await folder.CreateFileAsync(
            Path.GetFileName(request.OutputPath), CreationCollisionOption.GenerateUniqueName);

        try
        {
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
            profile.Video.Width = (uint)Math.Max(2, request.Width);
            profile.Video.Height = (uint)Math.Max(2, request.Height);
            profile.Video.Bitrate = (uint)Math.Max(1, request.Bitrate);
            profile.Video.FrameRate.Numerator = (uint)Math.Max(1, request.Fps);
            profile.Video.FrameRate.Denominator = 1;

            if (profile.Audio is not null)
            {
                profile.Audio.Bitrate = RecordingQualityProfile.AudioBitrate;
                profile.Audio.SampleRate = 48_000;
                profile.Audio.ChannelCount = 2;
            }

            var transcoder = new MediaTranscoder
            {
                HardwareAccelerationEnabled = true,
            };

            var prepared = await transcoder
                .PrepareFileTranscodeAsync(source, output, profile)
                .AsTask(cancellationToken);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException($"系统转码器无法处理该文件：{prepared.FailureReason}");

            var reporter = progress is null
                ? null
                : new Progress<double>(value => progress.Report(Math.Clamp(value * 100, 0, 100)));

            await prepared.TranscodeAsync().AsTask(cancellationToken, reporter);
            return output.Path;
        }
        catch
        {
            // 失败或取消时不留下半截文件
            try { await output.DeleteAsync(); } catch { /* 忽略清理失败 */ }
            throw;
        }
    }
}
