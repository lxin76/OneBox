namespace OneBox.Plugins.Screenshot.Core;

/// <summary>截图保存格式。</summary>
public enum ShotImageFormat
{
    Png,
    Jpeg,
}

/// <summary>
/// 截图插件设置（JSON 持久化到插件数据目录，由宿主分配：
/// %LOCALAPPDATA%\OneBox\plugin-state\screenshot）。
/// </summary>
public sealed class ScreenshotSettings
{
    /// <summary>截图保存目录；为空时使用 <see cref="AppPaths.DefaultSaveDirectory"/>。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>文件名模板，支持 {yyyy}/{MM}/{dd}/{HH}/{mm}/{ss}/{fff} 时间占位符。
    /// 不要写扩展名，扩展名由保存格式决定。</summary>
    public string FileNameTemplate { get; set; } = "Shot_{yyyyMMdd_HHmmss}";

    /// <summary>保存格式。</summary>
    public ShotImageFormat Format { get; set; } = ShotImageFormat.Png;

    /// <summary>JPEG 质量（1-100），仅 <see cref="ShotImageFormat.Jpeg"/> 生效。</summary>
    public int JpegQuality { get; set; } = 92;

    /// <summary>截图完成后是否自动写入剪贴板。</summary>
    public bool CopyToClipboard { get; set; } = true;

    /// <summary>贴图窗口是否默认置顶。</summary>
    public bool PinTopmost { get; set; } = true;

    /// <summary>区域截图后是否弹出工具条（关闭则截完直接入库）。</summary>
    public bool ShowToolbarAfterRegion { get; set; } = true;

    /// <summary>滚动截长图时两帧之间至少要有多少行内容一致才认为匹配成功。
    /// 太小会把动态内容误判为滚动，太大会导致长页面里匹配失败。</summary>
    public int ScrollMinMatchRows { get; set; } = 24;

    /// <summary>滚动截长图的像素高度上限，达到后自动停止，避免内存失控。</summary>
    public int ScrollMaxHeight { get; set; } = 20000;

    /// <summary>滚动截长图每次滚动的鼠标滚轮格数。</summary>
    public int ScrollWheelNotches { get; set; } = 3;

    /// <summary>文本识别语言（BCP-47，如 "zh-Hans-CN"）；为空时用系统首选语言。</summary>
    public string OcrLanguage { get; set; } = "";

    /// <summary>识别结果里是否去掉中文之间的空格。</summary>
    public bool OcrStripCjkSpaces { get; set; } = true;

    /// <summary>文字偏小时是否放大后重识别，用来提高小字识别率。</summary>
    public bool OcrEnhanceImage { get; set; } = true;

    /// <summary>区域截图热键（"Ctrl+Alt+A" 形式）。</summary>
    public string RegionHotkey { get; set; } = "Ctrl+Alt+A";

    /// <summary>全屏截图热键。</summary>
    public string FullScreenHotkey { get; set; } = "Ctrl+Alt+F";

    /// <summary>滚动截长图热键。</summary>
    public string ScrollHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>识别剪贴板图片里的文字的热键。</summary>
    public string OcrClipboardHotkey { get; set; } = "Ctrl+Alt+T";

    /// <summary>取实际生效的保存目录。</summary>
    public string ResolveSaveDirectory()
        => string.IsNullOrWhiteSpace(SaveDirectory) ? AppPaths.DefaultSaveDirectory : SaveDirectory;
}