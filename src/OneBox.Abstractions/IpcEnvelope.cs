using System.Text.Json;

namespace OneBox.Abstractions;

/// <summary>IPC 消息信封：type 决定如何解释 payload，requestId 用于请求-应答配对。</summary>
public sealed class IpcEnvelope
{
    /// <summary>消息类型，见 <see cref="IpcTypes"/>。</summary>
    public string Type { get; set; } = "";

    /// <summary>请求标识；应答消息会带上相同的值。单向通知为 null。</summary>
    public string? RequestId { get; set; }

    /// <summary>消息体（按 Type 反序列化为对应类型）。</summary>
    public JsonElement Payload { get; set; }

    /// <summary>构造一个携带载荷的消息。</summary>
    public static IpcEnvelope Create<T>(string type, T payload, string? requestId = null)
        => new()
        {
            Type = type,
            RequestId = requestId,
            Payload = JsonSerializer.SerializeToElement(payload, Protocol.Json),
        };

    /// <summary>构造一个不带载荷的消息。</summary>
    public static IpcEnvelope Empty(string type, string? requestId = null)
        => new()
        {
            Type = type,
            RequestId = requestId,
            Payload = JsonSerializer.SerializeToElement(new EmptyPayload(), Protocol.Json),
        };

    /// <summary>反序列化载荷；格式不符时返回 null 而不抛异常。</summary>
    public T? GetPayload<T>() where T : class
    {
        try
        {
            return Payload.Deserialize<T>(Protocol.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>占位载荷（无内容消息使用）。</summary>
public sealed class EmptyPayload;

/// <summary>协议消息类型常量。</summary>
public static class IpcTypes
{
    // 宿主 → 插件
    /// <summary>握手：告知插件协议版本与宿主分配的数据目录。</summary>
    public const string Hello = "hello";
    /// <summary>宿主已就绪，插件可开始工作。</summary>
    public const string Start = "start";
    /// <summary>插件声明的热键被按下。</summary>
    public const string HotkeyPressed = "hotkeyPressed";
    /// <summary>用户在托盘/工具箱点击了插件注册的菜单项。</summary>
    public const string MenuInvoked = "menuInvoked";
    /// <summary>用户在设置中心改动了本插件的设置，插件应据此更新并持久化。</summary>
    public const string SettingsChanged = "settingsChanged";
    /// <summary>插件注册的菜单项需要刷新（插件可主动请求宿主重绘）。</summary>
    public const string TrayRefresh = "trayRefresh";
    /// <summary>宿主即将退出，插件应在限定时间内自行退出。</summary>
    public const string Shutdown = "shutdown";
    /// <summary>连通性探测。</summary>
    public const string Ping = "ping";

    // 插件 → 宿主
    /// <summary>插件启动完成，上报展示信息与状态。</summary>
    public const string Ready = "ready";
    /// <summary>插件状态文本变化（显示在工具箱列表）。</summary>
    public const string Status = "status";
    /// <summary>上报一条日志。</summary>
    public const string Log = "log";
    /// <summary>声明/更新插件的全局热键需求。</summary>
    public const string DeclareHotkeys = "declareHotkeys";
    /// <summary>声明/更新插件的托盘菜单项。</summary>
    public const string DeclareTrayMenu = "declareTrayMenu";
    /// <summary>声明/更新插件在设置中心里暴露的设置项。</summary>
    public const string DeclareSettings = "declareSettings";
    /// <summary>读取插件私有设置。</summary>
    public const string GetSetting = "getSetting";
    /// <summary>写入插件私有设置。</summary>
    public const string SetSetting = "setSetting";
    /// <summary>请求宿主显示一条托盘气泡通知。</summary>
    public const string Notify = "notify";
    /// <summary>插件请求退出（宿主不再自动重启）。</summary>
    public const string RequestShutdown = "requestShutdown";
    /// <summary>插件请求重启自己。</summary>
    public const string RequestRestart = "requestRestart";
    /// <summary>连通性探测应答。</summary>
    public const string Pong = "pong";

    // 通用应答
    /// <summary>请求成功应答。</summary>
    public const string Ok = "ok";
    /// <summary>请求失败应答。</summary>
    public const string Error = "error";
}
