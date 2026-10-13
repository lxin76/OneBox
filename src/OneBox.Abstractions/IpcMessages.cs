namespace OneBox.Abstractions;

/// <summary>握手载荷（宿主 → 插件）。</summary>
public sealed class HelloPayload
{
    public int ProtocolVersion { get; set; }
    public string PluginId { get; set; } = "";
    public string HostVersion { get; set; } = "";
    /// <summary>宿主分配的数据目录（已创建），插件在此保存自己的设置与数据。</summary>
    public string DataDirectory { get; set; } = "";
    /// <summary>宿主日志目录。</summary>
    public string LogDirectory { get; set; } = "";
}

/// <summary>插件就绪载荷（插件 → 宿主）。</summary>
public sealed class ReadyPayload
{
    /// <summary>插件展示名（可与清单不同，用于界面）。</summary>
    public string? DisplayName { get; set; }
    /// <summary>状态文本，如"监听中"。</summary>
    public string? StatusText { get; set; }
    /// <summary>插件进程 id，便于宿主排障。</summary>
    public int ProcessId { get; set; }
}

/// <summary>状态更新载荷。</summary>
public sealed class StatusPayload
{
    public string Text { get; set; } = "";
}

/// <summary>日志等级。</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>日志载荷（插件 → 宿主）。</summary>
public sealed class LogPayload
{
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Message { get; set; } = "";
}

/// <summary>热键声明载荷（插件 → 宿主）。</summary>
public sealed class DeclareHotkeysPayload
{
    public List<HotkeyDeclaration> Hotkeys { get; set; } = [];
}

/// <summary>热键按下通知（宿主 → 插件）。</summary>
public sealed class HotkeyPressedPayload
{
    public string ActionId { get; set; } = "";
}

/// <summary>托盘/工具箱菜单声明载荷（插件 → 宿主）。</summary>
public sealed class DeclareTrayMenuPayload
{
    public List<TrayMenuItem> Items { get; set; } = [];
}

/// <summary>菜单项类型。</summary>
public enum TrayMenuItemKind
{
    /// <summary>可点击的普通项。</summary>
    Command,
    /// <summary>可勾选项（点击后宿主回传新的勾选状态）。</summary>
    Toggle,
    /// <summary>分隔线。</summary>
    Separator,
}

/// <summary>插件向宿主注册的托盘菜单项。</summary>
public sealed class TrayMenuItem
{
    /// <summary>插件内唯一标识，点击时回传。</summary>
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public TrayMenuItemKind Kind { get; set; } = TrayMenuItemKind.Command;
    public bool Enabled { get; set; } = true;
    public bool Checked { get; set; }
    /// <summary>提示文本（可选）。</summary>
    public string? ToolTip { get; set; }
}

/// <summary>设置项在设置中心里的编辑方式。</summary>
public enum SettingKind
{
    /// <summary>勾选框。</summary>
    Bool,
    /// <summary>单行文本。</summary>
    Text,
    /// <summary>多行文本。</summary>
    Multiline,
    /// <summary>数字（可用 Min/Max 限定范围）。</summary>
    Number,
    /// <summary>从 Choices 里单选（下拉框）。</summary>
    Choice,
    /// <summary>目录路径（带「浏览」按钮）。</summary>
    DirectoryPath,
    /// <summary>文件路径（带「浏览」按钮）。</summary>
    FilePath,
}

/// <summary>Choice 类型设置项的一个候选项。</summary>
public sealed class SettingChoice
{
    /// <summary>实际写入设置的值。</summary>
    public string Value { get; set; } = "";
    /// <summary>界面上显示的文字。</summary>
    public string Label { get; set; } = "";
}

/// <summary>
/// 一个设置项的声明。插件通过 <see cref="DeclareSettingsPayload"/> 上报，
/// 宿主据此在设置中心里为该插件生成表单。
///
/// 注意：**插件是设置值的唯一来源**。宿主只负责渲染与收集用户改动，
/// 改动通过 <see cref="SettingsChangedPayload"/> 回传插件，由插件自己落地持久化。
/// 这样不会出现"宿主存一份、插件存一份"的双份真相。
/// </summary>
public sealed class SettingDescriptor
{
    /// <summary>插件内唯一的设置键。</summary>
    public string Key { get; set; } = "";

    /// <summary>界面上显示的标题。</summary>
    public string Label { get; set; } = "";

    /// <summary>编辑方式。</summary>
    public SettingKind Kind { get; set; } = SettingKind.Text;

    /// <summary>补充说明（显示在控件下方的小字）。</summary>
    public string? Description { get; set; }

    /// <summary>分组标题；同一分组的设置项会归到同一个标题下。为空时归入「常规」。</summary>
    public string? Group { get; set; }

    /// <summary>当前值（JSON）。</summary>
    public System.Text.Json.JsonElement? Value { get; set; }

    /// <summary>默认值（JSON），用于「恢复默认」与类型回退。</summary>
    public System.Text.Json.JsonElement? DefaultValue { get; set; }

    /// <summary>Number 类型的最小值。</summary>
    public double? Min { get; set; }

    /// <summary>Number 类型的最大值。</summary>
    public double? Max { get; set; }

    /// <summary>输入框的占位提示。</summary>
    public string? Placeholder { get; set; }

    /// <summary>Choice 类型的候选项。</summary>
    public List<SettingChoice> Choices { get; set; } = [];

    /// <summary>只读展示（不可编辑），用于呈现由插件计算出的信息。</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>插件声明自己的设置项（插件 → 宿主）。</summary>
public sealed class DeclareSettingsPayload
{
    public List<SettingDescriptor> Items { get; set; } = [];
}

/// <summary>单项设置的改动。</summary>
public sealed class SettingChange
{
    public string Key { get; set; } = "";
    public System.Text.Json.JsonElement Value { get; set; }
}

/// <summary>宿主告知插件用户在设置中心改动了哪些设置（宿主 → 插件）。</summary>
public sealed class SettingsChangedPayload
{
    public List<SettingChange> Changes { get; set; } = [];
}

/// <summary>菜单点击通知（宿主 → 插件）。</summary>
public sealed class MenuInvokedPayload
{
    public string ItemId { get; set; } = "";
    /// <summary>Toggle 类型项点击后的新勾选状态。</summary>
    public bool Checked { get; set; }
}

/// <summary>读取设置请求（插件 → 宿主）。</summary>
public sealed class GetSettingPayload
{
    public string Key { get; set; } = "";
}

/// <summary>写入设置请求（插件 → 宿主）。</summary>
public sealed class SetSettingPayload
{
    public string Key { get; set; } = "";
    /// <summary>值的 JSON 表示（字符串、数字、布尔或对象）。</summary>
    public System.Text.Json.JsonElement Value { get; set; }
}

/// <summary>读取设置应答（宿主 → 插件）。</summary>
public sealed class SettingValuePayload
{
    /// <summary>键是否存在；false 表示从未写入过。</summary>
    public bool Exists { get; set; }
    public System.Text.Json.JsonElement? Value { get; set; }
}

/// <summary>通知载荷（插件 → 宿主）。</summary>
public sealed class NotifyPayload
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public LogLevel Level { get; set; } = LogLevel.Info;
}

/// <summary>通用应答载荷。</summary>
public sealed class ResultPayload
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    /// <summary>应答相关的附加数据（如热键注册结果）。</summary>
    public System.Text.Json.JsonElement? Data { get; set; }
}

/// <summary>退出通知载荷（宿主 → 插件）。</summary>
public sealed class ShutdownPayload
{
    /// <summary>退出原因，便于插件写日志。</summary>
    public string Reason { get; set; } = "";
    /// <summary>宽限时间（毫秒），超时后宿主强制结束进程。</summary>
    public int GracefulTimeoutMs { get; set; } = 5000;
}

/// <summary>热键注册结果负载（作为 ResultPayload.Data 返回）。</summary>
public sealed class HotkeyRegistrationPayload
{
    public List<HotkeyRegistrationResult> Results { get; set; } = [];
}
