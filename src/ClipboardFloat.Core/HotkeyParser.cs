using System.Text;

namespace ClipboardFloat.Core;

/// <summary>热键组合串解析/格式化，如 "Ctrl+Shift+V"。</summary>
public static class HotkeyParser
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>解析 "Ctrl+Shift+V" 形式（修饰键在前、最后为单字符或 F1-F24）。失败返回 null。</summary>
    public static (uint Modifiers, uint Vk)? Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        uint mods = 0;
        uint vk = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL": mods |= MOD_CONTROL; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "ALT": mods |= MOD_ALT; break;
                case "WIN": mods |= MOD_WIN; break;
                default:
                    if (i != parts.Length - 1) return null; // 修饰键必须在前面
                    vk = parts[i].Length == 1
                        ? char.ToUpperInvariant(parts[i][0])
                        : (parts[i].StartsWith("F", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(parts[i][1..], out int fn) && fn is >= 1 and <= 24
                                ? (uint)(0x70 + fn - 1)
                                : 0);
                    break;
            }
        }

        if (mods == 0 || vk == 0) return null;
        return (mods, vk);
    }

    /// <summary>格式化为 "Ctrl+Shift+V" 显示文本。</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var sb = new StringBuilder();
        if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(vk is >= 0x70 and <= 0x87 ? "F" + (vk - 0x70 + 1) : (char)vk);
        return sb.ToString();
    }
}
