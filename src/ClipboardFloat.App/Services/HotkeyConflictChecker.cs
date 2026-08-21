namespace ClipboardFloat.App.Services;

/// <summary>热键冲突检查（纯逻辑，可单测）。</summary>
public static class HotkeyConflictChecker
{
    /// <summary>内置固定热键（修复键盘输入），不可被其他热键占用。</summary>
    public const string FixedResetHotkey = "Ctrl+Alt+K";

    /// <summary>
    /// 检查新热键是否与自身其他快捷键重复。无冲突返回 null。
    /// <paramref name="others"/> 应排除当前正在修改的项。
    /// </summary>
    public static string? FindSelfConflict(string newCombo, IReadOnlyList<string> others)
    {
        if (string.Equals(newCombo, FixedResetHotkey, StringComparison.OrdinalIgnoreCase))
            return $"⚠ 与内置『修复键盘输入』({FixedResetHotkey}) 重复";

        foreach (var other in others)
        {
            if (string.Equals(other, newCombo, StringComparison.OrdinalIgnoreCase))
                return $"⚠ 与自身其他快捷键重复：{newCombo}";
        }
        return null;
    }

    /// <summary>其他软件占用提示文案。</summary>
    public const string OtherAppConflictMessage =
        "⚠ 该快捷键已被其他软件占用，可能无法生效（已保存，可在其他软件中修改其快捷键，或换一个组合键）";
}
