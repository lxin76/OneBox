using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using OneBox.Abstractions.Theming;
using OneBox.Host.Services;

namespace OneBox.Host.ViewModels;

/// <summary>
/// 热键页里的一行热键。只做展示，颜色与文本都在这里算好，
/// 免得把一堆判断塞进 XAML 触发器里。
/// </summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    public HotkeyRowViewModel(HotkeyRow row)
    {
        Combination = row.Combination;
        Description = row.Description;
        Registered = row.Registered;
        Error = row.Error;
        Conflict = row.Conflict;
        ConflictWith = row.ConflictWith;
        PluginId = row.PluginId;
        ActionId = row.ActionId;
        DefaultCombination = string.IsNullOrWhiteSpace(row.DefaultCombination) ? row.Combination : row.DefaultCombination;
        IsCustomized = row.IsCustomized;
        IsFixed = row.IsFixed;
        OccupiedByOtherApp = row.OccupiedByOtherApp;

        // 冲突优先于注册状态展示：注册失败的组合（被别的插件先占住）本身也是冲突，
        // 这时"未生效：热键已被其他插件占用"对用户没有"和谁撞了"来得有用
        Status = Conflict is not null
            ? "冲突"
            : row.Registered
                ? "已生效"
                : row.OccupiedByOtherApp ? "与其他软件冲突" : $"未生效：{row.Error}";
        StatusBrush = Conflict is not null ? HotkeyBrushes.Error : row.Registered ? HotkeyBrushes.Ok : HotkeyBrushes.Warn;
        RowBackground = Conflict is not null ? HotkeyBrushes.ErrorWash : HotkeyBrushes.Row;
    }

    /// <summary>所属插件 id。</summary>
    public string PluginId { get; }

    /// <summary>动作标识。</summary>
    public string ActionId { get; }

    /// <summary>插件声明的默认键。</summary>
    public string DefaultCombination { get; }

    /// <summary>是否被用户改过键（此时才显示"恢复默认"）。</summary>
    public bool IsCustomized { get; }

    /// <summary>是否固定不可改（改键按钮置灰）。</summary>
    public bool IsFixed { get; }

    /// <summary>该热键因被其他软件占用而未能生效。</summary>
    public bool OccupiedByOtherApp { get; }

    /// <summary>外部冲突的提示文案（无外部冲突时为空）。</summary>
    public string ExternalConflictDetail => OccupiedByOtherApp
        ? $"⚠ 与其他软件冲突：{Combination} 已被其他程序注册，与它同时运行时此功能无法触发。若两者不同时运行则无影响。"
        : "";

    /// <summary>
    /// 本次改键操作遇到的外部冲突提示。与 <see cref="ExternalConflictDetail"/> 不同，
    /// 这里描述的是"你刚选的这个键被别的软件占着"，允许保存，但要用户明确知情。
    /// </summary>
    public string? ExternalNotice
    {
        get => _externalNotice;
        set
        {
            if (SetProperty(ref _externalNotice, value)) OnPropertyChanged(nameof(HasExternalNotice));
        }
    }

    private string? _externalNotice;

    /// <summary>是否有需要醒目标出的外部冲突提示。</summary>
    public bool HasExternalNotice => !string.IsNullOrWhiteSpace(ExternalNotice);

    /// <summary>是否可改键。</summary>
    public bool CanEdit => !IsFixed;

    /// <summary>
    /// 是否处于录制中（行内显示"请按下组合键…"）。
    ///
    /// 变化时必须把**所有由它派生的属性**都通知一遍：键位文字、录入提示、按钮文字。
    /// 早先只通知了 EditHint，而界面绑定的是 DisplayCombination，
    /// 于是录制状态在数据上已经成立、界面却纹丝不动——用户以为"点了没反应"。
    /// </summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (!SetProperty(ref _isRecording, value)) return;
            OnPropertyChanged(nameof(EditHint));
            OnPropertyChanged(nameof(DisplayCombination));
            OnPropertyChanged(nameof(RecordButtonText));
            OnPropertyChanged(nameof(RowHighlightBrush));
            OnPropertyChanged(nameof(IsEditable));
        }
    }

    private bool _isRecording;

    /// <summary>「改键」按钮上的文字：录制中变成「取消」，否则是「改键」。</summary>
    public string RecordButtonText => IsRecording ? "取消" : "改键";

    /// <summary>录制中的整行底色高亮，让用户一眼看出"正在听按键"。</summary>
    public Brush RowHighlightBrush => IsRecording
        ? ThemeManager.Brush(ThemeKeys.AccentSoft)
        : RowBackground;

    /// <summary>录制中不允许再点其他行的改键按钮，避免钩子状态错乱。</summary>
    public bool IsEditable => !IsRecording;

    /// <summary>
    /// 改键时的即时提示（冲突／被占用／已保存）。与 Conflict 分开：
    /// Conflict 是"当前已生效配置的问题"，这里是"这次操作的结果"，不会随重建保留。
    /// </summary>
    public string? EditMessage
    {
        get => _editMessage;
        set
        {
            if (SetProperty(ref _editMessage, value)) OnPropertyChanged(nameof(HasEditMessage));
        }
    }

    private string? _editMessage;

    /// <summary>提示是否为错误（决定显示颜色）。</summary>
    public bool EditMessageIsError
    {
        get => _editMessageIsError;
        set => SetProperty(ref _editMessageIsError, value);
    }

    private bool _editMessageIsError;

    public bool HasEditMessage => !string.IsNullOrWhiteSpace(EditMessage);

    /// <summary>录入框上的提示文字。</summary>
    public string EditHint => IsRecording ? "请按下组合键…（Esc 取消）" : "";

    /// <summary>该行当前显示的键位（录制中显示提示）。</summary>
    public string DisplayCombination => IsRecording ? "按下新组合键…" : Combination;

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
    public Brush StatusBrush { get; }

    /// <summary>整行的强调色：冲突行用红色，其余按状态色。</summary>
    public Brush AccentBrush => StatusBrush;

    /// <summary>行背景：冲突时给一层淡淡的红，让整行在列表里跳出来。</summary>
    public Brush RowBackground { get; }

    /// <summary>改键提示的颜色（错误红／成功绿）。</summary>
    public Brush EditMessageBrush => EditMessageIsError ? HotkeyBrushes.Error : HotkeyBrushes.Ok;
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