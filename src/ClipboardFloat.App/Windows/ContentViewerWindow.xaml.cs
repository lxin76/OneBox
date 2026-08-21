using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardFloat.App.Services;
using ClipboardFloat.Core;

namespace ClipboardFloat.App.Windows;

/// <summary>
/// 完整内容查看窗口：显示历史条目的完整内容（超出可滚动），支持复制与置顶。
/// </summary>
public partial class ContentViewerWindow : Window
{
    private readonly ClipItem _item;
    private readonly Action _markSelfWrite;
    private bool _pinned;
    private DispatcherTimer? _resetTimer;

    public ContentViewerWindow(ClipItem item, Action markSelfWrite, bool autoPin = false)
    {
        InitializeComponent();
        _item = item;
        _markSelfWrite = markSelfWrite;
        ApplyPinState(autoPin);
        ApplyContent();
    }

    private void ApplyContent()
    {
        switch (_item.Kind)
        {
            case ClipKind.Image when _item.ImagePng is { Length: > 0 } png:
                TypeText.Text = "图片";
                ContentBox.Visibility = Visibility.Collapsed;
                FilesText.Visibility = Visibility.Collapsed;
                ImageScroller.Visibility = Visibility.Visible;
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = new MemoryStream(png);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    ContentImage.Source = bi;
                }
                catch
                {
                    ImageScroller.Visibility = Visibility.Collapsed;
                    ContentBox.Visibility = Visibility.Visible;
                    ContentBox.Text = "[图片内容无法显示]";
                }
                break;

            case ClipKind.Files when _item.Files is { Count: > 0 } files:
                TypeText.Text = $"文件 ({files.Count})";
                ContentBox.Visibility = Visibility.Collapsed;
                ImageScroller.Visibility = Visibility.Collapsed;
                FilesText.Visibility = Visibility.Visible;
                FilesText.Text = string.Join(Environment.NewLine, files);
                break;

            default:
                TypeText.Text = _item.Kind switch
                {
                    ClipKind.Html => "HTML",
                    ClipKind.Rtf => "富文本",
                    _ => "文本",
                };
                ContentBox.Text = _item.Text ?? _item.Preview;
                break;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        _markSelfWrite();
        ClipboardWriter.SetClipboardAsync(_item);
        CopyButton.Content = "已复制 ✓";
        _resetTimer?.Stop();
        _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _resetTimer.Tick += (_, _) =>
        {
            CopyButton.Content = "复制全部";
            _resetTimer!.Stop();
        };
        _resetTimer.Start();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
        => ApplyPinState(!_pinned);

    private void ApplyPinState(bool pinned)
    {
        _pinned = pinned;
        Topmost = pinned;
        PinButton.Opacity = pinned ? 1.0 : 0.5;
        PinButton.ToolTip = pinned ? "取消置顶" : "置顶";
    }
}
