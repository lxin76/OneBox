using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OneBox.Abstractions;
using OneBox.Abstractions.Theming;
using OneBox.Host.Services;

namespace OneBox.Host.Windows;

/// <summary>
/// 设置中心：左栏按插件分层列出，右栏只显示选中插件的设置。
///
/// 与旧设置窗的区别：旧窗把所有选项混在一起。小工具一多，用户很难分辨
/// 某个选项属于哪个插件。这里以插件为第一层分组，宿主自身的选项也作为一项。
///
/// 设置值的来源：插件通过 declareSettings 声明，宿主只负责渲染与收集改动，
/// 改动回传给插件，由插件自己落地。宿主不保存插件设置，避免两份真相。
/// </summary>
public partial class SettingsCenterWindow : Window
{
    private readonly PluginManager _manager;
    private readonly PluginRegistry _registry;

    /// <summary>
    /// 窗口句柄就绪后把系统标题栏切成深色，与工具箱窗口保持一致。
    /// 不接管窗口框架，拖动/最大化/贴边分屏仍由系统负责。
    /// </summary>
    private void OnSourceInitialized(object sender, EventArgs e)
        => DarkTitleBar.Apply(this, ThemeManager.Current.IsDark);

    /// <summary>左栏每一项：要么是宿主，要么是某个插件。</summary>
    private sealed class Entry
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required string Subtitle { get; init; }
        public required ImageSource Icon { get; init; }
        public PluginRuntime? Runtime { get; init; }
        public bool IsHost => Runtime is null;
    }

    public SettingsCenterWindow(PluginManager manager, PluginRegistry registry)
    {
        InitializeComponent();
        _manager = manager;
        _registry = registry;

        Loaded += (_, _) =>
        {
            _manager.SettingsChanged += OnSettingsChanged;
            _manager.Changed += OnSettingsChanged;
            Rebuild(keepSelection: false);
            Activate();
        };
        Closed += (_, _) =>
        {
            _manager.SettingsChanged -= OnSettingsChanged;
            _manager.Changed -= OnSettingsChanged;
        };
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(() => Rebuild(keepSelection: true));

    /// <summary>
    /// 重建左栏列表。插件的设置项是插件启动后才声明的，
    /// 因此这里只列出「有设置可编辑」的插件，避免出现点进去空白的项。
    /// </summary>
    private void Rebuild(bool keepSelection)
    {
        var selectedKey = keepSelection ? (PluginList.SelectedItem as Entry)?.Key : null;

        var entries = new List<Entry>
        {
            new()
            {
                Key = "__host__", Name = "宿主", Subtitle = "OneBox 平台自身的行为",
                Icon = PluginIcons.Host,
            },
        };

        foreach (var plugin in _manager.Plugins)
        {
            var runtime = plugin.Runtime;
            if (runtime is null || plugin.Manifest is null) continue;

            var count = runtime.Settings.Count;
            var subtitle = runtime.State switch
            {
                PluginState.Running => count > 0 ? $"{count} 项设置" : "运行中",
                PluginState.Starting => "启动中…",
                PluginState.Disabled => "已禁用",
                PluginState.Crashed => "已崩溃",
                PluginState.Faulted => "启动失败",
                _ => "已停止",
            };

            entries.Add(new Entry
            {
                Key = runtime.Manifest.Id,
                Name = runtime.Manifest.Name,
                Subtitle = subtitle,
                Icon = PluginIcons.ForManifest(runtime.Manifest),
                Runtime = runtime,
            });
        }

        PluginList.ItemsSource = entries;

        var restored = selectedKey is not null
            ? entries.FirstOrDefault(e => e.Key == selectedKey)
            : null;
        PluginList.SelectedItem = restored ?? entries[0];
    }

    private void OnPluginSelectionChanged(object sender, SelectionChangedEventArgs e)
        => RenderSelected();

    private void RenderSelected()
    {
        FormHost.Children.Clear();

        if (PluginList.SelectedItem is not Entry entry) return;

        if (entry.IsHost)
        {
            HeaderText.Text = "宿主";
            HeaderHint.Text = "OneBox 平台自身的行为。插件的设置请在上方选择对应的小工具。";
            BuildHostForm();
            return;
        }

        var runtime = entry.Runtime!;
        HeaderText.Text = runtime.Manifest.Name;
        HeaderHint.Text = $"来自小工具 {runtime.Manifest.Id}。修改后立即生效。";

        if (runtime.Settings.Count == 0)
        {
            FormHost.Children.Add(new TextBlock
            {
                Text = "这个小工具没有提供可在此调整的设置。",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        BuildPluginForm(runtime);
    }

    // ---- 插件设置表单 ----

    private void BuildPluginForm(PluginRuntime runtime)
    {
        foreach (var section in SettingsFormBuilder.Group(runtime.Settings))
        {
            FormHost.Children.Add(new TextBlock
            {
                Text = section.Title,
                Style = (Style)FindResource("SectionTitle"),
                Margin = new Thickness(0, FormHost.Children.Count == 0 ? 0 : 14, 0, 8),
            });

            foreach (var descriptor in section.Items)
                FormHost.Children.Add(BuildRow(runtime, descriptor));
        }
    }

    /// <summary>为一个设置项生成「标题 + 说明 + 编辑控件」的一行。</summary>
    private FrameworkElement BuildRow(PluginRuntime runtime, SettingDescriptor descriptor)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(descriptor.Label) ? descriptor.Key : descriptor.Label,
        };
        // 代码构建的控件也要跟着主题走：SetResourceReference 就是代码版的 DynamicResource
        label.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.Text));
        panel.Children.Add(label);

        var editor = BuildEditor(runtime, descriptor);
        editor.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(editor);

        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return panel;
    }

    private FrameworkElement BuildEditor(PluginRuntime runtime, SettingDescriptor descriptor)
    {
        if (descriptor.ReadOnly)
        {
            return new TextBlock
            {
                Text = SettingValueCodec.GetString(SettingValueCodec.EffectiveValue(descriptor)),
                Style = (Style)FindResource("Hint"),
            };
        }

        return descriptor.Kind switch
        {
            SettingKind.Bool => BuildBool(runtime, descriptor),
            SettingKind.Number => BuildNumber(runtime, descriptor),
            SettingKind.Choice => BuildChoice(runtime, descriptor),
            SettingKind.Multiline => BuildText(runtime, descriptor, multiline: true),
            SettingKind.DirectoryPath => BuildPath(runtime, descriptor, pickDirectory: true),
            SettingKind.FilePath => BuildPath(runtime, descriptor, pickDirectory: false),
            _ => BuildText(runtime, descriptor, multiline: false),
        };
    }

    private FrameworkElement BuildBool(PluginRuntime runtime, SettingDescriptor descriptor)
    {
        var box = new CheckBox
        {
            Content = "启用",
            IsChecked = SettingValueCodec.GetBool(SettingValueCodec.EffectiveValue(descriptor)),
        };
        box.Checked += (_, _) => Commit(runtime, descriptor, SettingValueCodec.FromBool(true));
        box.Unchecked += (_, _) => Commit(runtime, descriptor, SettingValueCodec.FromBool(false));
        return box;
    }

    private FrameworkElement BuildNumber(PluginRuntime runtime, SettingDescriptor descriptor)
    {
        var box = new TextBox
        {
            Text = FormatNumber(SettingValueCodec.GetNumber(SettingValueCodec.EffectiveValue(descriptor))),
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // 失焦或回车才提交：逐字提交会让插件收到一堆中间态（比如输入 "10" 时的 "1"）
        box.LostFocus += (_, _) => CommitNumber(runtime, descriptor, box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitNumber(runtime, descriptor, box);
            e.Handled = true;
        };

        if (descriptor.Min is not null || descriptor.Max is not null)
        {
            var hint = descriptor.Min is not null && descriptor.Max is not null
                ? $"范围 {FormatNumber(descriptor.Min.Value)} ~ {FormatNumber(descriptor.Max.Value)}"
                : descriptor.Min is not null
                    ? $"不小于 {FormatNumber(descriptor.Min.Value)}"
                    : $"不大于 {FormatNumber(descriptor.Max!.Value)}";

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(box);
            var hintBlock = new TextBlock
            {
                Text = hint,
                Style = (Style)FindResource("Hint"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(hintBlock, 1);
            grid.Children.Add(hintBlock);
            return grid;
        }

        return box;
    }

    private void CommitNumber(PluginRuntime runtime, SettingDescriptor descriptor, TextBox box)
    {
        if (!double.TryParse(box.Text.Trim(), out var parsed))
        {
            // 输入非法时还原成当前值，不把坏值发给插件
            box.Text = FormatNumber(SettingValueCodec.GetNumber(SettingValueCodec.EffectiveValue(descriptor)));
            return;
        }

        if (descriptor.Min is { } min && parsed < min) parsed = min;
        if (descriptor.Max is { } max && parsed > max) parsed = max;

        box.Text = FormatNumber(parsed);
        Commit(runtime, descriptor, SettingValueCodec.FromNumber(parsed));
    }

    private FrameworkElement BuildChoice(PluginRuntime runtime, SettingDescriptor descriptor)
    {
        var current = SettingValueCodec.GetString(SettingValueCodec.EffectiveValue(descriptor));
        var box = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };

        var selectedIndex = 0;
        for (int i = 0; i < descriptor.Choices.Count; i++)
        {
            var choice = descriptor.Choices[i];
            box.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(choice.Label) ? choice.Value : choice.Label,
                Tag = choice.Value,
            });
            if (string.Equals(choice.Value, current, StringComparison.Ordinal)) selectedIndex = i;
        }

        box.SelectedIndex = descriptor.Choices.Count > 0 ? selectedIndex : -1;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string value })
                Commit(runtime, descriptor, SettingValueCodec.FromString(value));
        };

        return box;
    }

    private FrameworkElement BuildText(PluginRuntime runtime, SettingDescriptor descriptor, bool multiline)
    {
        var current = SettingValueCodec.GetString(SettingValueCodec.EffectiveValue(descriptor));
        var box = new TextBox
        {
            Text = current,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinLines = multiline ? 3 : 1,
            MaxLines = multiline ? 6 : 1,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };

        if (!string.IsNullOrWhiteSpace(descriptor.Placeholder))
            box.ToolTip = descriptor.Placeholder;

        box.LostFocus += (_, _) => Commit(runtime, descriptor, SettingValueCodec.FromString(box.Text));
        box.KeyDown += (_, e) =>
        {
            // 多行文本框里回车是换行，只能用失焦提交
            if (multiline || e.Key != Key.Enter) return;
            Commit(runtime, descriptor, SettingValueCodec.FromString(box.Text));
            e.Handled = true;
        };

        if (!multiline) box.Width = 320;
        return box;
    }

    private FrameworkElement BuildPath(PluginRuntime runtime, SettingDescriptor descriptor, bool pickDirectory)
    {
        var current = SettingValueCodec.GetString(SettingValueCodec.EffectiveValue(descriptor));
        var box = new TextBox { Text = current, MinWidth = 300 };

        box.LostFocus += (_, _) => Commit(runtime, descriptor, SettingValueCodec.FromString(box.Text));
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit(runtime, descriptor, SettingValueCodec.FromString(box.Text));
            e.Handled = true;
        };

        var browse = new Button
        {
            Content = "浏览…",
            Margin = new Thickness(8, 0, 0, 0),
        };
        browse.Click += (_, _) =>
        {
            var picked = pickDirectory ? PickDirectory(box.Text) : PickFile(box.Text);
            if (picked is null) return;
            box.Text = picked;
            Commit(runtime, descriptor, SettingValueCodec.FromString(picked));
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    private string? PickDirectory(string current)
    {
        // .NET 8 的 WPF 自带目录选择器（与截图插件用的是同一个）
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择目录" };
        if (System.IO.Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickFile(string current)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择文件", CheckFileExists = true };
        if (System.IO.File.Exists(current))
        {
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current);
            dialog.FileName = System.IO.Path.GetFileName(current);
        }
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>把一项改动回传给插件，并就地更新本地副本的当前值。</summary>
    private void Commit(PluginRuntime runtime, SettingDescriptor descriptor, JsonElement value)
    {
        descriptor.Value = value;

        var change = new SettingChange { Key = descriptor.Key, Value = value };
        _ = SendChangesAsync(runtime, [change]);
    }

    private async Task SendChangesAsync(PluginRuntime runtime, IReadOnlyList<SettingChange> changes)
    {
        try
        {
            await runtime.SendSettingsChangedAsync(changes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HostLog.Write($"下发设置失败（{runtime.Manifest.Id}）: {ex.Message}");
        }
    }

    private static string FormatNumber(double value)
        => Math.Abs(value - Math.Round(value)) < 0.0001
            ? ((long)Math.Round(value)).ToString()
            : value.ToString("0.###");

    // ---- 宿主自身设置 ----

    private void BuildHostForm()
    {
        var settings = _registry.Settings;

        AddSection("外观");
        FormHost.Children.Add(BuildThemeChooser());
        FormHost.Children.Add(Hint("主题同时作用于宿主窗口与各小工具的窗口，点一下立即生效并保存。"));

        AddSection("插件运行", topMargin: 16);

        var autoRestart = new CheckBox
        {
            Content = "插件异常退出后自动重启",
            IsChecked = settings.AutoRestartPlugins,
            Margin = new Thickness(0, 0, 0, 2),
        };
        FormHost.Children.Add(autoRestart);
        FormHost.Children.Add(Hint("插件进程与宿主隔离：崩溃只影响该插件本身，宿主会按下面的次数重试。"));

        var maxBox = new TextBox
        {
            Text = settings.MaxRestartAttempts.ToString(),
            Width = 70,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        FormHost.Children.Add(Labelled("最多连续重启次数", maxBox));

        AddSection("宿主行为", topMargin: 16);

        FormHost.Children.Add(BuildCloseBehaviorChooser());
        FormHost.Children.Add(Hint("两个窗口都开着时，关掉其中一个不会退出；全部关掉才按这里的选择执行。"));

        var autoStart = new CheckBox
        {
            Content = "开机自动启动 OneBox",
            IsChecked = AutoStartService.IsEnabled(),
            Margin = new Thickness(0, 10, 0, 0),
        };
        FormHost.Children.Add(autoStart);

        var autoStartTip = Hint(AutoStartService.IsEnabled()
            ? $"已启用：开机自动启动 {Environment.ProcessPath}"
            : "已关闭开机自动启动");
        FormHost.Children.Add(autoStartTip);

        var showToolbox = new CheckBox
        {
            Content = "启动时打开工具箱窗口",
            IsChecked = settings.ShowToolboxOnStartup,
            Margin = new Thickness(0, 8, 0, 0),
        };
        FormHost.Children.Add(showToolbox);
        FormHost.Children.Add(Hint(
            "单击（或双击）托盘图标也能打开工具箱。"
            + "若任务栏里看不到 OneBox 图标，请在「任务栏设置 → 系统托盘」中把它固定到托盘区；"
            + "也可以给 OneBox.exe --toolbox 建一个桌面快捷方式，并在快捷方式属性里设置快捷键。"));

        var contextMenu = new CheckBox
        {
            Content = "在桌面与文件夹的右键菜单里加入「OneBox 工具箱」",
            IsChecked = settings.AddToContextMenu,
            Margin = new Thickness(0, 8, 0, 0),
        };
        FormHost.Children.Add(contextMenu);
        FormHost.Children.Add(Hint("勾选后在桌面或文件夹空白处右键就能打开工具箱，不必先去找托盘图标。"));

        AddSection("目录", topMargin: 16);
        FormHost.Children.Add(Hint($"插件安装位置：{HostPaths.PluginsDirectory}"));
        FormHost.Children.Add(Hint($"数据位置：{HostPaths.DataDirectory}"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ActionButton("打开插件目录", () => OpenPath(HostPaths.PluginsDirectory)));
        buttons.Children.Add(ActionButton("打开数据目录", () => OpenPath(HostPaths.DataDirectory)));
        buttons.Children.Add(ActionButton("打开日志目录", () => OpenPath(HostPaths.LogDirectory)));
        FormHost.Children.Add(buttons);

        // 绑定放在初始化之后：避免初始赋值触发保存
        autoRestart.Checked += OnHostSettingChanged;
        autoRestart.Unchecked += OnHostSettingChanged;
        showToolbox.Checked += OnHostSettingChanged;
        showToolbox.Unchecked += OnHostSettingChanged;
        autoStart.Checked += (_, _) => ApplyAutoStart(true, autoStartTip);
        autoStart.Unchecked += (_, _) => ApplyAutoStart(false, autoStartTip);
        contextMenu.Checked += (_, _) => ApplyContextMenu(true);
        contextMenu.Unchecked += (_, _) => ApplyContextMenu(false);

        maxBox.LostFocus += (_, _) => ApplyMaxRestart(maxBox);
        maxBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyMaxRestart(maxBox);
            e.Handled = true;
        };

        void OnHostSettingChanged(object sender, RoutedEventArgs e)
        {
            _registry.Settings.AutoRestartPlugins = autoRestart.IsChecked == true;
            _registry.Settings.ShowToolboxOnStartup = showToolbox.IsChecked == true;
            _registry.Save();
        }
    }

    /// <summary>
    /// 主题选择：每个主题一张卡片（带配色预览），整卡可点。
    /// 切换立即写设置、刷宿主资源，并经 App 广播给所有插件进程。
    /// </summary>
    private FrameworkElement BuildThemeChooser()
    {
        // 用实际生效的主题而不是设置里的原始值：设置里若存着已删掉的 id，
        // ThemeManager 会回退到默认主题，卡片也应跟着选中默认那一张
        var currentId = ThemeManager.Current.Id;
        var panel = new StackPanel();

        foreach (var theme in ThemeCatalog.All)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("ChoiceCard"),
                GroupName = "OneBoxTheme",
                Margin = new Thickness(0, 0, 0, 6),
                Content = BuildThemeCard(theme),
                // 先赋值再挂事件：初始化时的选中不该被当成一次用户改动
                IsChecked = string.Equals(theme.Id, currentId, StringComparison.OrdinalIgnoreCase),
            };
            radio.Checked += (_, _) => ApplyTheme(theme);
            panel.Children.Add(radio);
        }

        return panel;
    }

    /// <summary>主题卡片的正面：配色预览条 + 主题名 + 一句话说明。</summary>
    private static FrameworkElement BuildThemeCard(ThemePalette theme)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var swatches = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        foreach (var key in new[] { ThemeKeys.Window, ThemeKeys.Surface, ThemeKeys.Accent })
        {
            swatches.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 3, 0),
                Background = ThemeManager.BrushFor(theme, key),
                BorderBrush = ThemeManager.BrushFor(theme, ThemeKeys.Border),
                BorderThickness = new Thickness(1),
            });
        }
        grid.Children.Add(swatches);

        var text = BuildOptionCard(theme.DisplayName, theme.Description);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return grid;
    }

    /// <summary>卡片的正面：标题 + 一句话说明。主题卡片与关闭行为卡片共用。</summary>
    private static FrameworkElement BuildOptionCard(string title, string description)
    {
        var text = new StackPanel();
        var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.TextStrong));
        text.Children.Add(name);

        var detail = new TextBlock
        {
            Text = description,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.TextMuted));
        text.Children.Add(detail);

        return text;
    }

    /// <summary>换主题：落地到宿主设置并立刻应用（App 会顺带刷新标题栏、广播给插件）。</summary>
    private void ApplyTheme(ThemePalette theme)
    {
        _registry.Settings.Theme = theme.Id;
        _registry.Save();
        ThemeManager.Apply(theme);
    }

    /// <summary>
    /// 关闭窗口时的行为：两个选项各一张卡片，点一下立即保存、下次关窗口生效。
    /// 刚装好时这里是空的（还没问过），第一次关窗口会被问一次，选择写进同一处设置。
    /// </summary>
    private FrameworkElement BuildCloseBehaviorChooser()
    {
        var current = CloseWindowActions.Normalize(_registry.Settings.CloseWindowAction);
        var host = new StackPanel();

        var caption = new TextBlock { Text = "关闭窗口时：", Margin = new Thickness(0, 0, 0, 6) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.Text));
        host.Children.Add(caption);

        (string Value, string Title, string Description)[] options =
        [
            (CloseWindowActions.CloseWindowOnly, "只关闭窗口", "OneBox 继续留在托盘，随时可以从托盘图标再打开。"),
            (CloseWindowActions.ExitApp, "关闭的同时退出 OneBox", "窗口关掉就直接结束进程，不用再去托盘右键退出。"),
        ];

        foreach (var (value, title, description) in options)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("ChoiceCard"),
                GroupName = "OneBoxCloseBehavior",
                Margin = new Thickness(0, 0, 0, 6),
                Content = BuildOptionCard(title, description),
                // 先赋值再挂事件：初始化时的选中不该被当成一次用户改动
                IsChecked = string.Equals(value, current, StringComparison.Ordinal),
            };
            radio.Checked += (_, _) => ApplyCloseBehavior(value);
            host.Children.Add(radio);
        }

        return host;
    }

    /// <summary>记下关闭窗口时的行为，下次关窗口按它执行。</summary>
    private void ApplyCloseBehavior(string value)
    {
        _registry.Settings.CloseWindowAction = value;
        _registry.Save();
    }

    /// <summary>勾选/取消右键菜单：写设置并立即同步注册表。</summary>
    private void ApplyContextMenu(bool enabled)
    {
        _registry.Settings.AddToContextMenu = enabled;
        _registry.Save();
        ContextMenuRegistrar.Sync(_registry);
    }

    private void ApplyAutoStart(bool enabled, TextBlock tip)
    {
        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath)) AutoStartService.Enable(exePath);
        }
        else
        {
            AutoStartService.Disable();
        }

        _registry.Settings.AutoStart = AutoStartService.IsEnabled();
        _registry.Save();
        tip.Text = AutoStartService.IsEnabled()
            ? $"已启用：开机自动启动 {Environment.ProcessPath}"
            : "已关闭开机自动启动";
    }

    private void ApplyMaxRestart(TextBox box)
    {
        if (int.TryParse(box.Text.Trim(), out var attempts) && attempts is >= 0 and <= 20)
        {
            _registry.Settings.MaxRestartAttempts = attempts;
            _registry.Save();
        }
        else
        {
            box.Text = _registry.Settings.MaxRestartAttempts.ToString();
        }
    }

    // ---- 小工具 ----

    private void AddSection(string title, double topMargin = 0)
    {
        FormHost.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(0, topMargin, 0, 8),
        });
    }

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 0, 0, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.ResourceKey(ThemeKeys.TextMuted));
        return block;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement editor)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(editor);
        return panel;
    }

    private Button ActionButton(string text, Action onClick)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HostLog.Write($"打开目录失败（{path}）: {ex.Message}");
        }
    }
}