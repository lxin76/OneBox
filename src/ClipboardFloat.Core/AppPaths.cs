namespace ClipboardFloat.Core;

/// <summary>应用数据目录与文件路径（%LOCALAPPDATA%\ClipboardFloat）。</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClipboardFloat");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string HistoryDbPath => Path.Combine(DataDirectory, "history.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
}
