using System.Drawing;
using System.IO;
using ScreenRecorderLib;
using OneBox.Plugins.Recorder.Core;
using RecorderLib = ScreenRecorderLib.Recorder;

namespace OneBox.Plugins.Recorder.Services;

/// <summary>一次录制请求。坐标均为物理像素，SourceRect 为相对目标显示器的局部坐标。</summary>
public sealed record RecordingStartRequest
{
    public required string OutputPath { get; init; }
    public required string DisplayDeviceName { get; init; }
    public Rectangle? SourceRect { get; init; }
    public required int OutputWidth { get; init; }
    public required int OutputHeight { get; init; }
    public required int Fps { get; init; }
    public required int VideoBitrate { get; init; }
    public required bool RecordSystemAudio { get; init; }
    public required bool RecordMicrophone { get; init; }
    public required bool RecordCursor { get; init; }
    public IntPtr ExcludeWindow { get; init; }
}

/// <summary>录制结束结果。</summary>
public sealed record RecordingOutcome(bool Success, string? FilePath, string? Error);

/// <summary>
/// 对 ScreenRecorderLib 的薄封装：只负责把设置翻译成录制参数，
/// 并把「开始 / 暂停 / 继续 / 停止 / 结束」这套生命周期收敛到一个对象里。
///
/// 结束通知由底层在编码线程上回调，因此 <see cref="Completion"/> 用
/// TaskCompletionSource 直接完成，不经过 WPF 调度器——插件退出时要在
/// UI 线程上同步等它落盘，不能被自己的消息泵卡住。
/// </summary>
public sealed class RecorderService : IDisposable
{
    private RecorderLib? _recorder;
    private TaskCompletionSource<RecordingOutcome>? _completion;
    private string? _outputPath;
    private bool _paused;

    public bool IsActive { get; private set; }
    public bool IsPaused => IsActive && _paused;

    /// <summary>最近一次启动时的非致命提示（例如没有找到音频设备）。</summary>
    public string? Warning { get; private set; }

    /// <summary>本次录制是否真的带音轨（设备找不到时会降级为纯画面）。</summary>
    public bool HasAudio { get; private set; }

    public Task<RecordingOutcome> Completion
        => _completion?.Task ?? Task.FromResult(new RecordingOutcome(false, null, "尚未开始录制"));

    /// <summary>开始录制。同步返回初始化是否成功；真正的编码结果由 <see cref="Completion"/> 给出。</summary>
    public bool Start(RecordingStartRequest request, out string? error)
    {
        error = null;
        Warning = null;
        HasAudio = false;

        var completion = new TaskCompletionSource<RecordingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _outputPath = request.OutputPath;
        _paused = false;

        var warnings = new List<string>();

        try
        {
            var options = BuildOptions(request, warnings);
            HasAudio = options.AudioOptions.IsAudioEnabled == true;
            var recorder = RecorderLib.CreateRecorder(options);

            recorder.OnRecordingComplete += (_, e) =>
            {
                var path = string.IsNullOrWhiteSpace(e.FilePath) ? _outputPath : e.FilePath;
                Finish(new RecordingOutcome(true, path, null));
            };
            recorder.OnRecordingFailed += (_, e) =>
            {
                var message = string.IsNullOrWhiteSpace(e.Error) ? "录制器返回未知错误。" : e.Error;
                Finish(new RecordingOutcome(false, _outputPath, message));
            };

            _recorder = recorder;
            IsActive = true;

            // 悬浮控制条不能出现在录像里。必须在开始录制前设置，否则首帧可能带上它。
            if (request.ExcludeWindow != IntPtr.Zero)
            {
                try
                {
                    var excluded = RecorderLib.SetExcludeFromCapture(request.ExcludeWindow, true);
                    AppLog.Write(excluded ? "已把控制条排除出录制画面" : "控制条排除失败，画面里可能带上控制条");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"排除控制条失败: {ex.Message}");
                }
            }

            recorder.Record(request.OutputPath);

            Warning = warnings.Count > 0 ? string.Join("；", warnings) : null;
            return true;
        }
        catch (Exception ex)
        {
            IsActive = false;
            Warning = warnings.Count > 0 ? string.Join("；", warnings) : null;
            completion.TrySetResult(new RecordingOutcome(false, request.OutputPath, ex.Message));
            SafeDisposeRecorder();
            error = ex.Message;
            return false;
        }
    }

    public void Pause()
    {
        if (!IsActive || _paused) return;
        try
        {
            _recorder?.Pause();
            _paused = true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"暂停失败: {ex.Message}");
        }
    }

    public void Resume()
    {
        if (!IsActive || !_paused) return;
        try
        {
            _recorder?.Resume();
            _paused = false;
        }
        catch (Exception ex)
        {
            AppLog.Write($"继续录制失败: {ex.Message}");
        }
    }

    /// <summary>请求停止；结果仍由 <see cref="Completion"/> 给出。</summary>
    public void Stop()
    {
        try
        {
            _recorder?.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Write($"停止录制失败: {ex.Message}");
            Finish(new RecordingOutcome(false, _outputPath, ex.Message));
        }
    }

    private void Finish(RecordingOutcome outcome)
    {
        IsActive = false;
        _paused = false;
        _completion?.TrySetResult(outcome);
    }

    private static RecorderOptions BuildOptions(RecordingStartRequest request, List<string> warnings)
    {
        var source = new DisplayRecordingSource(request.DisplayDeviceName)
        {
            // Windows 10 1803+ 的窗口捕获 API：对多显示器和排除窗口的处理比 DesktopDuplication 好
            RecorderApi = RecorderApi.WindowsGraphicsCapture,
            IsCursorCaptureEnabled = request.RecordCursor,
            // Windows 11 默认会给正在捕获的屏幕/窗口画一圈黄框；这里关掉，录制更干净
            IsBorderRequired = false,
        };

        if (request.SourceRect is { } rect && rect.Width > 0 && rect.Height > 0)
            source.SourceRect = new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height);

        var audioSources = BuildAudioSources(request, warnings);

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = { source },
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(request.OutputWidth, request.OutputHeight),
                Stretch = StretchMode.Fill,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = request.Fps,
                Bitrate = request.VideoBitrate,
                // 软件编码兼容性最好；这个插件的默认分辨率与帧率都很低，CPU 压力可控
                IsHardwareEncodingEnabled = false,
                IsLowLatencyEnabled = false,
                // 固定帧率：静态画面也按时间轴补帧，成片时长才不会比实际录制时间短
                IsFixedFramerate = true,
                IsThrottlingDisabled = true,
                IsMp4FastStartEnabled = false,
                IsFragmentedMp4Enabled = false,
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.Main,
                },
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                Bitrate = AudioBitrate.bitrate_96kbps,
                Channels = AudioChannels.Stereo,
                AudioSources = audioSources,
            },
            LogOptions = new LogOptions
            {
                IsLogEnabled = true,
                LogFilePath = Path.Combine(AppPaths.DataDirectory, "recorder-native.log"),
                LogSeverityLevel = LogLevel.Warn,
            },
        };
    }

    /// <summary>
    /// 只挑默认设备。用户想换设备可以在 Windows 声音设置里改默认，
    /// 插件不额外维护一份设备列表——轻量工具不需要为这个场景加一层界面。
    /// </summary>
    private static List<AudioSourceBase> BuildAudioSources(RecordingStartRequest request, List<string> warnings)
    {
        var sources = new List<AudioSourceBase>();

        if (request.RecordSystemAudio)
        {
            try
            {
                var devices = RecorderLib.GetSystemAudioLoopbackDevices();
                var device = devices.FirstOrDefault(d => d.IsDefaultDevice) ?? devices.FirstOrDefault();
                if (device is null)
                    warnings.Add("没有找到可用的系统声音输出设备，已只录画面");
                else
                    sources.Add(new LoopbackAudioSource(device.DeviceName) { IsAudioCaptureEnabled = true });
            }
            catch (Exception ex)
            {
                warnings.Add($"系统声音设备枚举失败：{ex.Message}");
            }
        }

        if (request.RecordMicrophone)
        {
            try
            {
                var devices = RecorderLib.GetSystemAudioCaptureDevices();
                var device = devices.FirstOrDefault(d => d.IsDefaultDevice) ?? devices.FirstOrDefault();
                if (device is null)
                    warnings.Add("没有找到可用的麦克风，已跳过麦克风");
                else
                    sources.Add(new CaptureAudioSource(device.DeviceName) { IsAudioCaptureEnabled = true });
            }
            catch (Exception ex)
            {
                warnings.Add($"麦克风枚举失败：{ex.Message}");
            }
        }

        return sources;
    }

    private void SafeDisposeRecorder()
    {
        try
        {
            _recorder?.Dispose();
        }
        catch
        {
            // 释放失败不影响退出
        }
        _recorder = null;
    }

    public void Dispose()
    {
        if (_recorder is not null)
        {
            try
            {
                if (_recorder.Status != RecorderStatus.Idle) _recorder.Stop();
            }
            catch
            {
                // 已停止或底层已释放
            }

            SafeDisposeRecorder();
        }

        IsActive = false;
    }
}
