using System.IO;
using System.Windows;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;
using OneBox.Plugin.Sdk.Wpf;

namespace OneBox.Plugins.QuickNote;

/// <summary>
/// 示例小工具：一个置顶便签窗。
/// 演示插件平台的全部核心能力：声明全局热键、注册托盘菜单、读写私有设置、上报日志与状态。
/// </summary>
public sealed class QuickNotePlugin : IAsyncDisposable
{
    private const string HotkeyToggle = "toggle";
    private const string MenuShow = "show";
    private const string MenuClear = "clear";
    private const string SettingKey = "note";

    /// <summary>便签窗呼出热键的默认值。用户可在工具箱里改键，改后以宿主回传的为准。</summary>
    private const string DefaultToggleHotkey = "Ctrl+Alt+N";

    private readonly PluginContext _context;
    private readonly Dispatcher _dispatcher;
    private NoteWindow? _window;
    private string _note = "";
    private bool _disposed;

    /// <summary>
    /// 当前实际生效的呼出热键。由宿主回传的注册结果驱动，
    /// 界面提示一律用它，避免出现"改了键、提示还写着旧键"。
    /// </summary>
    private string _toggleHotkey = DefaultToggleHotkey;

    public QuickNotePlugin(PluginContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    public async Task StartAsync()
    {
        // 跟随宿主主题：便签窗里的颜色全部引用主题键，这里把调色板刷进应用资源
        PluginTheme.Attach(_context);

        // 插件私有设置由宿主保存到 %LOCALAPPDATA%\OneBox\plugin-state\quicknote\
        _note = await _context.GetSettingAsync(SettingKey, "") ?? "";

        _context.HotkeyPressed += OnHotkeyPressed;
        _context.MenuInvoked += OnMenuInvoked;
        // 用户在工具箱改键后，宿主会推一份最新注册结果过来
        _context.HotkeysChanged += OnHotkeysChanged;

        var results = await _context.DeclareHotkeysAsync(
        [
            new HotkeyDeclaration
            {
                ActionId = HotkeyToggle,
                Combination = DefaultToggleHotkey,
                Description = "快速便签：显示/隐藏便签窗",
            },
        ]);

        await _context.DeclareTrayMenuAsync(
        [
            new TrayMenuItem { Id = MenuShow, Text = "显示便签" },
            new TrayMenuItem { Id = MenuClear, Text = "清空便签" },
        ]);

        ApplyHotkeyResults(results);
        await _context.ReadyAsync("快速便签", BuildStatusText());
        await _context.LogInfoAsync($"快速便签已启动（数据目录 {_context.DataDirectory}）");
    }

    /// <summary>宿主推送了最新的热键注册结果（用户改键，或注册状态变化）。</summary>
    private void OnHotkeysChanged(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        ApplyHotkeyResults(results);
        UpdateHintText();

        // 状态栏也一并刷新，否则工具箱列表里还会显示旧键位
        _ = _context.SetStatusAsync(BuildStatusText());
    }

    /// <summary>把注册结果落到 <see cref="_toggleHotkey"/>。</summary>
    private void ApplyHotkeyResults(IReadOnlyList<HotkeyRegistrationResult> results)
    {
        var toggle = results.FirstOrDefault(r =>
            string.Equals(r.ActionId, HotkeyToggle, StringComparison.OrdinalIgnoreCase));

        // 拿不到结果时保留上一次的键位，不要退回写死的默认值——
        // 那样反而会把用户改过的键覆盖成错误的提示
        if (toggle is { Combination.Length: > 0 })
        {
            _toggleHotkey = toggle.Combination;
            _toggleHotkeyRegistered = toggle.Registered;
        }
    }

    /// <summary>状态栏文本：已就绪 + 当前键位（或未生效原因）。</summary>
    private string BuildStatusText()
        => _toggleHotkeyRegistered ? $"已就绪 · {_toggleHotkey}" : "热键未生效";

    private bool _toggleHotkeyRegistered = true;

    /// <summary>把当前键位写进便签窗底部提示（窗口已存在才更新）。</summary>
    private void UpdateHintText()
    {
        if (_window is null) return;
        _window.HotkeyHint = $"内容自动保存。按 {_toggleHotkey} 或托盘菜单可隐藏本窗口。";
    }

    private void OnHotkeyPressed(string actionId)
    {
        if (actionId == HotkeyToggle) _dispatcher.BeginInvoke(new Action(ToggleWindow));
    }

    private void OnMenuInvoked(MenuInvokedPayload invoked)
    {
        switch (invoked.ItemId)
        {
            case MenuShow:
                _dispatcher.BeginInvoke(new Action(ToggleWindow));
                break;
            case MenuClear:
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    _note = "";
                    if (_window is not null) _window.NoteText = "";
                    SaveNote();
                }));
                break;
        }
    }

    private void ToggleWindow()
    {
        _window ??= CreateWindow();
        if (_window.IsVisible)
        {
            SaveNote();
            _window.Hide();
        }
        else
        {
            _window.NoteText = _note;
            _window.Show();
            _window.Activate();
        }
    }

    private NoteWindow CreateWindow()
    {
        var window = new NoteWindow();
        PluginTheme.ApplyToWindow(window);
        // 窗口可能在改键之后才第一次打开，这里按当前生效键位填一次；
        // 之后再改键会走 HotkeysChanged → UpdateHintText 更新
        window.HotkeyHint = $"内容自动保存。按 {_toggleHotkey} 或托盘菜单可隐藏本窗口。";
        window.NoteChanged += text =>
        {
            _note = text;
            SaveNote();
        };
        window.Closed += (_, _) => SaveNote();
        return window;
    }

    private void SaveNote()
        => _ = _context.SetSettingAsync(SettingKey, _note);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        SaveNote();

        try
        {
            _window?.Close();
        }
        catch
        {
            // 关闭过程中的异常忽略
        }
        await _context.DisposeAsync();
    }
}