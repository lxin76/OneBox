using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneBox.Abstractions;

/// <summary>插件清单（插件目录下的 plugin.json），描述如何启动这个插件。</summary>
public sealed class PluginManifest
{
    /// <summary>插件唯一标识（小写字母、数字与短横线），用于管道名、数据目录与注册表键。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>插件版本（自由格式，宿主仅展示）。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>一句话说明，显示在工具箱列表里。</summary>
    public string Description { get; set; } = "";

    /// <summary>作者（可选）。</summary>
    public string? Author { get; set; }

    /// <summary>入口可执行文件，相对插件目录的路径。</summary>
    public string Entry { get; set; } = "";

    /// <summary>传给插件的额外命令行参数。</summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>插件声明的协议版本；宿主用它判断兼容性。0 表示未声明，按当前版本处理。</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>清单所在目录（读取时填充，不写入 JSON）。</summary>
    public string Directory { get; set; } = "";
}

/// <summary>清单读取结果：成功返回清单，失败返回原因（用于工具箱里显示"损坏"插件）。</summary>
public sealed record ManifestLoadResult(PluginManifest? Manifest, string? Error)
{
    public bool Success => Manifest is not null;
}

/// <summary>plugin.json 的读取与校验。</summary>
public static partial class ManifestReader
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    /// <summary>读取并校验插件目录下的清单。任何问题都返回错误描述而不抛异常。</summary>
    public static ManifestLoadResult Load(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, Protocol.ManifestFileName);
        if (!File.Exists(path))
            return new ManifestLoadResult(null, $"缺少 {Protocol.ManifestFileName}");

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Protocol.ManifestJson);
        }
        catch (Exception ex)
        {
            return new ManifestLoadResult(null, $"{Protocol.ManifestFileName} 解析失败: {ex.Message}");
        }

        if (manifest is null)
            return new ManifestLoadResult(null, $"{Protocol.ManifestFileName} 内容为空");

        if (string.IsNullOrWhiteSpace(manifest.Id) || !IdPattern().IsMatch(manifest.Id))
            return new ManifestLoadResult(null, "id 必须以小写字母或数字开头，且只含小写字母、数字、'.'、'_'、'-'");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            manifest.Name = manifest.Id;

        if (string.IsNullOrWhiteSpace(manifest.Entry))
            return new ManifestLoadResult(null, "缺少 entry（入口可执行文件）");

        if (manifest.ProtocolVersion != 0 && manifest.ProtocolVersion != Protocol.Version)
            return new ManifestLoadResult(null, $"协议版本 {manifest.ProtocolVersion} 与宿主 {Protocol.Version} 不兼容");

        manifest.Directory = pluginDirectory;

        // 相对路径按插件目录解析后必须仍在插件目录内，防止清单越权指向系统目录
        var entryPath = Path.GetFullPath(Path.Combine(pluginDirectory, manifest.Entry));
        var rootPath = Path.GetFullPath(pluginDirectory);
        if (!IsInside(entryPath, rootPath))
            return new ManifestLoadResult(null, "entry 指向插件目录之外");

        if (!File.Exists(entryPath))
            return new ManifestLoadResult(null, $"入口文件不存在: {manifest.Entry}");

        return new ManifestLoadResult(manifest, null);
    }

    private static bool IsInside(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
