using System.Windows;
using System.Windows.Controls;
using OneBox.Abstractions.Theming;

namespace OneBox.Host.Windows;

/// <summary>卸载确认对话框：让用户决定是否连同插件的私有数据一起删除。</summary>
public sealed class UninstallDialog : Window
{
    private readonly CheckBox _deleteData;
    private bool _confirmed;

    private UninstallDialog(string pluginName, bool broken)
    {
        Title = "卸载小工具";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontSize = 12;
        // 代码构建的窗口也走主题资源：SetResourceReference 就是代码版的 DynamicResource
        SetResourceReference(BackgroundProperty, ThemeKeys.ResourceKey(ThemeKeys.Surface));
        SetResourceReference(ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.Text));

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

        panel.Children.Add(new TextBlock
        {
            Text = $"确定要卸载「{pluginName}」吗？",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var description = new TextBlock
        {
            Text = broken
                ? "该插件清单无法解析，卸载会删除它的插件目录。"
                : "卸载会停止插件进程、删除插件目录并释放它占用的全局热键。重新安装即可恢复。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.TextMuted));
        panel.Children.Add(description);

        _deleteData = new CheckBox
        {
            Content = "同时删除该小工具的数据与设置（不可恢复）",
            IsChecked = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(_deleteData);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = MakeButton("取消", false);
        cancel.Click += (_, _) => Close();
        var ok = MakeButton("卸载", true);
        ok.Click += (_, _) =>
        {
            _confirmed = true;
            Close();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 5),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1),
        };

        // 主动作（卸载）用错误色强调：这是不可逆操作，颜色本身就是提醒
        button.SetResourceReference(
            Control.BackgroundProperty,
            ThemeKeys.ResourceKey(primary ? ThemeKeys.ButtonHover : ThemeKeys.ButtonBg));
        button.SetResourceReference(
            Control.ForegroundProperty,
            ThemeKeys.ResourceKey(primary ? ThemeKeys.Error : ThemeKeys.Text));
        button.SetResourceReference(Control.BorderBrushProperty, ThemeKeys.ResourceKey(ThemeKeys.Border));
        return button;
    }

    /// <summary>显示对话框；用户取消时返回 null。</summary>
    public static (bool DeleteUserData, bool Confirmed)? Ask(Window owner, string pluginName, bool broken)
    {
        var dialog = new UninstallDialog(pluginName, broken) { Owner = owner };
        dialog.ShowDialog();
        return dialog._confirmed ? (dialog._deleteData.IsChecked == true, true) : null;
    }
}