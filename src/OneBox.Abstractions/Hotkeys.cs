using System.Text;

namespace OneBox.Abstractions;

/// <summary>热键组合串（"Ctrl+Shift+V"）的解析与格式化。宿主与插件共用同一套规则。</summary>
public static class HotkeyParser
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>RegisterHotKey 的 MOD_NOREPEAT，按住不放时不重复触发。</summary>
    public const uint MOD_NOREPEAT = 0x4000;

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
                case "CTRL": case "CONTROL": mods |= MOD_CONTROL; break;
                case "SHIFT": mods |= MOD_SHIFT; break;
                case "ALT": mods |= MOD_ALT; break;
                case "WIN": case "WINDOWS": mods |= MOD_WIN; break;
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

    /// <summary>规范化组合串（统一大小写与修饰键顺序）；无法解析时返回 null。</summary>
    public static string? Normalize(string? combo)
    {
        if (Parse(combo) is not { } parsed) return null;
        return Format(parsed.Modifiers, parsed.Vk);
    }
}

/// <summary>插件声明的一个全局热键。宿主负责注册与冲突仲裁，按下时通过 IPC 通知插件。</summary>
public sealed class HotkeyDeclaration
{
    /// <summary>插件内唯一动作标识，按下时回传给插件。</summary>
    public string ActionId { get; set; } = "";

    /// <summary>组合键文本，如 "Ctrl+Alt+V"。</summary>
    public string Combination { get; set; } = "";

    /// <summary>用途说明，显示在工具箱的"热键占用"列表里。</summary>
    public string Description { get; set; } = "";

    /// <summary>是否固定不可被用户改（仍然参与冲突仲裁）。</summary>
    public bool Fixed { get; set; }
}

/// <summary>一条热键的注册结果，宿主在应答里回传，便于插件提示用户。</summary>
public sealed class HotkeyRegistrationResult
{
    public string ActionId { get; set; } = "";
    public string Combination { get; set; } = "";
    public bool Registered { get; set; }
    public string? Error { get; set; }
}
