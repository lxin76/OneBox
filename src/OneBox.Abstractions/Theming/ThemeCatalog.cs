namespace OneBox.Abstractions.Theming;

/// <summary>
/// 内置主题目录。
///
/// 主题是**纯数据**：这里只定义配色，不碰任何 UI 类型。宿主负责把它刷进
/// WPF 资源（<c>ThemeManager</c>），插件侧由 <c>OneBox.Plugin.Sdk.Wpf</c> 的
/// <c>PluginTheme</c> 接收宿主下发的同一份调色板——两端用的是同一组颜色键，
/// 所以换主题时宿主窗口与各小工具的窗口会一起变色。
///
/// 默认主题是 <see cref="DefaultId"/>（深色）。未知主题 id 一律回退到默认主题，
/// 这样用户手改配置文件、或旧版本残留了一个已删掉的主题名，都不会让界面变成空白。
/// </summary>
public static class ThemeCatalog
{
    /// <summary>默认主题 id。</summary>
    public const string DefaultId = "dark";

    /// <summary>全部内置主题（顺序即设置中心里的显示顺序）。</summary>
    public static readonly IReadOnlyList<ThemePalette> All =
    [
        new ThemePalette
        {
            Id = "dark",
            DisplayName = "深色",
            Description = "默认主题：深蓝紫底色配亮蓝强调色，夜间使用不刺眼。",
            IsDark = true,
            Colors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ThemeKeys.Window] = "#16162A",
                [ThemeKeys.Surface] = "#1B1B2F",
                [ThemeKeys.Card] = "#1E1E36",
                [ThemeKeys.CardHover] = "#24243F",
                [ThemeKeys.RowHover] = "#24243C",
                [ThemeKeys.Floating] = "#F21B1B2F",
                [ThemeKeys.FloatingBorder] = "#40FFFFFF",
                [ThemeKeys.EditorBg] = "#14000000",
                [ThemeKeys.OverlayHover] = "#26FFFFFF",
                [ThemeKeys.OverlayPressed] = "#3DFFFFFF",
                [ThemeKeys.Line] = "#2E2E48",
                [ThemeKeys.LineSoft] = "#26263E",
                [ThemeKeys.Border] = "#3A3A58",
                [ThemeKeys.Accent] = "#5B8CFF",
                [ThemeKeys.AccentSoft] = "#2E3A5C",
                [ThemeKeys.AccentHover] = "#35456B",
                [ThemeKeys.AccentText] = "#9AB4FF",
                [ThemeKeys.TextOnAccent] = "#10162A",
                [ThemeKeys.TabInactive] = "#9AA0B8",
                [ThemeKeys.TabHover] = "#23233C",
                [ThemeKeys.TabHoverText] = "#D6DAE8",
                [ThemeKeys.Text] = "#E6E8F0",
                [ThemeKeys.TextStrong] = "#F4F6FC",
                [ThemeKeys.TextInput] = "#F2F2F2",
                [ThemeKeys.TextMuted] = "#8A90A6",
                [ThemeKeys.TextFaint] = "#6A7088",
                [ThemeKeys.ButtonBg] = "#2E2E48",
                [ThemeKeys.ButtonHover] = "#3A3A58",
                [ThemeKeys.ButtonPressed] = "#24243C",
                [ThemeKeys.InputBg] = "#2E2E48",
                [ThemeKeys.BadgeBg] = "#2A2A44",
                [ThemeKeys.Ok] = "#7CE38B",
                [ThemeKeys.Warn] = "#FFB454",
                [ThemeKeys.Error] = "#FF9A9A",
                [ThemeKeys.ErrorWash] = "#3A222A",
                [ThemeKeys.ErrorLine] = "#8A3A48",
            },
        },

        new ThemePalette
        {
            Id = "light",
            DisplayName = "浅色",
            Description = "白底深字，白天或明亮环境下最清晰。",
            IsDark = false,
            Colors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ThemeKeys.Window] = "#F2F4F9",
                [ThemeKeys.Surface] = "#FFFFFF",
                [ThemeKeys.Card] = "#FFFFFF",
                [ThemeKeys.CardHover] = "#EDF1F8",
                [ThemeKeys.RowHover] = "#EDF1F8",
                [ThemeKeys.Floating] = "#F2FFFFFF",
                [ThemeKeys.FloatingBorder] = "#33000000",
                [ThemeKeys.EditorBg] = "#0A000000",
                [ThemeKeys.OverlayHover] = "#14000000",
                [ThemeKeys.OverlayPressed] = "#26000000",
                [ThemeKeys.Line] = "#E1E5EE",
                [ThemeKeys.LineSoft] = "#EDF0F6",
                [ThemeKeys.Border] = "#CBD2E0",
                [ThemeKeys.Accent] = "#2F6BE4",
                [ThemeKeys.AccentSoft] = "#DCE7FB",
                [ThemeKeys.AccentHover] = "#C7D9F8",
                [ThemeKeys.AccentText] = "#1F55C0",
                [ThemeKeys.TextOnAccent] = "#FFFFFF",
                [ThemeKeys.TabInactive] = "#667085",
                [ThemeKeys.TabHover] = "#E9EDF6",
                [ThemeKeys.TabHoverText] = "#232A38",
                [ThemeKeys.Text] = "#2A3140",
                [ThemeKeys.TextStrong] = "#171D29",
                [ThemeKeys.TextInput] = "#1B2230",
                [ThemeKeys.TextMuted] = "#667085",
                [ThemeKeys.TextFaint] = "#7D879B",
                [ThemeKeys.ButtonBg] = "#FFFFFF",
                [ThemeKeys.ButtonHover] = "#EDF1F8",
                [ThemeKeys.ButtonPressed] = "#E0E6F1",
                [ThemeKeys.InputBg] = "#FFFFFF",
                [ThemeKeys.BadgeBg] = "#E7ECF6",
                [ThemeKeys.Ok] = "#1C7C43",
                [ThemeKeys.Warn] = "#A96500",
                [ThemeKeys.Error] = "#C0392B",
                [ThemeKeys.ErrorWash] = "#FCE8E6",
                [ThemeKeys.ErrorLine] = "#E5A9A1",
            },
        },

        new ThemePalette
        {
            Id = "graphite",
            DisplayName = "石墨",
            Description = "中性深灰配色，去掉蓝紫味，适合长时间盯着看。",
            IsDark = true,
            Colors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ThemeKeys.Window] = "#17181A",
                [ThemeKeys.Surface] = "#1D1E21",
                [ThemeKeys.Card] = "#222326",
                [ThemeKeys.CardHover] = "#2B2C30",
                [ThemeKeys.RowHover] = "#2B2C30",
                [ThemeKeys.Floating] = "#F21D1E21",
                [ThemeKeys.FloatingBorder] = "#40FFFFFF",
                [ThemeKeys.EditorBg] = "#14000000",
                [ThemeKeys.OverlayHover] = "#26FFFFFF",
                [ThemeKeys.OverlayPressed] = "#3DFFFFFF",
                [ThemeKeys.Line] = "#34363B",
                [ThemeKeys.LineSoft] = "#2A2C30",
                [ThemeKeys.Border] = "#3F4248",
                [ThemeKeys.Accent] = "#7FB2E5",
                [ThemeKeys.AccentSoft] = "#2C3945",
                [ThemeKeys.AccentHover] = "#38495A",
                [ThemeKeys.AccentText] = "#A9CDEC",
                [ThemeKeys.TextOnAccent] = "#10161C",
                [ThemeKeys.TabInactive] = "#9BA1A9",
                [ThemeKeys.TabHover] = "#26282C",
                [ThemeKeys.TabHoverText] = "#DCDFE3",
                [ThemeKeys.Text] = "#E2E4E7",
                [ThemeKeys.TextStrong] = "#F2F3F5",
                [ThemeKeys.TextInput] = "#EDEFF1",
                [ThemeKeys.TextMuted] = "#939AA2",
                [ThemeKeys.TextFaint] = "#6F757D",
                [ThemeKeys.ButtonBg] = "#2B2C30",
                [ThemeKeys.ButtonHover] = "#3A3C41",
                [ThemeKeys.ButtonPressed] = "#232427",
                [ThemeKeys.InputBg] = "#2B2C30",
                [ThemeKeys.BadgeBg] = "#2D2F33",
                [ThemeKeys.Ok] = "#8FD49A",
                [ThemeKeys.Warn] = "#E8B567",
                [ThemeKeys.Error] = "#EF9A9A",
                [ThemeKeys.ErrorWash] = "#3A2528",
                [ThemeKeys.ErrorLine] = "#7E474B",
            },
        },

        new ThemePalette
        {
            Id = "sepia",
            DisplayName = "暖阳",
            Description = "米黄纸张底色，低刺激的护眼配色。",
            IsDark = false,
            Colors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ThemeKeys.Window] = "#F5EFE1",
                [ThemeKeys.Surface] = "#FBF6EA",
                [ThemeKeys.Card] = "#FFFDF7",
                [ThemeKeys.CardHover] = "#F2E9D5",
                [ThemeKeys.RowHover] = "#F2E9D5",
                [ThemeKeys.Floating] = "#F2FFFDF7",
                [ThemeKeys.FloatingBorder] = "#33000000",
                [ThemeKeys.EditorBg] = "#0A000000",
                [ThemeKeys.OverlayHover] = "#14000000",
                [ThemeKeys.OverlayPressed] = "#26000000",
                [ThemeKeys.Line] = "#E3D8C1",
                [ThemeKeys.LineSoft] = "#EFE7D5",
                [ThemeKeys.Border] = "#D3C4A6",
                [ThemeKeys.Accent] = "#9A6220",
                [ThemeKeys.AccentSoft] = "#F0E3C8",
                [ThemeKeys.AccentHover] = "#EBE0C6",
                [ThemeKeys.AccentText] = "#8A5518",
                [ThemeKeys.TextOnAccent] = "#FFFDF7",
                [ThemeKeys.TabInactive] = "#7C6E58",
                [ThemeKeys.TabHover] = "#EDE4D0",
                [ThemeKeys.TabHoverText] = "#4A4030",
                [ThemeKeys.Text] = "#423828",
                [ThemeKeys.TextStrong] = "#2C2317",
                [ThemeKeys.TextInput] = "#382F21",
                [ThemeKeys.TextMuted] = "#6F6250",
                [ThemeKeys.TextFaint] = "#9B8D75",
                [ThemeKeys.ButtonBg] = "#FFFDF7",
                [ThemeKeys.ButtonHover] = "#F2E9D5",
                [ThemeKeys.ButtonPressed] = "#E8DCC2",
                [ThemeKeys.InputBg] = "#FFFDF7",
                [ThemeKeys.BadgeBg] = "#EDE3CC",
                [ThemeKeys.Ok] = "#3D7A48",
                [ThemeKeys.Warn] = "#9A6400",
                [ThemeKeys.Error] = "#A83A2E",
                [ThemeKeys.ErrorWash] = "#F7E2DA",
                [ThemeKeys.ErrorLine] = "#DCB0A4",
            },
        },
    ];

    /// <summary>默认主题。</summary>
    public static ThemePalette Default => All[0];

    /// <summary>按 id 取主题；未知或空 id 回退默认主题（id 不区分大小写）。</summary>
    public static ThemePalette Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Default;
        foreach (var theme in All)
            if (string.Equals(theme.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                return theme;
        return Default;
    }

    /// <summary>该 id 是否是已知主题。</summary>
    public static bool IsKnown(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && All.Any(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}