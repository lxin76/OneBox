namespace OneBox.Abstractions.Theming;

/// <summary>
/// 主题调色板的颜色键。
///
/// 宿主与插件用同一组键名把颜色注入 WPF 资源字典（XAML 侧写作
/// <c>{DynamicResource Theme.Card}</c>），因此键名是对外契约的一部分：
/// 新增键只能追加，已有键不能改名，否则旧插件里的资源引用会静默失效。
///
/// 键按用途分组，UI 只引用语义名，具体色值由各主题给出——这样换主题时
/// 所有窗口（宿主与各插件）会一起变色，不必逐个改色值。
/// </summary>
public static class ThemeKeys
{
    /// <summary>WPF 资源键的统一前缀（完整键形如 <c>Theme.Card</c>）。</summary>
    public const string ResourcePrefix = "Theme.";

    // ---- 容器底色 ----
    /// <summary>窗口底色。</summary>
    public const string Window = "Window";
    /// <summary>面板/分组容器底色（比窗口略亮或略暗）。</summary>
    public const string Surface = "Surface";
    /// <summary>卡片底色。</summary>
    public const string Card = "Card";
    /// <summary>卡片悬停底色。</summary>
    public const string CardHover = "CardHover";
    /// <summary>列表项悬停底色。</summary>
    public const string RowHover = "RowHover";
    /// <summary>半透明浮窗底色（置顶文本/贴图这类悬浮卡片）。</summary>
    public const string Floating = "Floating";
    /// <summary>半透明浮窗的描边。</summary>
    public const string FloatingBorder = "FloatingBorder";
    /// <summary>文本编辑区底色（在卡片上再压一层淡淡的反差）。</summary>
    public const string EditorBg = "EditorBg";
    /// <summary>半透明浮窗上控件的悬停叠加色（深色主题下是提亮，浅色主题下是压暗）。</summary>
    public const string OverlayHover = "OverlayHover";
    /// <summary>半透明浮窗上控件的按下叠加色。</summary>
    public const string OverlayPressed = "OverlayPressed";

    // ---- 线条与边框 ----
    /// <summary>主分隔线。</summary>
    public const string Line = "Line";
    /// <summary>次级分隔线。</summary>
    public const string LineSoft = "LineSoft";
    /// <summary>控件边框。</summary>
    public const string Border = "Border";

    // ---- 强调色 ----
    /// <summary>强调色（选中、链接、主要标识）。</summary>
    public const string Accent = "Accent";
    /// <summary>强调色的淡底色（选中项背景）。</summary>
    public const string AccentSoft = "AccentSoft";
    /// <summary>强调色淡底上的悬停底色。</summary>
    public const string AccentHover = "AccentHover";
    /// <summary>强调色淡底上的文字。</summary>
    public const string AccentText = "AccentText";
    /// <summary>强调色实底上的文字。</summary>
    public const string TextOnAccent = "TextOnAccent";

    // ---- 页签 ----
    /// <summary>未选中页签的文字。</summary>
    public const string TabInactive = "TabInactive";
    /// <summary>页签悬停底色。</summary>
    public const string TabHover = "TabHover";
    /// <summary>页签悬停时的文字。</summary>
    public const string TabHoverText = "TabHoverText";

    // ---- 文字 ----
    /// <summary>正文。</summary>
    public const string Text = "Text";
    /// <summary>标题与强调正文。</summary>
    public const string TextStrong = "TextStrong";
    /// <summary>输入框正文（要求最高对比度）。</summary>
    public const string TextInput = "TextInput";
    /// <summary>次要文字（说明、副标题）。</summary>
    public const string TextMuted = "TextMuted";
    /// <summary>更淡的文字（占位、水印）。</summary>
    public const string TextFaint = "TextFaint";

    // ---- 控件 ----
    /// <summary>按钮底色。</summary>
    public const string ButtonBg = "ButtonBg";
    /// <summary>按钮悬停底色。</summary>
    public const string ButtonHover = "ButtonHover";
    /// <summary>按钮按下底色。</summary>
    public const string ButtonPressed = "ButtonPressed";
    /// <summary>输入框底色。</summary>
    public const string InputBg = "InputBg";
    /// <summary>徽标底色。</summary>
    public const string BadgeBg = "BadgeBg";

    // ---- 状态色 ----
    /// <summary>正常/已生效。</summary>
    public const string Ok = "Ok";
    /// <summary>警告/未生效。</summary>
    public const string Warn = "Warn";
    /// <summary>错误文字。</summary>
    public const string Error = "Error";
    /// <summary>错误行底色。</summary>
    public const string ErrorWash = "ErrorWash";
    /// <summary>错误边框。</summary>
    public const string ErrorLine = "ErrorLine";

    /// <summary>全部颜色键（供完整性校验与测试使用）。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Window, Surface, Card, CardHover, RowHover, Floating, FloatingBorder, EditorBg,
        OverlayHover, OverlayPressed,
        Line, LineSoft, Border,
        Accent, AccentSoft, AccentHover, AccentText, TextOnAccent,
        TabInactive, TabHover, TabHoverText,
        Text, TextStrong, TextInput, TextMuted, TextFaint,
        ButtonBg, ButtonHover, ButtonPressed, InputBg, BadgeBg,
        Ok, Warn, Error, ErrorWash, ErrorLine,
    ];

    /// <summary>把颜色键转成 WPF 资源键（<c>Card</c> → <c>Theme.Card</c>）。</summary>
    public static string ResourceKey(string key) => ResourcePrefix + key;
}