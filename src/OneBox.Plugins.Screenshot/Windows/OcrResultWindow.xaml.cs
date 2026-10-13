using System.Windows;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Windows;

/// <summary>文本识别结果窗口：结果可直接编辑、复制全部或复制为单行。</summary>
public partial class OcrResultWindow : Window
{
    private bool _pinned;

    public OcrResultWindow(string text, string header, string status)
    {
        InitializeComponent();
        ResultBox.Text = text;
        HeaderText.Text = header;
        StatusText.Text = status;

        Loaded += (_, _) =>
        {
            ResultBox.Focus();
            ResultBox.SelectAll();
        };
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var text = ResultBox.Text;
        StatusText.Text = ClipboardSupport.CopyText(text)
            ? $"已复制 {text.Length} 个字符"
            : "复制失败：剪贴板被占用";
    }

    private void OnCopySingleLineClick(object sender, RoutedEventArgs e)
    {
        var text = ResultBox.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        StatusText.Text = ClipboardSupport.CopyText(text)
            ? $"已复制单行文本（{text.Length} 个字符）"
            : "复制失败：剪贴板被占用";
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        PinButton.Opacity = _pinned ? 1.0 : 0.5;
        PinButton.ToolTip = _pinned ? "取消置顶" : "置顶";
    }
}
