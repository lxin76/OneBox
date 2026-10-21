namespace OneBox.Plugins.Recorder.Core;

/// <summary>录制画质档位。档位只决定码率与最大输出宽度，不追求高画质。</summary>
public enum RecordingQuality
{
    /// <summary>省空间：小分辨率、低码率，适合随手记录操作步骤。</summary>
    Compact,

    /// <summary>标准：兼顾文字可读性与体积。</summary>
    Standard,

    /// <summary>清晰：码率与分辨率略高，体积也更大。</summary>
    Sharp,

    /// <summary>原始分辨率：不缩放，码率按屏幕像素与帧率自动计算；适合停止后再压缩保存。</summary>
    Original,
}

/// <summary>全屏录制时选哪块屏幕。</summary>
public enum MonitorTarget
{
    /// <summary>鼠标当前所在的屏幕。</summary>
    Cursor,

    /// <summary>系统主屏幕。</summary>
    Primary,
}

/// <summary>录屏插件设置（JSON 持久化到插件数据目录，由宿主分配）。</summary>
public sealed class RecorderSettings
{
    /// <summary>录制文件保存目录；为空时使用 <see cref="AppPaths.DefaultRecordingsDirectory"/>。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>画质档位，默认省空间——这个插件定位就是轻巧便捷。</summary>
    public RecordingQuality Quality { get; set; } = RecordingQuality.Compact;

    /// <summary>帧率。低帧率对静态演示足够，文件也更小。</summary>
    public int Fps { get; set; } = 10;

    /// <summary>是否录制系统声音（扬声器回环）。</summary>
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>是否录制麦克风（可与系统声音同时录制）。</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>是否把鼠标指针录进画面。</summary>
    public bool RecordCursor { get; set; } = true;

    /// <summary>全屏录制时录哪块屏幕。</summary>
    public MonitorTarget FullScreenTarget { get; set; } = MonitorTarget.Cursor;

    /// <summary>录制时是否显示悬浮控制条（计时/暂停/停止）。</summary>
    public bool ShowControlBar { get; set; } = true;

    /// <summary>录制完成后是否在资源管理器里定位到文件。</summary>
    public bool OpenFolderAfterStop { get; set; }

    /// <summary>录制完成后是否询问「保持原画质 / 压缩保存」。</summary>
    public bool AskOnSave { get; set; } = true;

    /// <summary>取实际生效的保存目录。</summary>
    public string ResolveSaveDirectory()
        => string.IsNullOrWhiteSpace(SaveDirectory) ? AppPaths.DefaultRecordingsDirectory : SaveDirectory;
}
