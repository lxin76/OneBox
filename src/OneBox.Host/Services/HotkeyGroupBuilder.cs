using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>某一分组下的一条热键（已整理成界面可直接绑定的纯数据）。</summary>
public sealed class HotkeyRow
{
    /// <summary>插件内动作标识。</summary>
    public required string ActionId { get; init; }

    /// <summary>组合键文本（如 "Ctrl+Alt+V"）。</summary>
    public required string Combination { get; init; }

    /// <summary>用途说明；插件没给说明时退回 actionId。</summary>
    public required string Description { get; init; }

    /// <summary>是否已成功注册。</summary>
    public required bool Registered { get; init; }

    /// <summary>注册失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>
    /// 冲突说明（无冲突时为 null）。与 <see cref="Error"/> 的区别：
    /// Error 是本次注册失败的原因，Conflict 是"这个组合键被同一平台里的其他热键用了"，
    /// 适用于用户需要四处改键才能消解的情形——界面据此高亮。
    /// </summary>
    public string? Conflict { get; init; }

    /// <summary>与之冲突的其他热键（用于在界面里点名"和谁撞了"）。</summary>
    public IReadOnlyList<HotkeyConflictPeer> ConflictWith { get; init; } = [];

    /// <summary>是否存在需要高亮的冲突。</summary>
    public bool HasConflict => Conflict is not null;
}

/// <summary>冲突的另一方：哪个小工具的哪条热键。</summary>
public sealed class HotkeyConflictPeer
{
    /// <summary>对方所属插件 id。</summary>
    public required string PluginId { get; init; }

    /// <summary>对方所属插件展示名。</summary>
    public required string PluginName { get; init; }

    /// <summary>对方的用途说明。</summary>
    public required string Description { get; init; }

    /// <summary>与当前行是否属于同一个插件（插件内部自撞）。</summary>
    public required bool SamePlugin { get; init; }

    /// <summary>格式化后的提示文本，如「截图 · 区域截图」。</summary>
    public string Describe() => SamePlugin ? Description : $"{PluginName} · {Description}";
}

/// <summary>按小工具归拢的一组热键。</summary>
public sealed class HotkeyGroup
{
    /// <summary>插件 id（损坏插件为目录名）。</summary>
    public required string PluginId { get; init; }

    /// <summary>插件展示名。</summary>
    public required string PluginName { get; init; }

    /// <summary>该插件当前的热键行，按组合键排序。</summary>
    public required IReadOnlyList<HotkeyRow> Items { get; init; }

    /// <summary>该插件是否处于运行中（未运行时热键不生效）。</summary>
    public required bool Running { get; init; }
}

/// <summary>
/// 把「扁平的插件热键列表」整理成「按小工具分组」的结构。
///
/// 抽成纯逻辑是刻意的：分组、排序、筛选这些规则最容易出错（插件一多、名字相近时
/// 尤其明显），放在这里可以完整单测，不必去驱动一个真实的窗口。
/// </summary>
public static class HotkeyGroupBuilder
{
    /// <summary>
    /// 按插件分组。组顺序按插件展示名排序，组内按组合键排序；
    /// 没有任何热键的插件不产出分组（避免界面里全是空壳）。
    /// </summary>
    public static IReadOnlyList<HotkeyGroup> Build(IEnumerable<HotkeyGroupSource> sources)
    {
        var materialized = sources.ToList();

        // 先把全部热键摊平，按"规范化组合键"建索引，
        // 这样冲突是"全体对全体"的比较，而不是只看同一插件内部
        var byCombination = ConflictIndex.Build(materialized);

        var groups = new List<HotkeyGroup>();

        foreach (var source in materialized)
        {
            var description = (PluginHotkeyBinding h) =>
                string.IsNullOrWhiteSpace(h.Description) ? h.ActionId : h.Description;

            var rows = source.Hotkeys
                .Select(h => new HotkeyRow
                {
                    ActionId = h.ActionId,
                    Combination = h.Combination,
                    Description = description(h),
                    Registered = h.Registered,
                    Error = h.Error,
                    Conflict = byCombination.Describe(source, h),
                    ConflictWith = byCombination.PeersOf(source, h),
                })
                .OrderBy(r => r.Combination, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ActionId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (rows.Count == 0) continue;

            groups.Add(new HotkeyGroup
            {
                PluginId = source.PluginId,
                PluginName = source.PluginName,
                Items = rows,
                Running = source.Running,
            });
        }

        return groups
            .OrderBy(g => g.PluginName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// 冲突索引：把「组合键 → 占用它的全部热键」建好，再回答某条热键是否撞车、撞了谁。
///
/// 为什么单独抽一个类型：冲突判定是这次改造的核心，规则本身不复杂，
/// 但边界不少（大小写、写法差异、同一插件自撞、插件未运行）。集中在这里可以完整单测，
/// 不必去驱动真实的窗口与进程。
///
/// 判定用的是 <see cref="HotkeyParser.Normalize"/> 后的组合串：
/// "alt+ctrl+x" 与 "Ctrl+Alt+X" 必须被认作同一个组合，否则界面会漏报冲突。
/// </summary>
internal sealed class ConflictIndex
{
    private readonly Dictionary<string, List<(HotkeyGroupSource Source, PluginHotkeyBinding Binding)>> _byCombination
        = new(StringComparer.OrdinalIgnoreCase);

    private ConflictIndex() { }

    public static ConflictIndex Build(IEnumerable<HotkeyGroupSource> sources)
    {
        var index = new ConflictIndex();

        foreach (var source in sources)
        {
            foreach (var binding in source.Hotkeys)
            {
                var key = HotkeyParser.Normalize(binding.Combination);
                if (key is null) continue;

                if (!index._byCombination.TryGetValue(key, out var list))
                {
                    list = [];
                    index._byCombination[key] = list;
                }
                list.Add((source, binding));
            }
        }

        return index;
    }

    /// <summary>与当前热键冲突的其他热键（不含自身）。</summary>
    public IReadOnlyList<HotkeyConflictPeer> PeersOf(HotkeyGroupSource source, PluginHotkeyBinding binding)
    {
        if (NormalizedOf(binding) is not { } key) return [];

        var peers = new List<HotkeyConflictPeer>();
        foreach (var (otherSource, otherBinding) in _byCombination[key])
        {
            if (ReferenceEquals(otherBinding, binding)) continue;

            // 同一插件内组合键相同、动作也相同的是同一条声明的重复项，不算冲突
            if (ReferenceEquals(otherSource, source)
                && string.Equals(otherBinding.ActionId, binding.ActionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var samePlugin = string.Equals(otherSource.PluginId, source.PluginId, StringComparison.OrdinalIgnoreCase);
            peers.Add(new HotkeyConflictPeer
            {
                PluginId = otherSource.PluginId,
                PluginName = otherSource.PluginName,
                Description = string.IsNullOrWhiteSpace(otherBinding.Description)
                    ? otherBinding.ActionId
                    : otherBinding.Description,
                SamePlugin = samePlugin,
            });
        }

        return peers;
    }

    /// <summary>冲突说明文本；无冲突返回 null。</summary>
    public string? Describe(HotkeyGroupSource source, PluginHotkeyBinding binding)
    {
        var peers = PeersOf(source, binding);
        if (peers.Count == 0) return null;

        var samePlugin = peers.Where(p => p.SamePlugin).ToList();
        var crossPlugin = peers.Where(p => !p.SamePlugin).ToList();

        var who = string.Join("、", peers.Select(p => p.Describe()));

        // 两类冲突的处置方式不同：自撞改本插件即可，跨插件要连同对方一起考虑
        if (crossPlugin.Count > 0 && samePlugin.Count > 0)
            return $"⚠ 与其他小工具及本小工具的热键重复：{who}";
        if (crossPlugin.Count > 0)
            return $"⚠ 与其他小工具的热键重复：{who}";
        return $"⚠ 与本小工具的其他热键重复：{who}";
    }

    private static string? NormalizedOf(PluginHotkeyBinding binding)
        => HotkeyParser.Normalize(binding.Combination);
}

/// <summary>
/// 分组器的输入：某个插件及其热键。用中性结构而不是直接吃 <see cref="PluginRuntime"/>，
/// 是为了让分组逻辑不依赖运行时对象，测试里能随手构造。
/// </summary>
public sealed class HotkeyGroupSource
{
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required bool Running { get; init; }
    public required IReadOnlyList<PluginHotkeyBinding> Hotkeys { get; init; }
}
