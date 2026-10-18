using OneBox.Abstractions;

namespace OneBox.Host.Services;

/// <summary>用户改键的一条"占位"信息：某个小工具的某个动作当前占着哪个键。</summary>
public sealed class HotkeyOccupant
{
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required string ActionId { get; init; }
    public required string Description { get; init; }
    public required string Combination { get; init; }

    /// <summary>是否为用户自定义的键（否则是插件声明的默认键）。</summary>
    public bool IsCustom { get; init; }

    /// <summary>是否固定不可改（固定热键被撞时，只能改另一头）。</summary>
    public bool Fixed { get; init; }
}

/// <summary>改键冲突的另一个占用者（用于在界面上点名"和谁撞了"）。</summary>
public sealed class HotkeyConflictCandidate
{
    public required string PluginName { get; init; }
    public required string Description { get; init; }
    public required string Combination { get; init; }

    public string Describe() => $"{PluginName} · {Description}（{Combination}）";
}

/// <summary>改键检查结果。</summary>
public sealed class HotkeyChangeCheck
{
    /// <summary>是否允许保存这一改动。</summary>
    public bool CanApply => Error is null;

    /// <summary>阻断保存的原因（不合法的组合、与平台内其他热键重复等）。</summary>
    public string? Error { get; init; }

    /// <summary>平台内与之冲突的其他热键。</summary>
    public IReadOnlyList<HotkeyConflictCandidate> Conflicts { get; init; } = [];

    /// <summary>
    /// 该组合键当前被其他软件占用（探测结果）。
    ///
    /// 之所以**不阻断保存**：不同软件未必同时运行，错开使用就不会真的撞车，
    /// 用户往往比探测更清楚自己的使用习惯。但这是个需要显眼提示的风险，
    /// 因此单独成一项，由界面用醒目的警示呈现，而不是混在普通提示里。
    ///
    /// 为 null 表示没探测（例如已经因为内部冲突被拒）。
    /// </summary>
    public bool? OccupiedByOtherApp { get; set; }

    /// <summary>被其他软件占用时的提示文案。</summary>
    public string? ExternalConflictMessage { get; set; }

    /// <summary>是否存在需要醒目提示的外部冲突。</summary>
    public bool HasExternalConflict => OccupiedByOtherApp == true;

    /// <summary>
    /// 非阻断性提醒（目前用于外部冲突）。保留该属性是为了让老调用方仍能拿到一句话摘要。
    /// </summary>
    public string? Warning => ExternalConflictMessage;
}

/// <summary>
/// 改键时的冲突检查。纯逻辑，不碰 WPF/Win32，便于完整单测。
///
/// 检查分三层，顺序即优先级：
/// 1. 组合键本身能不能用（能否解析、是否带修饰键、是否是仅 Windows 键）；
/// 2. 是否与平台内其他热键重复（这才是"冲突"的主体，必须挡住）；
/// 3. 是否可能被其他软件占用（警告，由调用方用 Win32 试探后填进来）。
///
/// 把第 2 层单独抽出来，是因为它的规则最容易出错：同一个动作改了键之后，
/// 它"原来那个键"要让出来参与比较，而不是继续占位；固定热键、未运行插件的热键
/// 也都还参与仲裁（见 <see cref="PluginManager.RebuildHotkeys"/>）。
/// </summary>
public static class HotkeyChangeValidator
{
    /// <summary>
    /// 检查把 <paramref name="pluginId"/>/<paramref name="actionId"/> 改为 <paramref name="combination"/> 是否可行。
    /// </summary>
    /// <param name="combination">用户想设置的新组合键。</param>
    /// <param name="pluginId">动作所属插件 id。</param>
    /// <param name="actionId">动作 id。</param>
    /// <param name="occupants">
    /// 平台内全部热键的当前占位（含未运行插件的热键、也含本动作自己）。
    /// 本动作会先被剔除，因此不必由调用方过滤。
    /// </param>
    public static HotkeyChangeCheck Check(
        string? combination,
        string pluginId,
        string actionId,
        IEnumerable<HotkeyOccupant> occupants)
    {
        if (string.IsNullOrWhiteSpace(combination))
            return new HotkeyChangeCheck { Error = "请先按下要设置的组合键" };

        if (HotkeyParser.Parse(combination) is not { } parsed)
            return new HotkeyChangeCheck
            {
                Error = "无法识别的组合键：至少需要一个修饰键（Ctrl/Alt/Shift/Win）加一个按键",
            };

        // 只有 Win 键 + 普通字符是系统保留区域：Win 键组合会被系统或输入法先吃掉，
        // 即便注册成功也时灵时不灵，不如在改键时就挡下来
        if ((parsed.Modifiers & HotkeyParser.MOD_WIN) != 0
            && (parsed.Modifiers & (HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_ALT | HotkeyParser.MOD_SHIFT)) == 0)
            return new HotkeyChangeCheck { Error = "不建议只用 Win+按键：这类组合常被系统或输入法先接管，基本无法生效" };

        var normalized = HotkeyParser.Format(parsed.Modifiers, parsed.Vk);

        // 本动作改键后，它原来占的键要让出来；比较基数因此排除它自己
        var others = occupants
            .Where(o => !IsSelf(o, pluginId, actionId))
            .Where(o => !string.IsNullOrWhiteSpace(o.Combination))
            .ToList();

        var conflicts = others
            .Where(o => HotkeyParser.SameCombination(o.Combination, normalized))
            .Select(o => new HotkeyConflictCandidate
            {
                PluginName = o.PluginName,
                Description = o.Description,
                Combination = o.Combination,
            })
            .ToList();

        if (conflicts.Count > 0)
        {
            var who = string.Join("、", conflicts.Select(c => c.Describe()));
            return new HotkeyChangeCheck
            {
                Error = $"{normalized} 已被占用，与 {who} 冲突，请换一个组合键",
                Conflicts = conflicts,
            };
        }

        return new HotkeyChangeCheck();
    }

    /// <summary>
    /// 把外部占用探测的结果合并进检查结果。
    ///
    /// 关键点：**只提示、不阻断**。已因内部冲突被拒的检查结果不再叠加外部结论
    /// （用户得先解决内部重复），而外部占用本身只抬高提示级别。
    /// </summary>
    public static HotkeyChangeCheck WithAvailabilityProbe(
        HotkeyChangeCheck check, bool? available, string combination)
    {
        if (!check.CanApply || available is null) return check;

        var occupied = available is false;
        check.OccupiedByOtherApp = occupied;
        check.ExternalConflictMessage = occupied
            ? $"⚠ {combination} 已被其他软件占用：现在设置它，与那个软件同时运行时 OneBox 的这个功能将无法触发。" +
              "若两者不会同时运行，可以先这样设置（此提示不影响保存）。"
            : null;

        return check;
    }

    private static bool IsSelf(HotkeyOccupant occupant, string pluginId, string actionId)
        => string.Equals(occupant.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(occupant.ActionId, actionId, StringComparison.OrdinalIgnoreCase);
}
