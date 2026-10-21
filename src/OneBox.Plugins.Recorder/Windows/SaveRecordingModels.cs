using OneBox.Plugins.Recorder.Core;

namespace OneBox.Plugins.Recorder.Windows;

/// <summary>停止录制后，用户在保存对话框里的选择。</summary>
public enum RecordingSaveMode
{
    KeepOriginal,
    Compress,
}

/// <summary>保存对话框需要的录制信息。</summary>
public sealed record SaveRecordingInfo(
    string FilePath,
    string QualityLabel,
    TimeSpan Duration,
    long FileSize,
    int Width,
    int Height,
    int Bitrate,
    bool HasAudio,
    IReadOnlyList<CompressionTarget> Targets);

/// <summary>保存对话框的结果。</summary>
public sealed record SaveRecordingChoice(
    RecordingSaveMode Mode,
    CompressionTarget? Target,
    bool KeepOriginalFile);
