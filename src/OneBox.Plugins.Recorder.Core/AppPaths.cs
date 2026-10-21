namespace OneBox.Plugins.Recorder.Core;

/// <summary>
/// 录屏插件的私有数据目录。宿主在握手时把分配好的目录传进来
/// （便携模式下是 &lt;宿主安装目录&gt;\data\plugin-state\recorder），
/// 卸载插件时由宿主决定是否删除。
/// </summary>
public static class AppPaths
{
    private static string? _dataDirectory;

    /// <summary>由插件入口在收到宿主握手后初始化。</summary>
    public static void Initialize(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _dataDirectory = dataDirectory;
    }

    /// <summary>插件数据目录；未初始化时回退到临时目录，保证单元测试可独立运行。</summary>
    public static string DataDirectory
    {
        get
        {
            if (_dataDirectory is { Length: > 0 }) return _dataDirectory;

            var fallback = Path.Combine(Path.GetTempPath(), "OneBox", "recorder");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>默认录制目录：跟插件数据一起走，整个安装目录拷走仍可用。</summary>
    public static string DefaultRecordingsDirectory => Path.Combine(DataDirectory, "recordings");
}
