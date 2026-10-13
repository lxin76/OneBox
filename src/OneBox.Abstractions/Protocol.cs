using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneBox.Abstractions;

/// <summary>宿主与插件之间的协议常量与共享序列化设置。</summary>
public static class Protocol
{
    /// <summary>当前 IPC 协议版本。宿主与插件版本不一致时宿主拒绝加载该插件。</summary>
    public const int Version = 1;

    /// <summary>插件启动时接收的命名管道名参数（"onebox-plugin --pipe &lt;name&gt;"）。</summary>
    public const string PipeArgument = "--pipe";

    /// <summary>插件启动时接收的插件 id 参数。</summary>
    public const string PluginIdArgument = "--plugin-id";

    /// <summary>插件清单文件名。</summary>
    public const string ManifestFileName = "plugin.json";

    /// <summary>IPC 与清单共用的 JSON 序列化设置（camelCase、忽略 null、枚举转字符串）。</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>清单文件的序列化设置（缩进，便于人工编辑）。</summary>
    public static readonly JsonSerializerOptions ManifestJson = new(Json) { WriteIndented = true };

    /// <summary>为指定插件生成唯一的管道名。</summary>
    public static string CreatePipeName(string pluginId)
        => $"onebox-plugin-{pluginId}-{Guid.NewGuid():N}";
}
