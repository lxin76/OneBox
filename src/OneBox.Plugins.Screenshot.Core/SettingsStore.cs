using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneBox.Plugins.Screenshot.Core;

/// <summary>截图插件设置的读写（JSON）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ScreenshotSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ScreenshotSettings>(File.ReadAllText(path), JsonOptions)
                       ?? new ScreenshotSettings();
        }
        catch
        {
            // 设置文件损坏时回退默认
        }
        return new ScreenshotSettings();
    }

    public static void Save(ScreenshotSettings settings, string? path = null)
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
