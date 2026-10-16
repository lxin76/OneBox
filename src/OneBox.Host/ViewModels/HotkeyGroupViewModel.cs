using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OneBox.Abstractions.Theming;
using OneBox.Host.Services;

namespace OneBox.Host.ViewModels;

/// <summary>
/// 热键页里的一行热键。只做展示，颜色与文本都在这里算好，
/// 免得把一堆判断塞进 XAML 触发器里。
/// </summary>
public sealed class HotkeyRowViewModel
{
    public HotkeyRowViewModel(HotkeyRow row)
    {
        Combination = row.Combination;
        Description = row.Description;
        Registered = row.Registered;
        Error = row.Error;
        Conflict = row.Conflict;
        ConflictWith = row.ConflictWith;

        // 冲突优先于注册状态展示：注册失败的组合（被别的插件先占住）本身也是冲突，
        // 这时"未生效：热键已被其他插件占用"对用户没有"和谁撞了"来得有用
        Status = Conflict is not null
            ? "冲突"
            : row.Registered ? "已生效" : $"未生效：{row.Error}";
    }

    public string Combination { get; }
    public string Description { get; }
    public bool Registered { get; }
    public string? Error { get; }

    /// <summary>冲突说明；无冲突为 null。</summary>
    public string? Conflict { get; }

    /// <summary>与哪些热键冲突。</summary>
    public IReadOnlyList<HotkeyConflictPeer> ConflictWith { get; }

    /// <summary>是否存在需要高亮的冲突。</summary>
    public bool HasConflict => Conflict is not null;

    /// <summary>状态文本：冲突 / 已生效 / 未生效及原因。</summary>
    public string Status { get; }

    /// <summary>冲突详情，换行展开后显示在状态下方。</summary>
    public string ConflictDetail => Conflict ?? "";

    /// <summary>
    /// 状态色。冲突用红色，这是本次改造的重点：
    /// 橙色原本同时表示"被外部程序占用"和"插件间互相冲突"，用户无法区分，
    /// 而后者是自己改个键就能解决的，必须一眼看出来。
    /// </summary>
    public Brush StatusBrush => HasConflict ? HotkeyBrushes.Error : Registered ? HotkeyBrushes.Ok : HotkeyBrushes.Warn;

    /// <summary>整行的强调色：冲突行用红色，其余按状态色。</summary>
    public Brush AccentBrush => StatusBrush;

    /// <summary>行背景：冲突时给一层淡淡的红，让整行在列表里跳出来。</summary>
    public Brush RowBackground => HasConflict ? HotkeyBrushes.ErrorWash : HotkeyBrushes.Row;
}

/// <summary>
/// 热键页共用的状态色。取自当前主题，且都是已冻结的画刷——
/// 绑定会在渲染线程读取，未冻结的画刷跨线程使用会抛异常。
///
/// 因为这些颜色是算出来绑定到属性的（不像 XAML 里写 DynamicResource 会自动更新），
/// 换主题后由工具箱重建这份视图模型（见 ToolboxWindow.OnThemeChanged）。
/// </summary>
internal static class HotkeyBrushes
{
    public static Brush Ok => ThemeManager.Brush(ThemeKeys.Ok);
    public static Brush Warn => ThemeManager.Brush(ThemeKeys.Warn);
    public static Brush Muted => ThemeManager.Brush(ThemeKeys.TextMuted);

    /// <summary>冲突高亮：用错误色，且与"被外部程序占用"的警告色区分开。</summary>
    public static Brush Error => ThemeManager.Brush(ThemeKeys.Error);

    /// <summary>冲突行的底色（主题里的淡红）。</summary>
    public static Brush ErrorWash => ThemeManager.Brush(ThemeKeys.ErrorWash);

    /// <summary>普通行底色（即卡片色）。</summary>
    public static Brush Row => ThemeManager.Brush(ThemeKeys.Card);
}

/// <summary>
/// 热键页里的一个分组：某个小工具及其热键。
/// 分组可折叠，默认展开——用户打开热键页就是想看清单，
/// 一进来全是收起的标题反而要多点几下。
/// </summary>
public partial class HotkeyGroupViewModel : ObservableObject
{
    public HotkeyGroupViewModel(HotkeyGroup group, ImageSource icon)
    {
        PluginId = group.PluginId;
        PluginName = group.PluginName;
        Icon = icon;

        _items = [.. group.Items.Select(r => new HotkeyRowViewModel(r))];
        AllItems = _items;

        RegisteredCount = AllItems.Count(i => i.Registered);
        FailedCount = AllItems.Count - RegisteredCount;
        ConflictCount = AllItems.Count(i => i.HasConflict);

        CountText = ConflictCount > 0
            ? $"{AllItems.Count} 个热键 · {ConflictCount} 个冲突"
            : FailedCount == 0
                ? $"{AllItems.Count} 个热键"
                : $"{AllItems.Count} 个热键 · {FailedCount} 个未生效";

        Subtitle = !group.Running
            ? "未运行 · 热键已让出"
            : ConflictCount > 0
                ? $"{ConflictCount} 个热键与其他热键重复（需改键）"
                : FailedCount == 0
                    ? "全部已生效"
                    : $"{FailedCount} 个未生效（可能被其他软件占用）";

        // 三级健康状态：冲突（红）> 未生效（橙）> 正常（绿）；未运行单独用灰
        HealthText = !group.Running
            ? "未运行"
            : ConflictCount > 0 ? "有冲突" : FailedCount == 0 ? "正常" : "有未生效";
        HealthBrush = !group.Running
            ? HotkeyBrushes.Muted
            : ConflictCount > 0 ? HotkeyBrushes.Error
            : FailedCount == 0 ? HotkeyBrushes.Ok
            : HotkeyBrushes.Warn;
    }

    public string PluginId { get; }
    public string PluginName { get; }
    public ImageSource Icon { get; }

    /// <summary>
    /// 分组下当前要显示的热键行。筛选时会被换成命中的子集，
    /// 因此用可写属性而不是只读集合。
    /// </summary>
    public IReadOnlyList<HotkeyRowViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    private IReadOnlyList<HotkeyRowViewModel> _items = [];

    /// <summary>成功注册的热键数。</summary>
    public int RegisteredCount { get; }

    /// <summary>未能注册的热键数。</summary>
    public int FailedCount { get; }

    /// <summary>存在冲突的热键数（与平台内其他热键组合重复）。</summary>
    public int ConflictCount { get; }

    /// <summary>分组头右侧的数量徽标文本。</summary>
    public string CountText { get; }

    /// <summary>分组头副标题（运行状态与冲突概况）。</summary>
    public string Subtitle { get; }

    /// <summary>分组头右侧的健康状态文本。</summary>
    public string HealthText { get; }

    /// <summary>健康状态色。</summary>
    public Brush HealthBrush { get; }

    private bool _isExpanded = true;

    /// <summary>是否展开显示该组的热键。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>本组的全部热键行（不受筛选影响）。</summary>
    public IReadOnlyList<HotkeyRowViewModel> AllItems { get; }

    /// <summary>
    /// 应用筛选：命中插件名时整组保留，否则只留下命中组合键或用途的行。
    /// </summary>
    /// <returns>是否命中（false 表示该组应从界面上隐去）。</returns>
    public bool ApplyFilter(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            Items = AllItems;
            return AllItems.Count > 0;
        }

        var needle = keyword.Trim();

        if (Contains(PluginName, needle) || Contains(PluginId, needle))
        {
            Items = AllItems;
            return AllItems.Count > 0;
        }

        var matched = AllItems
            .Where(i => Contains(i.Combination, needle) || Contains(i.Description, needle))
            .ToList();

        Items = matched;
        return matched.Count > 0;
    }

    private static bool Contains(string? text, string needle)
        => !string.IsNullOrEmpty(text)
           && text.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
}