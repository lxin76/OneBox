namespace OneBox.Plugins.Clipboard.Core;

/// <summary>插件设置（JSON 持久化到插件数据目录，由宿主分配的 %LOCALAPPDATA%\OneBox\plugin-state\clipboard）。</summary>
public sealed class Settings
{
    /// <summary>点击历史条目后是否自动粘贴到前台窗口。</summary>
    public bool AutoPaste { get; set; } = true;

    /// <summary>悬浮窗启动时是否置顶。</summary>
    public bool Pinned { get; set; } = true;

    /// <summary>是否监听剪贴板。默认关闭：不注册剪贴板监听器，也不记录任何新的复制内容，
    /// 需要时用悬浮条上的“监听”开关（或托盘菜单/设置页）手动开启。</summary>
    public bool MonitoringEnabled { get; set; }

    /// <summary>历史条数上限（超出裁剪最旧）。</summary>
    public int MaxHistory { get; set; } = 500;

    /// <summary>呼出/收起历史面板的全局热键（"Ctrl+Alt+V" 形式）。
    /// 注意：默认避开 Ctrl+Shift 组合，因为中文 Windows 上 Ctrl+Shift 会被输入法切换器拦截。</summary>
    public string PanelHotkey { get; set; } = "Ctrl+Alt+V";

    /// <summary>粘贴最新剪贴板内容的全局热键（"Ctrl+Alt+P" 形式）。</summary>
    public string PasteHotkey { get; set; } = "Ctrl+Alt+P";

    /// <summary>复制选中内容并打开置顶查看窗口的全局热键（"Ctrl+Alt+C" 形式）。
    /// 在任意编辑器选中文字后按下，自动复制并打开完整查看窗口。</summary>
    public string ViewHotkey { get; set; } = "Ctrl+Alt+C";
}
