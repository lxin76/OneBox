namespace OneBox.Host.Services;

/// <summary>
/// 宿主命令行开关，以及"后续实例 → 现有实例"转交请求的编解码。
///
/// 资源管理器右键菜单里的项只能执行命令行，所以每一项都会再启动一次 OneBox.exe 并带上开关，
/// 由单实例闸门把请求转交给正在运行的实例去真正执行。这条链路上前后两段必须对字符串的理解
/// 完全一致——写菜单的和读命令行的、发请求的和收请求的——因此集中在这里并配单测。
/// </summary>
public static class HostRequest
{
    /// <summary>打开工具箱。</summary>
    public const string Toolbox = "toolbox";

    /// <summary>打开设置中心。</summary>
    public const string Settings = "settings";

    /// <summary>
    /// 让小工具执行它声明过的某个菜单项。命令行写作 <c>--menu-invoke &lt;pluginId&gt; &lt;itemId&gt;</c>。
    /// </summary>
    public const string MenuInvoke = "menu-invoke";

    private const string MenuPrefix = "menu-invoke:";

    /// <summary>
    /// 解析命令行，得到要执行的请求：<c>toolbox</c> / <c>settings</c> / 菜单请求；没有请求时返回 null。
    /// 菜单请求的两个参数在写菜单时做过百分号转义（id 里可能有空格或中文），这里先还原。
    /// </summary>
    public static string? ParseCommandLine(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (Matches(args[i], Toolbox)) return Toolbox;
            if (Matches(args[i], Settings)) return Settings;

            if (!Matches(args[i], MenuInvoke) || i + 2 >= args.Count) continue;

            var pluginId = Unescape(args[i + 1]);
            var itemId = Unescape(args[i + 2]);
            if (pluginId is null || itemId is null || pluginId.Length == 0 || itemId.Length == 0) return null;

            return PackMenuInvoke(pluginId, itemId);
        }

        return null;
    }

    /// <summary>生成带某个开关的命令行（如 <c>"…\\OneBox.exe" --toolbox</c>）。</summary>
    public static string BuildSwitchCommandLine(string hostExePath, string @switch)
        => $"\"{hostExePath}\" --{@switch}";

    /// <summary>
    /// 生成"执行某小工具菜单项"的命令行，交给快捷方式、脚本或右键菜单使用。
    /// 两个 id 做百分号转义：id 里可能有空格或中文，转义后不必再操心命令行引号规则。
    /// </summary>
    public static string BuildMenuInvokeCommandLine(string hostExePath, string pluginId, string itemId)
        => $"\"{hostExePath}\" --{MenuInvoke} {Uri.EscapeDataString(pluginId)} {Uri.EscapeDataString(itemId)}";

    /// <summary>匹配命令行开关：忽略大小写与前置的 <c>-</c> / <c>/</c>。</summary>
    public static bool Matches(string argument, string name)
        => argument.TrimStart('-', '/').Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>打包"执行某小工具菜单项"的请求（转交管道用冒号分段，因此两段都要转义）。</summary>
    public static string PackMenuInvoke(string pluginId, string itemId)
        => MenuPrefix + Uri.EscapeDataString(pluginId) + ":" + Uri.EscapeDataString(itemId);

    /// <summary>解开菜单请求；不是菜单请求或格式不对时返回 false。</summary>
    public static bool TryUnpackMenuInvoke(string? request, out string pluginId, out string itemId)
    {
        pluginId = "";
        itemId = "";

        if (request is null || !request.StartsWith(MenuPrefix, StringComparison.Ordinal)) return false;

        var body = request[MenuPrefix.Length..];
        var separator = body.IndexOf(':');
        if (separator <= 0 || separator == body.Length - 1) return false;

        var unpackedPluginId = Unescape(body[..separator]);
        var unpackedItemId = Unescape(body[(separator + 1)..]);
        if (string.IsNullOrEmpty(unpackedPluginId) || string.IsNullOrEmpty(unpackedItemId)) return false;

        pluginId = unpackedPluginId;
        itemId = unpackedItemId;
        return true;
    }

    /// <summary>百分号解码；解不开时返回 null（宁可忽略一个请求，也不要抛出去）。</summary>
    private static string? Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}