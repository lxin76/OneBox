namespace OneBox.Host.Services;

/// <summary>
/// 宿主全局热键注册的抽象。抽出接口是为了让插件管理器能在没有真实窗口的
/// 环境（单元测试）里驱动，同时把 Win32 调用集中在一处。
/// </summary>
public interface IHotkeyRegistrar : IDisposable
{
    /// <summary>注册热键；返回失败原因（成功时返回 null）。</summary>
    string? Register(string combination, Action callback);

    /// <summary>注销本注册器持有的全部热键。</summary>
    void UnregisterAll();
}
