using System.Text;
using System.Windows.Input;

namespace OneBox.Plugins.TextPin.Services;

/// <summary>把 WPF 按键事件转为 "Ctrl+Shift+V" 形式的热键串（设置页录制用）。</summary>
public static class HotkeyInputParser
{
    /// <summary>从按键事件生成组合串；纯修饰键/无效组合返回 null。</summary>
    public static string? FromWpfKey(Key key, ModifierKeys modifiers, Key systemKey = Key.None)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin
            or Key.None or Key.Escape)
            return null;

        var real = key == Key.System && systemKey != Key.None ? systemKey : key;

        var sb = new StringBuilder();
        if ((modifiers & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((modifiers & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        if ((modifiers & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((modifiers & ModifierKeys.Windows) != 0) sb.Append("Win+");

        // 必须至少包含一个修饰键（RegisterHotKey 不允许纯按键）
        if (sb.Length == 0) return null;

        sb.Append(KeyToText(real));
        return sb.ToString();
    }

    private static string KeyToText(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();
        return key.ToString();
    }
}
