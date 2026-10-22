using OneBox.Plugin.Sdk.Logging;

namespace OneBox.Plugins.Recorder.Services;

/// <summary>
/// 插件日志适配层：真正的写入由 SDK 的 <see cref="PluginLog"/> 负责，
/// 每个插件只有一个 data/logs/plugin-&lt;id&gt;.log（10 MB 上限，写满覆盖最旧内容）。
/// </summary>
public static class AppLog
{
    public static void Write(string message) => PluginLog.Write(message);
}
