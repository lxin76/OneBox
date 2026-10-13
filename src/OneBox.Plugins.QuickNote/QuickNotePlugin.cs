using System.IO;
using System.Windows;
using System.Windows.Threading;
using OneBox.Abstractions;
using OneBox.Plugin.Sdk;

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

    private readonly PluginContext _context;
    private readonly Dispatcher _dispatcher;
    private NoteWindow? _window;
    private string _note = "";
    private bool _disposed;

    public QuickNotePlugin(PluginContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    public async Task StartAsync()
    {
        // 插件私有设置由宿主保存到 %LOCALAPPDATA%\OneBox\plugin-state\quicknote\
        _note = await _context.GetSettingAsync(SettingKey, "") ?? "";

        _context.HotkeyPressed += OnHotkeyPressed;
        _context.MenuInvoked += OnMenuInvoked;

        var results = await _context.DeclareHotkeysAsync(
        [
            new HotkeyDeclaration
            {
                ActionId = HotkeyToggle,
                Combination = "Ctrl+Alt+N",
                Description = "快速便签：显示/隐藏便签窗",
            },
        ]);

        await _context.DeclareTrayMenuAsync(
        [
            new TrayMenuItem { Id = MenuShow, Text = "显示便签" },
            new TrayMenuItem { Id = MenuClear, Text = "清空便签" },
        ]);

        var hotkeyText = results.FirstOrDefault() is { Registered: true } ? "Ctrl+Alt+N" : "热键未生效";
        await _context.ReadyAsync("快速便签", $"已就绪 · {hotkeyText}");
        await _context.LogInfoAsync($"快速便签已启动（数据目录 {_context.DataDirectory}）");
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
