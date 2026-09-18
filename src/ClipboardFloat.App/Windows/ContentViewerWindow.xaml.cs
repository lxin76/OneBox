using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardFloat.App.Services;
using ClipboardFloat.Core;

namespace ClipboardFloat.App.Windows;

/// <summary>
/// 完整内容查看窗口：显示历史条目的完整内容（超出可滚动），支持复制与置顶。
/// 文本内容支持“同段智能选中”（双击 + Ctrl+双击逐层扩大）；内容为 JSON 时额外提供属性面板。
/// </summary>
public partial class ContentViewerWindow : Window
{
    /// <summary>JSON 解析上限，避免超大文本卡住界面。</summary>
    private const int MaxJsonLength = 2_000_000;

    /// <summary>单击与双击的区分窗口（双击时取消单击的复制动作）。</summary>
    private static readonly TimeSpan ClickDelay = TimeSpan.FromMilliseconds(260);

    /// <summary>在同一位置连续 Ctrl+双击视为“继续扩大”的时间窗。</summary>
    private static readonly TimeSpan ExpandWindow = TimeSpan.FromSeconds(2);

    private readonly ClipItem _item;
    private readonly Action _markSelfWrite;
    private bool _pinned;
    private DispatcherTimer? _resetTimer;

    // 智能选中：当前扩张层级 + 上一次双击的位置，用于判断是否“继续扩大”
    private int _expandLevel;
    private int _lastExpandIndex = -1;
    private DateTime _lastExpandAt;

    // JSON 属性面板
    private JsonPropertyInfo? _jsonItem;
    private DispatcherTimer? _jsonClickTimer;

    public ContentViewerWindow(ClipItem item, Action markSelfWrite, bool autoPin = false)
    {
        InitializeComponent();
        _item = item;
        _markSelfWrite = markSelfWrite;
        ApplyPinState(autoPin);
        ApplyContent();
        BuildJsonPanel();
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

    // ---- JSON 属性面板 ----

    /// <summary>内容是 JSON 对象/数组时，展开出「路径 = 值」列表并显示面板。</summary>
    private void BuildJsonPanel()
    {
        if (_item.Kind != ClipKind.Text) return;
        var text = ContentBox.Text;
        if (string.IsNullOrEmpty(text) || text.Length > MaxJsonLength) return;

        var set = JsonPropertyList.TryBuild(text);
        if (set is null || set.Items.Count == 0) return;

        JsonList.ItemsSource = set.Items;
        JsonPanelButton.Content = $"属性 {set.Items.Count}";
        JsonPanelButton.Visibility = Visibility.Visible;
        JsonPanel.Visibility = Visibility.Visible;
        JsonPanelButton.Opacity = 1.0;
        if (set.Truncated) JsonPanelButton.ToolTip = $"属性较多，仅列出前 {set.Items.Count} 条";

        // JSON 通常较长：默认窗口放大一些，避免面板挤压文本区
        if (Width <= 320 && Height <= 320)
        {
            Width = 640;
            Height = 560;
        }
    }

    private void OnToggleJsonPanel(object sender, RoutedEventArgs e)
    {
        var show = JsonPanel.Visibility != Visibility.Visible;
        JsonPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        JsonPanelButton.Opacity = show ? 1.0 : 0.5;
    }

    /// <summary>
    /// 属性行点击：单击复制值（延迟一点，避免双击时先复制值再复制路径），双击复制路径。
    /// 用 Preview 事件：ListBoxItem 会吃掉 MouseDoubleClick，鼠标双击事件到不了 ListBox。
    /// </summary>
    private void OnJsonListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var info = FindJsonItem(e.OriginalSource as DependencyObject);
        if (info is null) return; // 点在空白处
        _jsonItem = info;
        _jsonClickTimer?.Stop();

        if (e.ClickCount >= 2)
        {
            CopyToClipboard(info.Path, $"路径 {info.Path}");
            e.Handled = true;
            return;
        }

        _jsonClickTimer = new DispatcherTimer { Interval = ClickDelay };
        _jsonClickTimer.Tick += (_, _) =>
        {
            _jsonClickTimer?.Stop();
            CopyToClipboard(info.DisplayValue, $"值 {info.Path}");
        };
        _jsonClickTimer.Start();
    }

    private void OnJsonListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var info = FindJsonItem(e.OriginalSource as DependencyObject) ?? _jsonItem;
        if (info is null)
        {
            e.Handled = true;
            return;
        }
        _jsonItem = info;

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem($"复制值：{Preview(info.DisplayValue)}",
            () => CopyToClipboard(info.DisplayValue, $"值 {info.Path}")));
        menu.Items.Add(MenuItem($"复制路径：{info.Path}",
            () => CopyToClipboard(info.Path, "路径")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem($"复制片段：{Preview(info.PairText)}",
            () => CopyToClipboard(info.PairText, $"片段 {info.Path}")));
        JsonList.ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static JsonPropertyInfo? FindJsonItem(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as JsonPropertyInfo;
    }

    // ---- 智能选中：双击选中同段内容，Ctrl+双击逐层扩大 ----

    private void OnContentPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return; // 单击/拖选保持系统默认行为
        var text = ContentBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        var index = ContentBox.GetCharacterIndexFromPoint(e.GetPosition(ContentBox), snapToText: true);
        if (index < 0) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var now = DateTime.UtcNow;
        var sameSpot = _lastExpandIndex >= 0
                       && Math.Abs(index - _lastExpandIndex) <= 2
                       && now - _lastExpandAt < ExpandWindow;

        _expandLevel = ctrl ? (sameSpot ? _expandLevel + 1 : 1) : 0;
        _lastExpandIndex = index;
        _lastExpandAt = now;

        var selection = SmartTextSelector.Select(text, index, _expandLevel);
        if (selection is null) return;

        ContentBox.SelectionStart = selection.Range.Start;
        ContentBox.SelectionLength = selection.Range.Length;
        Keyboard.Focus(ContentBox);
        SetStatus($"已选中 {selection.Label} · {selection.Range.Length} 字 · Ctrl+双击继续扩大 · Ctrl+C 复制");
        e.Handled = true;
    }

    // ---- 复制与状态 ----

    private void CopyToClipboard(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        _markSelfWrite(); // 抑制自身写入回灌成新历史条目
        ClipboardWriter.SetClipboardTextAsync(text);
        SetStatus($"已复制{what}：{Preview(text)}");
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
    }

    // ---- 复制全部 / 置顶 ----

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        _markSelfWrite();
        ClipboardWriter.SetClipboardAsync(_item);
        SetStatus("已复制全部内容");
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
