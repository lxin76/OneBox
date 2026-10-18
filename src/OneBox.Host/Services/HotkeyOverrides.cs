using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>
/// 用户自定义热键的覆盖层：记录"某个小工具的某个动作被用户改成了哪个键"。
///
/// 为什么不直接让插件记住用户改的键：热键由宿主统一注册（见 <see cref="HotkeyService"/>），
/// 用户又是在工具箱的"热键"页里改键，界面层根本不知道插件内部的设置格式。
/// 把它放在宿主侧，改键就是一次纯宿主操作——插件下次重启、甚至被重装，
/// 只要 actionId 没变，用户的改键依然有效。
///
/// 存储形态是扁平字典，键为 <see cref="KeyOf"/> 生成的 "&lt;插件id&gt;|&lt;动作id&gt;"：
/// 结构简单，JSON 里一眼能看出改过哪些键，损坏时也只需丢弃单条。
/// </summary>
public sealed class HotkeyOverrides
{
    private readonly Dictionary<string, string> _map;

    public HotkeyOverrides() : this(null) { }

    public HotkeyOverrides(IDictionary<string, string>? source)
    {
        _map = source is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>覆盖项数量。</summary>
    public int Count => _map.Count;

    /// <summary>覆盖键：插件 id + 动作 id，用"|"分隔（两者都不含该字符）。</summary>
    public static string KeyOf(string pluginId, string actionId) => $"{pluginId}|{actionId}";

    /// <summary>取用户为某动作设定的组合键；没改过返回 null。</summary>
    public string? Get(string pluginId, string actionId)
        => _map.TryGetValue(KeyOf(pluginId, actionId), out var combo) && !string.IsNullOrWhiteSpace(combo)
            ? combo
            : null;

    /// <summary>设定覆盖；组合键无法解析时忽略（宁可用回默认，也不注册一个坏组合）。</summary>
    public void Set(string pluginId, string actionId, string combination)
    {
        if (HotkeyParser.Normalize(combination) is not { } normalized) return;
        _map[KeyOf(pluginId, actionId)] = normalized;
    }

    /// <summary>移除某动作的覆盖（恢复插件默认键）。</summary>
    public bool Remove(string pluginId, string actionId)
        => _map.Remove(KeyOf(pluginId, actionId));

    /// <summary>移除某插件的全部覆盖（插件被卸载时调用）。</summary>
    public int RemovePlugin(string pluginId)
    {
        var prefix = pluginId + "|";
        var keys = _map.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys) _map.Remove(key);
        return keys.Count;
    }

    /// <summary>快照（写回设置文件用）。</summary>
    public Dictionary<string, string> ToDictionary()
        => new(_map, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把存档里读出的覆盖表整理成可用状态：丢掉解析不了的条目。
    ///
    /// 这里刻意不抛异常：存档可能被用户手改过、或来自旧版本，
    /// 一条坏数据不该让整个覆盖层失效，更不该让宿主启动失败。
    /// </summary>
    public static HotkeyOverrides FromPersisted(IDictionary<string, string>? persisted)
    {
        var result = new HotkeyOverrides();
        if (persisted is null) return result;

        foreach (var (key, combination) in persisted)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(combination)) continue;
            if (HotkeyParser.Normalize(combination) is not { } normalized) continue;
            result._map[key] = normalized;
        }

        return result;
    }
}
