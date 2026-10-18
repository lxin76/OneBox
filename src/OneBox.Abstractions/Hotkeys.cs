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

    /// <summary>
    /// 非字符键的虚拟键码表。录制框会把用户按下的键转成这里的名字，
    /// 因此这里的写法必须与 <see cref="Format"/> 的输出一致，否则"录进去"和"读回来"对不上。
    /// </summary>
    private static readonly Dictionary<string, uint> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPACE"] = 0x20, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["ESC"] = 0x1B,
        ["BACKSPACE"] = 0x08, ["INSERT"] = 0x2D, ["DELETE"] = 0x2E,
        ["HOME"] = 0x24, ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["NUMPAD0"] = 0x60, ["NUMPAD1"] = 0x61, ["NUMPAD2"] = 0x62, ["NUMPAD3"] = 0x63,
        ["NUMPAD4"] = 0x64, ["NUMPAD5"] = 0x65, ["NUMPAD6"] = 0x66, ["NUMPAD7"] = 0x67,
        ["NUMPAD8"] = 0x68, ["NUMPAD9"] = 0x69,
        ["MULTIPLY"] = 0x6A, ["ADD"] = 0x6B, ["SUBTRACT"] = 0x6D,
        ["DECIMAL"] = 0x6E, ["DIVIDE"] = 0x6F,
        ["OEM_1"] = 0xBA, ["OEM_PLUS"] = 0xBB, ["OEM_COMMA"] = 0xBC, ["OEM_MINUS"] = 0xBD,
        ["OEM_PERIOD"] = 0xBE, ["OEM_2"] = 0xBF, ["OEM_3"] = 0xC0,
        ["OEM_4"] = 0xDB, ["OEM_5"] = 0xDC, ["OEM_6"] = 0xDD, ["OEM_7"] = 0xDE,
    };

    /// <summary>虚键码 → 组合串里的键名（<see cref="SpecialKeys"/> 的反向表，构造时生成）。</summary>
    private static readonly Dictionary<uint, string> SpecialKeyNames =
        SpecialKeys.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// 解析 "Ctrl+Shift+V" 形式（修饰键在前、最后为一个键）。失败返回 null。
    ///
    /// 支持三类主键：单个字母或数字（"V"、"5"）、F1-F24、以及
    /// <see cref="SpecialKeys"/> 里的名字（"SPACE"、"LEFT"、"OEM_COMMA" 等）。
    /// 修饰键必须至少有一个：RegisterHotKey 不接受纯按键，纯字母键既注册不上也会抢走打字。
    /// </summary>
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
                    if (vk != 0) return null;               // 不能有两个主键
                    vk = ParseMainKey(parts[i]);
                    break;
            }
        }

        if (mods == 0 || vk == 0) return null;
        return (mods, vk);
    }

    /// <summary>解析主键部分（最后一个 "+" 之后的那段）。无法识别返回 0。</summary>
    private static uint ParseMainKey(string text)
    {
        if (text.Length == 1)
        {
            char c = char.ToUpperInvariant(text[0]);
            return c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : 0u;
        }

        if (text.Length is >= 2 and <= 3
            && text[0] is 'F' or 'f'
            && int.TryParse(text[1..], out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);

        return SpecialKeys.TryGetValue(text, out var vk) ? vk : 0;
    }

    /// <summary>格式化为 "Ctrl+Shift+V" 显示文本。</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var sb = new StringBuilder();
        if ((modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((modifiers & MOD_ALT) != 0) sb.Append("Alt+");
        if ((modifiers & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(FormatKey(vk));
        return sb.ToString();
    }

    /// <summary>把虚键码格式化成组合串里的键名（无修饰键部分）。</summary>
    public static string FormatKey(uint vk)
    {
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        return SpecialKeyNames.TryGetValue(vk, out var name) ? name : "0x" + vk.ToString("X2");
    }

    /// <summary>组合键是否带有真正的修饰键（Ctrl/Alt/Shift/Win 至少一个）。</summary>
    public static bool HasModifier(uint modifiers)
        => (modifiers & (MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN)) != 0;

    /// <summary>规范化组合串（统一大小写与修饰键顺序）；无法解析时返回 null。</summary>
    public static string? Normalize(string? combo)
    {
        if (Parse(combo) is not { } parsed) return null;
        return Format(parsed.Modifiers, parsed.Vk);
    }

    /// <summary>两个组合串是否表示同一个热键（"alt+ctrl+x" 与 "Ctrl+Alt+X" 视为相同）。</summary>
    public static bool SameCombination(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// 是否固定不可被用户改（仍然参与冲突仲裁）。
    /// 固定热键一般是"救急"性质的（例如修复卡住的修饰键），用户误改成别的键就再也救不回来了。
    /// </summary>
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
