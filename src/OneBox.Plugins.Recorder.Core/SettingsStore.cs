using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneBox.Plugins.Recorder.Core;

/// <summary>录屏插件设置的读写（JSON）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RecorderSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllText(path), JsonOptions)
                       ?? new RecorderSettings();
        }
        catch
        {
            // 设置文件损坏时回退默认
        }
        return new RecorderSettings();
    }

    public static void Save(RecorderSettings settings, string? path = null)
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
