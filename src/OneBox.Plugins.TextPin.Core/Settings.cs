namespace OneBox.Plugins.TextPin.Core;

/// <summary>
/// 置顶文本插件设置（JSON 持久化到插件数据目录，由宿主分配的
/// &lt;宿主安装目录&gt;\data\plugin-state\textpin）。
/// </summary>
public sealed class Settings
{
    /// <summary>窗口是否置顶（始终浮在其他窗口上方）。</summary>
    public bool Pinned { get; set; } = true;

    /// <summary>
    /// 是否"不抢焦点"（悬浮模式）：窗口用 WS_EX_NOACTIVATE 显示，点击它不打断
    /// 当前正在编辑的软件。需要输入时点击文本框会临时允许激活，失焦后自动恢复。
    /// </summary>
    public bool NoActivate { get; set; } = true;

    /// <summary>正文字号。</summary>
    public double FontSize { get; set; } = 13;

    /// <summary>正文是否自动换行（关闭则横向滚动）。</summary>
    public bool WordWrap { get; set; } = true;

    /// <summary>文本长度上限，超出部分直接截断，避免异常大的粘贴内容拖垮界面与磁盘写入。</summary>
    public int MaxTextLength { get; set; } = 200_000;

    /// <summary>显示/隐藏窗口的全局热键（"Ctrl+Alt+V" 形式）。
    /// 注意：默认避开 Ctrl+Shift 组合，因为中文 Windows 上 Ctrl+Shift 会被输入法切换器拦截。</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+V";

    /// <summary>抓取当前选中文字并置顶的全局热键（"Ctrl+Alt+C" 形式）。
    /// 在任意编辑器里选中文字后按下：模拟 Ctrl+C 复制，再把文本读进置顶窗。
    /// 这是本插件唯一注入按键的功能——复制/粘贴本身交给系统与文本框原生行为。</summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+C";
}
