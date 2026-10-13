using System.Windows;
using System.Windows.Input;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>
/// 热键录制：把键盘输入转成 "Ctrl+Alt+A" 形式的组合串。
/// 与宿主注册用的 <c>HotkeyParser</c> 保持同一套格式。
/// </summary>
public sealed class HotkeyRecorder
{
    private readonly Window _owner;
    private readonly Action<string> _onRecorded;
    private readonly Action _onCancel;
    private bool _recording;

    public HotkeyRecorder(Window owner, Action<string> onRecorded, Action onCancel)
    {
        _owner = owner;
        _onRecorded = onRecorded;
        _onCancel = onCancel;
    }

    public bool IsRecording => _recording;

    /// <summary>开始录制；期间拦截窗口的键盘输入。</summary>
    public void Start()
    {
        if (_recording) return;
        _recording = true;
        _owner.PreviewKeyDown += OnPreviewKeyDown;
        _owner.PreviewKeyUp += OnPreviewKeyUp;
    }

    public void Stop()
    {
        if (!_recording) return;
        _recording = false;
        _owner.PreviewKeyDown -= OnPreviewKeyDown;
        _owner.PreviewKeyUp -= OnPreviewKeyUp;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return; // 只按了修饰键，继续等主键
        }

        if (key == Key.Escape)
        {
            Stop();
            _onCancel();
            return;
        }

        var combo = Format(key, Keyboard.Modifiers);
        if (combo is null) return; // 缺少修饰键或按键不受支持，忽略这次输入

        Stop();
        _onRecorded(combo);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e) => e.Handled = true;

    /// <summary>把按键与修饰键转成组合串；无法表示时返回 null。</summary>
    public static string? Format(Key key, ModifierKeys modifiers)
    {
        // 必须至少有一个修饰键，避免注册出会吞掉普通输入的全局热键
        if (modifiers == ModifierKeys.None) return null;

        var main = MainKeyName(key);
        if (main is null) return null;

        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(main);

        return string.Join("+", parts);
    }

    /// <summary>
    /// 主键名。必须与宿主注册用的 <c>HotkeyParser</c> 保持一致：
    /// 只支持单个字符（字母/数字/符号）或 F1-F24，其它按键无法注册为全局热键。
    /// </summary>
    private static string? MainKeyName(Key key)
    {
        // 字母与数字
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();

        // 功能键 F1-F24
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();

        return key switch
        {
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.NumPad0 => "0",
            Key.NumPad1 => "1",
            Key.NumPad2 => "2",
            Key.NumPad3 => "3",
            Key.NumPad4 => "4",
            Key.NumPad5 => "5",
            Key.NumPad6 => "6",
            Key.NumPad7 => "7",
            Key.NumPad8 => "8",
            Key.NumPad9 => "9",
            _ => null,
        };
    }
}
