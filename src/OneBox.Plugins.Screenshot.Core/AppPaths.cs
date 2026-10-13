namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 插件的私有数据目录。宿主在握手时把分配好的目录传进来
/// （%LOCALAPPDATA%\OneBox\plugin-state\screenshot），卸载插件时由宿主决定是否删除。
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

            var fallback = Path.Combine(Path.GetTempPath(), "OneBox", "screenshot");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>截图默认保存目录（用户未指定时使用）。</summary>
    public static string DefaultSaveDirectory => Path.Combine(DataDirectory, "shots");
}
