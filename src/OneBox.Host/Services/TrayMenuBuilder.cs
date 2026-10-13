using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>
/// 一个插件在托盘菜单里的分组：插件名作为子菜单标题，其下是该插件的功能项。
/// </summary>
public sealed class PluginMenuGroup
{
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }

    /// <summary>清洗过的菜单项；<see cref="TrayMenuEntry.IsSeparator"/> 为 true 表示分隔线。</summary>
    public required IReadOnlyList<TrayMenuEntry> Entries { get; init; }

    /// <summary>是否至少有一个可点击的项（只有分隔线的分组没有展示价值）。</summary>
    public bool HasCommands => Entries.Any(e => !e.IsSeparator);
}

/// <summary>菜单里的一项：要么是插件声明的项，要么是分隔线。</summary>
public readonly record struct TrayMenuEntry(TrayMenuItem? Item)
{
    public bool IsSeparator => Item is null;

    public static TrayMenuEntry Separator => new(null);
    public static TrayMenuEntry Command(TrayMenuItem item) => new(item);
}

/// <summary>
/// 把各插件声明的菜单项整理成「每个插件一个子菜单」的结构。
///
/// 之所以抽成纯逻辑：托盘菜单是宿主里最容易堆乱的地方 —— 插件一多，
/// 扁平铺开的项会挤满整个右键菜单。这里只做数据整理，不碰 WPF，
/// 因此可以完整单元测试。
/// </summary>
public static class TrayMenuBuilder
{
    /// <summary>
    /// 构建菜单分组。
    /// </summary>
    /// <param name="plugins">
    /// 每个元素是（插件 id、显示名、该插件声明的菜单项）。调用方应只传入正在运行的插件。
    /// </param>
    public static IReadOnlyList<PluginMenuGroup> Build(
        IEnumerable<(string PluginId, string PluginName, IReadOnlyList<TrayMenuItem> Items)> plugins)
    {
        var groups = new List<PluginMenuGroup>();

        foreach (var (pluginId, pluginName, items) in plugins)
        {
            var entries = Sanitize(items);
            if (entries.Count == 0) continue;

            var group = new PluginMenuGroup
            {
                PluginId = pluginId,
                PluginName = string.IsNullOrWhiteSpace(pluginName) ? pluginId : pluginName,
                Entries = entries,
            };

            // 全是分隔线的分组（插件只声明了分隔线）没有展示意义
            if (!group.HasCommands) continue;

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// 清洗插件声明的菜单项：
    /// 去掉首尾分隔线，并把连续分隔线折叠成一条。
    /// 插件各自独立声明菜单，很容易在边界处留下多余分隔线
    /// （例如原来扁平排列时用来分隔"自己的项"和"别人的项"）。
    /// </summary>
    public static IReadOnlyList<TrayMenuEntry> Sanitize(IReadOnlyList<TrayMenuItem> items)
    {
        var result = new List<TrayMenuEntry>();

        foreach (var item in items)
        {
            if (item.Kind == TrayMenuItemKind.Separator)
            {
                // 开头不要分隔线；连续分隔线只留一条
                if (result.Count == 0 || result[^1].IsSeparator) continue;
                result.Add(TrayMenuEntry.Separator);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Text)) continue;
            result.Add(TrayMenuEntry.Command(item));
        }

        // 结尾的分隔线没有意义
        while (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);

        return result;
    }
}
