using System.Text.Json;

namespace OneBox.Plugins.TextPin.Core;

/// <summary>设置读写（JSON）。任何失败都回退默认值，绝不因为设置文件损坏而启动失败。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch
        {
            // 设置文件损坏时回退默认
        }
        return new Settings();
    }

    public static void Save(Settings settings, string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // 保存失败不影响主流程
        }
    }
}
