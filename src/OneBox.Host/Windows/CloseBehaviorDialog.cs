using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OneBox.Abstractions.Theming;
using OneBox.Host.Services;

namespace OneBox.Host.Windows;

/// <summary>
/// 第一次关闭窗口时的快捷确认：关窗口要不要顺便退出 OneBox。
///
/// 只问这一次——选择会写进宿主设置（<see cref="HostSettings.CloseWindowAction"/>），
/// 之后按它执行，想改随时可以去设置中心。之所以要问：托盘常驻的程序里
/// "关掉窗口后进程还在不在"两种习惯的人都有，与其替用户猜，不如问一次。
/// </summary>
internal sealed class CloseBehaviorDialog : Window
{
    /// <summary>用户的选择（<see cref="CloseWindowActions"/> 取值）；取消时为 null。</summary>
    private string? _choice;

    private CloseBehaviorDialog()
    {
        Title = "关闭窗口";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontSize = 12;
        SetResourceReference(BackgroundProperty, ThemeKeys.ResourceKey(ThemeKeys.Surface));
        SetResourceReference(ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.Text));

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

        panel.Children.Add(new TextBlock
        {
            Text = "关闭这个窗口时，要顺便退出 OneBox 吗？",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var tip = new TextBlock
        {
            Text = "只问这一次，之后可以在「设置 → 宿主 → 宿主行为」里随时更改。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.TextMuted));
        panel.Children.Add(tip);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(MakeButton("取消", false, () => _choice = null));
        buttons.Children.Add(MakeButton("退出 OneBox", false, () => _choice = CloseWindowActions.ExitApp));
        buttons.Children.Add(MakeButton("只关闭窗口", true, () => _choice = CloseWindowActions.CloseWindowOnly));
        panel.Children.Add(buttons);

        Content = panel;

        // Esc 等同取消：窗口留着，别把人卡在这
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _choice = null;
            Close();
        };
    }

    private Button MakeButton(string text, bool primary, Action assign)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
        };

        // 默认选项（只关窗口）用强调色点出来：不点它也不会误退程序
        button.SetResourceReference(
            Control.BackgroundProperty,
            ThemeKeys.ResourceKey(primary ? ThemeKeys.AccentSoft : ThemeKeys.ButtonBg));
        button.SetResourceReference(
            Control.ForegroundProperty,
            ThemeKeys.ResourceKey(primary ? ThemeKeys.AccentText : ThemeKeys.Text));
        button.SetResourceReference(Control.BorderBrushProperty, ThemeKeys.ResourceKey(ThemeKeys.Border));

        button.Click += (_, _) =>
        {
            assign();
            Close();
        };
        return button;
    }

    /// <summary>询问关闭窗口的行为；返回 <see cref="CloseWindowActions"/> 取值，用户取消时返回 null。</summary>
    public static string? Ask(Window owner)
    {
        var dialog = new CloseBehaviorDialog { Owner = owner };
        dialog.ShowDialog();
        return dialog._choice;
    }
}
